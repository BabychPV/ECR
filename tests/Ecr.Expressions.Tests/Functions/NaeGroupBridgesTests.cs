// tests/Ecr.Expressions.Tests/Functions/NaeGroupBridgesTests.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Functions;

/// <summary>
/// LP-10: похідні аргументи методологій Land зі словників C#. Береться ТОЙ САМИЙ текст моста (Formula-колонка шаблону)
/// й ТІ САМІ записи довідника <c>LAND_NAE_GROUP</c>, що згенеровані в моделі Land (<c>land-template.model.json</c>);
/// значень прода тут немає — лише коди NAE і номери груп зі старого коду.
/// </summary>
/// <remarks>
/// Доводяться правила старої системи: група потужності (<c>ECW_C11_03</c>, лише 9a_17) і <c>Category1</c>
/// (<c>ECW_C11_11_02</c>, NAE зі знятими пробілами); невідомий NAE - <c>#N/A</c> (рядок пропущено), а не 0.
/// Мутаційні докази (лише цей клас): у <c>nae_group_bridges.py</c> прибрати запис із пробілом перед дужкою
/// (<c>_spaced</c>) і перегенерувати модель — червоніє <see cref="NAE_з_пробілом_перед_дужкою_знаходить_ту_саму_групу"/>;
/// поміняти місцями дві групи потужності в <c>af-land-nae-groups.json</c> — червоніє
/// <see cref="Група_потужності_береться_з_NAE_а_невідомий_NAE_дає_NA"/>.
/// </remarks>
public sealed class NaeGroupBridgesTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Група_потужності_береться_з_NAE_а_невідомий_NAE_дає_NA()
    {
        var snapshot = Arrange();
        var expression = Bridge("T9a_17", "Land_Power");

        Assert.Equal(440m, Eval(expression, snapshot, "0114").AsNumber());
        Assert.Equal(9.9m, Eval(expression, snapshot, "0801").AsNumber());
        Assert.Equal(5420m, Eval(expression, snapshot, "0664").AsNumber());
        Assert.Equal(272m, Eval(expression, snapshot, "0924").AsNumber());

        // Стара система: невідомий NAE -> «Error Power value», рядок пропущено; тут - #N/A, а не нуль.
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(expression, snapshot, "9999").ErrorCode);

        // NAE з іншого словника (Category1): групи потужності немає - порожньо, аргумент не обчислено.
        Assert.True(Eval(expression, snapshot, "0788").IsNull);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void NAE_з_пробілом_перед_дужкою_знаходить_ту_саму_групу()
    {
        var snapshot = Arrange();
        var expression = Bridge("T9_01", "Category1");

        Assert.Equal("0788", Eval(expression, snapshot, "0788(001)").Value);
        Assert.Equal("0788", Eval(expression, snapshot, "0788 (001)").Value);
        Assert.Equal("0788", Eval(expression, snapshot, "0791 (001)").Value);
        Assert.Equal("6975", Eval(expression, snapshot, "6976").Value);
        Assert.Equal("6975", Eval(expression, snapshot, "6986").Value);
        Assert.Equal("0560", Eval(expression, snapshot, "0903").Value);
        Assert.Equal("0793", Eval(expression, snapshot, "0883").Value);

        // Невідомий NAE: «Error category1 - NAE», рядок пропущено -> #N/A.
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(expression, snapshot, "0788(002)").ErrorCode);
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(expression, snapshot, "9999").ErrorCode);

        // NAE зі словника потужності (C11_03): Category1 немає - порожньо, аргумент не обчислено, рядок без результату.
        Assert.True(Eval(expression, snapshot, "0114").IsNull);

        // Той самий міст на всіх шести таблицях Category1 - один вираз із власною колонкою NAE таблиці.
        foreach (var table in new[] { "T9_01", "T9_07", "T9_08", "T9a_16", "T9a_17a", "T9a_19" })
        {
            Assert.Contains("REGFIND('LAND_NAE_GROUP', [", Bridge(table, "Category1"), StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Ключ_довідника_нормалізується_рушієм_лише_за_краями_і_послідовностями_пробілів()
    {
        // Чому в довіднику ДВА написання: 0788(001) і 0788 (001) - різні канонічні ключі, а зайві пробіли по краях і
        // подвійні пробіли рушій знімає сам (RegistryKeyNormalizer) - окремі записи на них не потрібні.
        string? Canon(string s) => RegistryKeyNormalizer.Canonical([new RegistryKeyPart(CellDataType.String, s)]);

        Assert.NotEqual(Canon("0788(001)"), Canon("0788 (001)"));
        Assert.Equal(Canon("0788 (001)"), Canon("  0788  (001) "));

        // Пробіл усередині коду рушій не знімає (стара система - знімає): такий NAE недоведений і дає #N/A.
        Assert.NotEqual(Canon("0788(001)"), Canon("07 88(001)"));
    }

    /// <summary>
    /// RCN-1: старий код (ECW_C11_11_02) знімає з NAE усі пробіли, тож будь-який з 8 розташувань одного пробілу навколо
    /// дужок, а також регістр, NBSP, таб і довгі послідовності пробілів, мають давати ту саму групу на справжньому рушії
    /// з довідником із моделі. Мутація: повернути в <c>nae_group_bridges.py</c> лише два написання - червоніє.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("0788(001)")]
    [InlineData("0788 (001)")]
    [InlineData("0788( 001)")]
    [InlineData("0788(001 )")]
    [InlineData("0788 ( 001)")]
    [InlineData("0788 (001 )")]
    [InlineData("0788( 001 )")]
    [InlineData("0788 ( 001 )")]
    [InlineData("0791 ( 001 )")]
    public void NAE_з_будь_яким_одним_пробілом_навколо_дужок_знаходить_групу(string nae)
    {
        var snapshot = Arrange();
        var expression = Bridge("T9_01", "Category1");

        Assert.Equal("0788", Eval(expression, snapshot, nae).Value);
    }

    /// <summary>
    /// Брудні написання (краї, послідовності, NBSP, таб, регістр) зводить до запису довідника сам
    /// <see cref="RegistryKeyNormalizer"/> (знімок прода шукає за канонічним ключем): канон брудного написання мусить
    /// збігатися з каноном одного з ключів довідника моделі.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("  0788   (001)  ")]
    [InlineData("0788 (001)")]
    [InlineData("0788\t(001)")]
    [InlineData("0788 ( 001 )")]
    public void Брудне_написання_NAE_має_канон_ключа_довідника(string nae)
    {
        string? Canon(string s) => RegistryKeyNormalizer.Canonical([new RegistryKeyPart(CellDataType.String, s)]);

        using var model = LoadModel();
        var keys = model.RootElement.GetProperty("registries").EnumerateArray()
            .Single(r => r.GetProperty("code").GetString() == "LAND_NAE_GROUP")
            .GetProperty("entries").EnumerateArray()
            .Where(e => e.GetProperty("fields").TryGetProperty("CATEGORY1", out var c) && c.GetString() == "0788")
            .Select(e => Canon(e.GetProperty("fields").GetProperty("NAE_NORMALIZED").GetString()!))
            .ToHashSet();

        Assert.Contains(Canon(nae), keys);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Недоведене_написання_NAE_дає_NA_а_не_хибну_групу()
    {
        var snapshot = Arrange();
        var expression = Bridge("T9_01", "Category1");

        // Пробіл усередині 4-значного коду рушій не знімає (стара система знімає): #N/A, рядок без результату.
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(expression, snapshot, "07 88").ErrorCode);
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(expression, snapshot, "0788 (0 01)").ErrorCode);

        // Інший код у дужках - не з довідника.
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(expression, snapshot, "0788 ( 002 )").ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Фрагмент_Land_MassFraction_вибирає_аргумент_за_речовиною_проходу()
    {
        var spec = MassFraction();
        var snapshot = Arrange();

        Assert.True(Expr.Parse(spec.Fragment, ExpressionDialect.Methodology).IsSuccess);
        Assert.Equal(
            ["Land_MassFraction_0415", "Land_MassFraction_0416", "Land_MassFraction_0602", "Land_MassFraction_0616", "Land_MassFraction_0621"],
            spec.Arguments);

        var arguments = new Dictionary<string, ExpressionValue>();
        decimal value = 0.1m;
        foreach (var argument in spec.Arguments)
        {
            arguments[argument] = ExpressionValue.Number(value);
            value += 0.1m;
        }

        decimal? For(string substance) => EvalFragment(spec.Fragment, snapshot, substance, arguments).AsNumber();

        // Одна й та сама колонка-аргумент для різних речовин того самого рядка: різні значення.
        Assert.Equal(0.1m, For("415"));
        Assert.Equal(0.3m, For("602"));
        Assert.Equal(0.5m, For("621"));
        Assert.NotEqual(For("415"), For("621"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Речовина_поза_type1_у_фрагменті_Land_MassFraction_дає_NA_а_не_нуль()
    {
        var spec = MassFraction();
        var snapshot = Arrange();
        var arguments = spec.Arguments.ToDictionary(a => a, _ => ExpressionValue.Number(0.1m));

        // type2 (333) і type3 (1716): старий код аргумент не виставляє, формули цих типів його не читають.
        Assert.Equal(ExpressionErrors.NotAvailable, EvalFragment(spec.Fragment, snapshot, "333", arguments).ErrorCode);
        Assert.Equal(ExpressionErrors.NotAvailable, EvalFragment(spec.Fragment, snapshot, "1716", arguments).ErrorCode);
    }

    private static ExpressionValue Eval(string expression, InMemoryRegistrySnapshot snapshot, string nae)
    {
        var inner = new TestEvaluationContext();
        inner.SetCell("S", "T", string.Empty, NaeColumn(expression), ExpressionValue.Text(nae));
        return Expr.Eval(expression, snapshot.Attach(inner), ExpressionDialect.Template);
    }

    private static ExpressionValue EvalFragment(
        string fragment, InMemoryRegistrySnapshot snapshot, string substance, Dictionary<string, ExpressionValue> arguments)
    {
        var inner = new TestEvaluationContext { CurrentSubstanceCode = substance };
        foreach (var (name, value) in arguments)
        {
            inner.Arguments[name] = value;
        }

        return Expr.Eval(fragment, snapshot.Attach(inner), ExpressionDialect.Methodology);
    }

    /// <summary>Колонка NAE, яку читає міст: єдиний [Токен] виразу.</summary>
    private static string NaeColumn(string expression)
    {
        var start = expression.IndexOf('[', StringComparison.Ordinal) + 1;
        return expression[start..expression.IndexOf(']', start)];
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

    private static string Bridge(string table, string column)
    {
        using var model = LoadModel();
        var t = model.RootElement.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .First(x => x.GetProperty("code").GetString() == table);
        return t.GetProperty("columns").EnumerateArray()
            .First(c => c.GetProperty("code").GetString() == column).GetProperty("expression").GetString()!;
    }

    private static Spec MassFraction()
    {
        using var model = LoadModel();
        var m = model.RootElement.GetProperty("argumentBridges").GetProperty("massFraction")
            .GetProperty("methodologies").GetProperty("ECW_C11_11_04");
        return new Spec(
            m.GetProperty("fragment").GetString()!,
            m.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()!).ToArray());
    }

    /// <summary>Довідники з моделі Land: ті самі записи, що створить етап Registries (поле без значення - null).</summary>
    private static InMemoryRegistrySnapshot Arrange()
    {
        var snapshot = new InMemoryRegistrySnapshot()
            .AddRegistry("LAND_NAE_GROUP", ["NAE_NORMALIZED", "POWER_GROUP", "CATEGORY1"], primaryKey: ["NAE_NORMALIZED"])
            .AddRegistry("LAND_HSE400_SUBSTANCE", ["SUBSTANCE_CODE", "INDICATOR"], primaryKey: ["SUBSTANCE_CODE"]);

        using var model = LoadModel();
        long id = 100;
        foreach (var registry in model.RootElement.GetProperty("registries").EnumerateArray())
        {
            var code = registry.GetProperty("code").GetString()!;
            if (code is not ("LAND_NAE_GROUP" or "LAND_HSE400_SUBSTANCE"))
            {
                continue;
            }

            foreach (var entry in registry.GetProperty("entries").EnumerateArray())
            {
                var values = entry.GetProperty("fields").EnumerateObject().ToDictionary(
                    f => f.Name,
                    f => f.Name == "POWER_GROUP"
                        ? ExpressionValue.Number(decimal.Parse(f.Value.GetString()!, CultureInfo.InvariantCulture))
                        : ExpressionValue.Text(f.Value.GetString()!));
                snapshot.AddEntry(code, ++id, entry.GetProperty("code").GetString()!, values, ordinal: (int)id);
            }
        }

        return snapshot;
    }
}
