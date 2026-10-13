// tests/Ecr.Expressions.Tests/Functions/CompositionMassBySubstanceTests.cs
using System.Text.Json;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Functions;

/// <summary>
/// LP-10: склад газу HSE400 ПО РЕЧОВИНІ. Береться ТОЙ САМИЙ текст фрагмента й ТІ САМІ довідники (потоки, словник
/// «код речовини → показник»), що згенеровані в моделі Land (<c>land-template.model.json</c>,
/// <c>argumentBridges.hse400.compositionMass</c>); числа синтетичні, прод-даних тут немає.
/// </summary>
/// <remarks>
/// Доводиться правило старої системи (<c>PollutionCodeService.GetMassName</c> + <c>continue</c> без складу):
/// <list type="bullet">
/// <item>той самий рядок, потік і місяць, різні речовини проходу → РІЗНІ показники (H2S_mass, EMercaptan_mass);</item>
/// <item>код поза словником (Unknown_mass) і код без запису складу → <c>#N/A</c>, а не 0;</item>
/// <item>код запису довідника речовин (<c>E333</c>) ≠ <c>SUBSTANCE_CODE</c> (<c>333</c>): REGFIND по довіднику без
/// первинного ключа шукає за кодом запису, тому ключ словника показників - саме SUBSTANCE_CODE.</item>
/// </list>
/// Мутаційні докази (лише цей клас): у <c>hse400_bridges.py</c> поміняти місцями показники 333 і 1728 і
/// перегенерувати модель — червоніє <see cref="Дві_речовини_того_самого_рядка_дають_різні_показники"/>; прибрати
/// <c>CURRENT_SUBSTANCE()</c> із фрагмента — червоніє він же та <see cref="Фрагмент_розбирається_і_читає_речовину_проходу"/>.
/// </remarks>
public sealed class CompositionMassBySubstanceTests
{
    private const string FuelGasInternal = "Fuel Gas (Internal) - Топливный газ (Внутренний)";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Фрагмент_розбирається_і_читає_речовину_проходу()
    {
        var methodologies = Methodologies();
        Assert.Equal(["ECW_C11_01", "ECW_C11_02", "ECW_C11_10", "ECW_C12_01", "ECW_C12_03"], methodologies.Keys.Order().ToArray());

        foreach (var (name, spec) in methodologies)
        {
            Assert.True(Expr.Parse(spec.Fragment, ExpressionDialect.Methodology).IsSuccess, name);
            Assert.Contains("CURRENT_SUBSTANCE()", spec.Fragment, StringComparison.Ordinal);
            Assert.Contains("'LAND_HSE400_SUBSTANCE'", spec.Fragment, StringComparison.Ordinal);
            foreach (var argument in spec.Arguments)
            {
                Assert.Contains("@" + argument, spec.Fragment, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Дві_речовини_того_самого_рядка_дають_різні_показники()
    {
        var snapshot = Arrange();
        var fragment = Methodologies()["ECW_C11_01"].Fragment;

        decimal? For(string substance) =>
            Eval(fragment, snapshot, substance, new() { ["Land_TypeFuelOrStream"] = ExpressionValue.Text(FuelGasInternal) }).AsNumber();

        // 333 -> H2S_mass, 1728 -> EMercaptan_mass: хімічні назви колонок, не код речовини.
        Assert.Equal(0.02m, For("333"));
        Assert.Equal(0.0007m, For("1728"));
        Assert.NotEqual(For("333"), For("1728"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Речовина_поза_словником_і_речовина_без_запису_складу_дають_NA_а_не_нуль()
    {
        var snapshot = Arrange();
        var fragment = Methodologies()["ECW_C11_01"].Fragment;
        var arguments = new Dictionary<string, ExpressionValue> { ["Land_TypeFuelOrStream"] = ExpressionValue.Text(FuelGasInternal) };

        // 1716 немає у PollutionCodeService (Unknown_mass): колонки складу немає.
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(fragment, snapshot, "1716", arguments).ErrorCode);

        // 334 у словнику (CS2_mass), але запису складу для цього потоку й місяця немає.
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(fragment, snapshot, "334", arguments).ErrorCode);

        // Інший місяць - запису немає (стара система не мала складу): теж #N/A.
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(fragment, snapshot, "333", arguments, month: 4).ErrorCode);

        // Невідомий текст потоку (Diesel не входить у словник префіксів).
        arguments["Land_TypeFuelOrStream"] = ExpressionValue.Text("Diesel - Дизель");
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(fragment, snapshot, "333", arguments).ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Код_запису_довідника_речовин_не_є_SUBSTANCE_CODE_тому_ключ_словника_саме_SUBSTANCE_CODE()
    {
        var snapshot = Arrange();

        // LAND_AIR_SUBSTANCE: код запису E333, SUBSTANCE_CODE = 333, первинного ключа в описі немає.
        var byNumber = Expr.Eval("REGFIND('LAND_AIR_SUBSTANCE', '333')", snapshot.Attach(new TestEvaluationContext()), ExpressionDialect.Methodology);
        var byEntryCode = Expr.Eval("REGFIND('LAND_AIR_SUBSTANCE', 'E333')", snapshot.Attach(new TestEvaluationContext()), ExpressionDialect.Methodology);
        Assert.Equal(ExpressionErrors.NotAvailable, byNumber.ErrorCode);
        Assert.Equal(7001m, byEntryCode.AsNumber());

        // Словник показників - з первинним ключем SUBSTANCE_CODE: '333' знаходиться.
        var indicator = Expr.Eval(
            "REGFIELD(REGFIND('LAND_HSE400_SUBSTANCE', '333'), 'INDICATOR')",
            snapshot.Attach(new TestEvaluationContext()),
            ExpressionDialect.Methodology);
        Assert.Equal("H2S_mass", indicator.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Потік_C12_01_із_Land_Streams_і_C12_03_із_Lookup_палива()
    {
        var snapshot = Arrange();

        // C12_01: у словнику старої системи єдиний ключ FuelGas -> FG_Internal; інші потоки складу не мають.
        var c1201 = Methodologies()["ECW_C12_01"].Fragment;
        Assert.Equal(0.02m, Eval(c1201, snapshot, "333", new() { ["Land_Streams"] = ExpressionValue.Text("Fuel gas") }).AsNumber());
        Assert.Equal(
            ExpressionErrors.NotAvailable,
            Eval(c1201, snapshot, "333", new() { ["Land_Streams"] = ExpressionValue.Text("Diesel") }).ErrorCode);

        // C12_03: Land_TypeFuel - Lookup; текст палива - назва запису довідника палива.
        var c1203 = Methodologies()["ECW_C12_03"].Fragment;
        Assert.Equal(0.5m, Eval(c1203, snapshot, "333", new() { ["Land_TypeFuel"] = ExpressionValue.Number(8001) }).AsNumber());
    }

    private static ExpressionValue Eval(
        string fragment, InMemoryRegistrySnapshot snapshot, string substance, Dictionary<string, ExpressionValue> arguments, int month = 2)
    {
        var inner = new TestEvaluationContext
        {
            CurrentSubstanceCode = substance,
            Period = new PeriodContext(new DateOnly(2026, month, 1), new DateOnly(2026, month, 28), CalendarMode.Actual, 2026, (byte)month),
        };
        foreach (var (name, value) in arguments)
        {
            inner.Arguments[name] = value;
        }

        return Expr.Eval(fragment, snapshot.Attach(inner), ExpressionDialect.Methodology);
    }

    private sealed record Spec(string Fragment, string[] Arguments);

    private static JsonDocument LoadModel()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Ecr.sln")))
        {
            root = root.Parent;
        }

        return root?.FullName ?? throw new InvalidOperationException("Не знайдено Ecr.sln від каталогу збірки вгору.");
    }

    private static Dictionary<string, Spec> Methodologies()
    {
        using var model = LoadModel();
        return model.RootElement.GetProperty("argumentBridges").GetProperty("hse400").GetProperty("compositionMass")
            .GetProperty("methodologies").EnumerateObject()
            .ToDictionary(
                p => p.Name,
                p => new Spec(
                    p.Value.GetProperty("fragment").GetString()!,
                    p.Value.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()!).ToArray()));
    }

    private static InMemoryRegistrySnapshot Arrange()
    {
        var snapshot = new InMemoryRegistrySnapshot()
            .AddRegistry("LAND_HSE400_STREAM", ["TEXT", "PREFIX"], primaryKey: ["TEXT"])
            .AddRegistry("LAND_HSE400_SUBSTANCE", ["SUBSTANCE_CODE", "INDICATOR"], primaryKey: ["SUBSTANCE_CODE"])
            .AddRegistry("LAND_HSE400_COMPOSITION", ["YEAR", "MONTH", "STREAM", "INDICATOR", "VALUE"], primaryKey: ["YEAR", "MONTH", "STREAM", "INDICATOR"])
            .AddRegistry("LAND_AIR_SUBSTANCE", ["SUBSTANCE_CODE"])
            .AddRegistry("LAND_FUEL", ["NAME"]);

        // Довідники потоку й речовин - з моделі Land (ті самі записи, що створить етап Registries).
        using var model = LoadModel();
        long id = 100;
        foreach (var registry in model.RootElement.GetProperty("registries").EnumerateArray())
        {
            var code = registry.GetProperty("code").GetString()!;
            if (code is not ("LAND_HSE400_STREAM" or "LAND_HSE400_SUBSTANCE"))
            {
                continue;
            }

            foreach (var entry in registry.GetProperty("entries").EnumerateArray())
            {
                var values = entry.GetProperty("fields").EnumerateObject()
                    .ToDictionary(f => f.Name, f => ExpressionValue.Text(f.Value.GetString()!));
                snapshot.AddEntry(code, ++id, entry.GetProperty("code").GetString()!, values, ordinal: (int)id);
            }
        }

        // Склад (синтетичні числа): лютий 2026, потоки FG_Internal і LPG_HMB.
        void Add(int month, string stream, string indicator, decimal value)
            => snapshot.AddEntry(
                "LAND_HSE400_COMPOSITION",
                ++id,
                $"H{id}",
                new()
                {
                    ["YEAR"] = ExpressionValue.Number(2026),
                    ["MONTH"] = ExpressionValue.Number(month),
                    ["STREAM"] = ExpressionValue.Text(stream),
                    ["INDICATOR"] = ExpressionValue.Text(indicator),
                    ["VALUE"] = ExpressionValue.Number(value),
                },
                ordinal: (int)id);

        Add(2, "FG_Internal", "H2S_mass", 0.02m);
        Add(2, "FG_Internal", "EMercaptan_mass", 0.0007m);
        Add(2, "LPG_HMB", "H2S_mass", 0.5m);

        // Довідник речовин без первинного ключа: код запису E333, номер лежить у SUBSTANCE_CODE.
        snapshot.AddEntry(
            "LAND_AIR_SUBSTANCE", 7001, "E333", new() { ["SUBSTANCE_CODE"] = ExpressionValue.Text("333") }, ordinal: 1);

        // Довідник палива шаблону (Lookup-значення Land_TypeFuel): назва запису.
        snapshot.AddEntry("LAND_FUEL", 8001, "LPGHMB", new() { ["NAME"] = ExpressionValue.Text("LPG (HMB) - СУГ (МТБ)") }, ordinal: 1);
        return snapshot;
    }
}
