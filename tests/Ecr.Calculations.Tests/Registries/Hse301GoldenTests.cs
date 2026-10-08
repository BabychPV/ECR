// tests/Ecr.Calculations.Tests/Registries/Hse301GoldenTests.cs
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests.Registries;

/// <summary>
/// Золотий тест форми 301 (FEATURE-REGISTRY-TABLES AC-1, крок RT-23a): методологія
/// <c>Strict</c> із формулами §5.6 читає склад потоку зі ЗНІМКА довідників і дає числа
/// файлу <c>hse301.xlsx</c> (<c>AI_Int_SG_V8</c>, подія 1).
/// </summary>
/// <remarks>
/// ⚠ Дані — з фікстури <c>flert-1d2-370-winter.json</c> (склад кейсу <c>1D-2 / 370 Winter</c>,
/// молярні маси HYSYS, <c>N_C</c>/<c>N_S</c> з підписів компонентів), а не з коду тесту:
/// правка очікування має бути видимою в диффі даних.
///
/// ⛔ Формули й точність не підганяються (AC-1). Константи — за рішенням <c>D-192</c>
/// (<c>M_S = 32.064</c>, <c>M_CO2 = 44.00</c>, <c>EF = Round(…, 3)</c>). Розбір
/// <c>excel-analysis.md</c> брав 44.01 і давав <c>EF_RAW = 2.08743</c>; з 44.00 сире значення —
/// <c>2.0869542</c>, округлене — те саме <c>2.087</c> файлу.
///
/// Мутаційні докази (§9.2): передавати <c>EntryRef</c> і в <c>Legacy</c>
/// (<c>CalculationInputBuilder</c>) — <see cref="Legacy_побітно_як_до_кроку"/> червоний;
/// шлях <c>ROW.COMPONENT.MW</c> читає <c>MW</c> не компонента, а самого рядка кейсу
/// (<c>RegistryForms.RowField</c> бере лише останній сегмент) — <see cref="Hse301_MU"/> червоний.
/// </remarks>
public sealed class Hse301GoldenTests(Xunit.Abstractions.ITestOutputHelper log)
{
    private const int MethodologyId = 301;
    private const int VersionId = 3011;
    private const int SecondMethodologyId = 302;
    private const int SecondVersionId = 3021;
    private const long DocumentId = 700;
    private const long TableInstance = 500;
    private const long SecondTableInstance = 501;
    private const int TemplateVersion = 3;
    private const int Period = 202601;

    private const int StreamDef = 11;
    private const int StreamCaseDef = 12;
    private const int ComponentDef = 13;
    private const int GasCompositionDef = 14;

    private const long Stream1D2 = 1;
    private const long Winter = 101;

    private const int KilogramUnit = 1;
    private const int TonneUnit = 8;
    private const int GramUnit = 9;
    private const int GramPerSecondUnit = 20;
    private const int GramPerMoleUnit = 40;
    private const int PercentByMassUnit = 41;
    private const int TonnePerTonneUnit = 42;

    private const int StreamColumn = 31;
    private const int CaseColumn = 32;
    private const int VolumeColumn = 33;
    private const int DurationColumn = 34;
    private const int DensityColumn = 35;

    /// <summary>Останній день періоду 202601 — бізнес-дата знімка (§5.7).</summary>
    private static readonly DateOnly BusinessDate = new(2026, 1, 31);

    private static readonly JsonElement Fixture = LoadFixture();

    /// <summary>Формули §5.6 (порядок обчислення — як у публікації).</summary>
    private static readonly (string Code, string Expression)[] FormulaTexts =
    [
        ("CASE", "REGFIND('STREAM_CASE', @Stream, @HmbCase)"),
        ("MU", "REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE, ROW.MOL_PCT * ROW.COMPONENT.MW) / 100"),
        ("S_WT", "CST.M_S * REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE, ROW.COMPONENT.N_S * ROW.MOL_PCT) / !MU"),
        ("EF_RAW", "CST.M_CO2 * REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE, ROW.COMPONENT.N_C * ROW.MOL_PCT) / 100 / !MU"),
        ("EF", "Round(!EF_RAW, 3)"),
        ("M", "CONVERT(@Volume * @Density, 'kg', 't')"),
        ("SO2", "CST.K_SO2 * !M * !S_WT * CST.ETA"),
        ("SO2_GS", "CONVERT(!SO2, 't', 'g') / @Duration"),
        ("CO2_GHG", "!M * !EF * CST.OX"),
    ];

    // ——— AC-1: числа файлу ———

    /// <remarks>
    /// μ = 23.0544679165262 (<c>C26</c>) з допуском 1e-9 відносно — жорсткіше за ±1e-7 з
    /// DoD кроку. Мутація «<c>ROW.COMPONENT.MW</c> → <c>MW</c> кейсу» дає <c>#REF</c>
    /// (у рядка складу поля <c>MW</c> немає), і виходу <c>MU</c> немає зовсім.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.18")]
    public async Task Hse301_MU()
    {
        var stand = new Stand();

        var output = await stand.Module.ExecuteAsync(stand.Input(NumericMode.Strict), CancellationToken.None);

        AssertRelative(Expected("MU"), Value(output, "MU"), 1e-9m);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.18")]
    public async Task Hse301_сірка_і_коефіцієнт_CO2()
    {
        var stand = new Stand();

        var output = await stand.Module.ExecuteAsync(stand.Input(NumericMode.Strict), CancellationToken.None);

        // S = 17.2965446772043 мас.% (C27) — перехід ROW.COMPONENT.N_S.
        AssertRelative(Expected("S_WT"), Value(output, "S_WT"), 1e-9m);

        // EF: сире — з K = 44.00 (D-192), у звіті — Round(…, 3) = 2.087 (C103) ТОЧНО.
        Assert.Equal(Expected("EF_RAW"), Round(Value(output, "EF_RAW"), Digits("EF_RAW")));
        Assert.Equal(Expected("EF"), Value(output, "EF"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.18")]
    public async Task Hse301_приклад_A_маса_SO2_і_CO2()
    {
        var stand = new Stand();

        var output = await stand.Module.ExecuteAsync(stand.Input(NumericMode.Strict), CancellationToken.None);

        // M = 269.258 · 0.9589 кг → т (C104); SO2 (C112) і г/с (C129); CO2 — з EF,
        // ОКРУГЛЕНИМ до 2.087 (C105). Файл показує ці числа з 7 і 3 знаками.
        foreach (var code in new[] { "M", "SO2", "SO2_GS", "CO2_GHG" })
        {
            Assert.Equal(Expected(code), Round(Value(output, code), Digits(code)));
        }

        // Повні числа рушія — у вивід тесту: з ними звіряють звіт кроку.
        foreach (var value in output.Values)
        {
            log.WriteLine($"{value.OutputCode} = {value.Value}");
        }
    }

    // ——— D-162: знімок до прогону ———

    /// <remarks>
    /// ⛔ Знімок і довідник одиниць — у підготовці прив'язки; сам рядок не звертається
    /// до жодного сховища. Мутація «вантажити знімок в <c>ExecuteAsync</c>» робить тест
    /// червоним: завантажувач отримав би виклик після очищення лічильників.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.18")]
    public async Task Під_час_обчислення_жодного_звернення_до_сховищ()
    {
        var stand = new Stand();
        var input = stand.Input(NumericMode.Strict);

        var binding = await stand.Module.PrepareAsync(
            input.Methodology, DocumentId, new PeriodKey(Period), CancellationToken.None);

        // Довідники — ті, що формули називають літералом; COMPONENT і STREAM дочитує
        // сам завантажувач (цілі Lookup). Дата — кінець періоду, момент — поточний
        // (одиночний виклик, прогону немає).
        await stand.Loader.Received(1).LoadAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { StreamCaseDef, GasCompositionDef })),
            BusinessDate,
            null,
            Arg.Any<CancellationToken>());

        stand.ClearReceivedCalls();

        var output = await stand.Module.ExecuteAsync(binding, input, CancellationToken.None);

        Assert.Empty(stand.Methodologies.ReceivedCalls());
        Assert.Empty(stand.RegistryStore.ReceivedCalls());
        Assert.Empty(stand.Loader.ReceivedCalls());
        Assert.Empty(stand.Units.ReceivedCalls());
        Assert.Empty(stand.Periods.ReceivedCalls());
        Assert.Empty(stand.Bindings.ReceivedCalls());

        // …і обчислення справді читало довідник: інакше «жодного звернення» було б
        // правдою про формули, які нічого не порахували.
        AssertRelative(Expected("MU"), Value(output, "MU"), 1e-9m);
        Assert.True(stand.Snapshot.FieldReads > 0);
    }

    /// <remarks>
    /// Два методології прогону — дві паралельні гілки з власними scope й модулями
    /// (<c>Q-249</c>). Знімок той самий: один виклик завантажувача на прогін, момент —
    /// <c>RegistryAsOfUtc</c> прогону (§5.7, AC-7). Входи — зі справжнього
    /// <see cref="CalculationInputBuilder"/>: <c>Lookup</c>-комірка потоку стає
    /// <c>EntryRef</c>, і <c>REGFIND</c> знаходить кейс.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.18")]
    public async Task Прогін_читає_знімок_один_раз_на_момент_прогону()
    {
        var asOf = new DateTime(2026, 2, 3, 10, 15, 30, 123, DateTimeKind.Utc);
        var stand = new Stand();
        var written = new List<CalculationOutput>();
        using var provider = stand.Services(written);

        var orchestrator = new CalculationOrchestrator(
            provider.GetRequiredService<MethodologyResolver>(),
            stand.Periods,
            provider.GetRequiredService<IServiceScopeFactory>());

        var profile = await orchestrator.RunAsync(
            calculationRunId: 1,
            DocumentId,
            new PeriodKey(Period),
            [
                new CalculationBindingRef(TableInstance, MethodologyId),
                new CalculationBindingRef(SecondTableInstance, SecondMethodologyId),
            ],
            NoOpProgress.Instance,
            asOf,
            CancellationToken.None);

        Assert.Equal(2, profile.Stats.Sum(s => s.Rows));
        Assert.Equal(2, written.Count);
        Assert.All(written, output => AssertRelative(Expected("MU"), Value(output, "MU"), 1e-9m));

        await stand.Loader.Received(1).LoadAsync(
            Arg.Any<IReadOnlyCollection<int>>(), BusinessDate, asOf, Arg.Any<CancellationToken>());
    }

    // ——— D-161: EntryRef лише в Strict ———

    /// <remarks>
    /// ⛔ <c>Legacy</c> відтворює чинну систему, яка id запису в формулу не передавала:
    /// аргумент <c>Lookup</c>-колонки там — рівно той запис, що й до кроку (порожнє
    /// значення, без <c>EntryId</c>). Рівність записів — побітна, разом з усіма полями.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.18")]
    public async Task Legacy_побітно_як_до_кроку()
    {
        var stand = new Stand();
        var builder = new CalculationInputBuilder(stand.Cells, stand.Metadata, stand.Rows);

        var legacy = Assert.Single(await builder.BuildAsync(
            TableInstance, ["R1"], new PeriodKey(Period), Descriptor(NumericMode.Legacy, VersionId, MethodologyId),
            CancellationToken.None));

        // До кроку `CalculationInputBuilder` будував рівно такий список. ✎ L2-3 (розширення D-161): id запису
        // `Lookup` тепер їде в ОБОХ режимах — його потребує правило категорії; формули `Legacy` його не бачать
        // (`GenericCalculationModule.ToValue`, див. CategoryRuleLookupEntryIdTests), тож числа формул побітно ті самі.
        CalculationArgument[] before =
        [
            new("Stream", null, null, null, Stream1D2),
            new("HmbCase", null, "370 Winter", null),
            new("Volume", 269.258m, null, null),
            new("Duration", 930m, null, null),
            new("Density", 0.9589m, null, null),
        ];

        Assert.Equal(before, legacy.Arguments.OrderBy(a => ArgumentOrder(a.ArgumentCode)).ToArray());

        var strict = Assert.Single(await builder.BuildAsync(
            TableInstance, ["R1"], new PeriodKey(Period), Descriptor(NumericMode.Strict, VersionId, MethodologyId),
            CancellationToken.None));

        // У Strict — той самий аргумент плюс EntryRef, і більше нічого.
        Assert.Equal(new CalculationArgument("Stream", null, null, null, Stream1D2), strict.Arguments.Single(a => a.ArgumentCode == "Stream"));
        Assert.All(strict.Arguments.Where(a => a.ArgumentCode != "Stream"), a => Assert.Null(a.EntryId));
    }

    // ——— RT-21: перевірка одиниць проходить чесно ———

    /// <remarks>
    /// ⚠ Правило добутку (RT-21): дві РОЗМІРНІ величини без оголошеної похідної одиниці
    /// — помилка публікації. Тому <c>MOL_PCT</c> у фікстурі безрозмірний (частка, в
    /// об.%), а <c>MW</c> — у г/моль: μ = Σ x·M / 100 має одиницю г/моль. Перевірку не
    /// вимкнено — контрольний випадок нижче показує, що вона б спрацювала.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Формули_складу_проходять_перевірку_типів_і_одиниць()
    {
        var context = BindingContext(InMemoryRegistryShapes.Hse301(mwUnit: GramPerMoleUnit));

        foreach (var code in new[] { "CASE", "MU", "S_WT", "EF_RAW" })
        {
            var root = Parse(code);
            var diagnostics = new List<ExpressionDiagnostic>();

            new TypeChecker().Check(root, context, diagnostics);
            new UnitChecker().Check(root, context, diagnostics);

            Assert.True(diagnostics.Count == 0, $"{code}: {string.Join("; ", diagnostics.Select(d => d.MessageKey))}");
        }

        var mu = new UnitChecker().Check(Parse("MU"), context, []);
        Assert.Equal(GramPerMoleUnit, mu);

        // Контроль: той самий склад, але мол.% — з одиницею. Добуток двох розмірних
        // величин без похідної одиниці відхиляється, а не рахується.
        var dimensioned = new InMemoryRegistryShapes()
            .Add("GAS_COMPOSITION",
                [
                    InMemoryRegistryShapes.Field("CASE", CellDataType.Lookup, lookup: "STREAM_CASE"),
                    InMemoryRegistryShapes.Field("COMPONENT", CellDataType.Lookup, lookup: "COMPONENT"),
                    InMemoryRegistryShapes.Field("MOL_PCT", CellDataType.Decimal, PercentByMassUnit),
                ])
            .Add("COMPONENT", [InMemoryRegistryShapes.Field("MW", CellDataType.Decimal, GramPerMoleUnit)])
            .Add("STREAM_CASE", [InMemoryRegistryShapes.Field("STREAM", CellDataType.Lookup, lookup: "STREAM")]);

        var rejected = new List<ExpressionDiagnostic>();
        new UnitChecker().Check(Parse("MU"), BindingContext(dimensioned), rejected);
        Assert.Contains(rejected, d => d.MessageKey == "expr.unit.productUndeclared");
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static JsonElement LoadFixture()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TestFixtures.Path("flert-1d2-370-winter.json")));
        return document.RootElement.Clone();
    }

    private static decimal Expected(string code)
        => Fixture.GetProperty("expected").GetProperty(code).GetProperty("value").GetDecimal();

    private static int Digits(string code)
        => Fixture.GetProperty("expected").GetProperty(code).GetProperty("digits").GetInt32();

    private static decimal Event(string name) => Fixture.GetProperty("event").GetProperty(name).GetDecimal();

    private static decimal Round(decimal value, int digits)
        => decimal.Round(value, digits, MidpointRounding.AwayFromZero);

    private static void AssertRelative(decimal expected, decimal actual, decimal tolerance)
        => Assert.True(
            Math.Abs(actual - expected) <= Math.Abs(expected) * tolerance,
            $"очікувалося {expected}, отримано {actual} (відносний допуск {tolerance})");

    private static decimal Value(CalculationOutput output, string code)
        => Assert.Single(output.Values, v => v.OutputCode == code).Value;

    private static int ArgumentOrder(string code)
        => Array.IndexOf(["Stream", "HmbCase", "Volume", "Duration", "Density"], code);

    private static AstNode Parse(string code)
    {
        var text = FormulaTexts.Single(f => f.Code == code).Expression;
        var parsed = Expr.Parse(text, ExpressionDialect.Methodology);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));
        return parsed.Expression!.Root;
    }

    private static TestBindingContext BindingContext(IRegistryShapeSource shapes)
    {
        var context = new TestBindingContext { Registries = shapes };
        context.ArgumentRegistries["Stream"] = "STREAM";
        context.ArgumentTypes["HmbCase"] = ExpressionValueType.Text;
        context.FormulaRegistries["CASE"] = "STREAM_CASE";
        context.ConstantUnits["M_S"] = GramPerMoleUnit;
        context.ConstantUnits["M_CO2"] = GramPerMoleUnit;
        context.Dimensions[GramPerMoleUnit] = 19;
        context.Dimensions[PercentByMassUnit] = 9;
        return context;
    }

    private static MethodologyDescriptor Descriptor(NumericMode mode, int versionId, int methodologyId)
        => new(
            methodologyId,
            MethodologyVersionId: versionId,
            Code: "HSE301_RT23A",
            VersionNumber: "1.0.0.0",
            Level: CalculationLevel.Configuration,
            NumericMode: mode,
            CalendarMode: CalendarMode.Actual,
            TraceLevel: TraceLevel.ErrorsOnly);

    /// <summary>
    /// Знімок довідників із фікстури: потік, кейс потоку (первинний ключ
    /// <c>STREAM + CASE_NAME</c>), компоненти без первинного ключа, склад кейсу.
    /// </summary>
    private static InMemoryRegistrySnapshot Snapshot()
    {
        var snapshot = new InMemoryRegistrySnapshot()
            .AddRegistry("STREAM", ["NAME"])
            .AddRegistry("STREAM_CASE", ["STREAM", "CASE_NAME"], ["STREAM", "CASE_NAME"], ["STREAM"])
            .AddRegistry("COMPONENT", ["FORMULA", "MW", "N_C", "N_S"])
            .AddRegistry("GAS_COMPOSITION", ["CASE", "COMPONENT", "MOL_PCT"], ["CASE", "COMPONENT"], ["CASE", "COMPONENT"]);

        var stream = Fixture.GetProperty("event").GetProperty("stream").GetString()!;
        var hmbCase = Fixture.GetProperty("event").GetProperty("hmbCase").GetString()!;

        snapshot.AddEntry("STREAM", Stream1D2, stream, new() { ["NAME"] = ExpressionValue.Text(stream) }, ordinal: 1);
        snapshot.AddEntry(
            "STREAM_CASE",
            Winter,
            "C101",
            new() { ["STREAM"] = ExpressionValue.Number(Stream1D2), ["CASE_NAME"] = ExpressionValue.Text(hmbCase) },
            ordinal: 1);

        var componentIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var ordinal = 0;
        foreach (var component in Fixture.GetProperty("components").EnumerateArray())
        {
            var code = component.GetProperty("code").GetString()!;
            var values = new Dictionary<string, ExpressionValue>
            {
                ["N_C"] = ExpressionValue.Number(component.GetProperty("nC").GetDecimal()),
                ["N_S"] = ExpressionValue.Number(component.GetProperty("nS").GetDecimal()),
            };

            if (component.GetProperty("formula").ValueKind == JsonValueKind.String)
            {
                values["FORMULA"] = ExpressionValue.Text(component.GetProperty("formula").GetString()!);
            }

            // Без молярної маси (TEG: частка 0) — поле незаповнене, а не нуль.
            if (component.GetProperty("mw").ValueKind == JsonValueKind.Number)
            {
                values["MW"] = ExpressionValue.Number(component.GetProperty("mw").GetDecimal());
            }

            componentIds[code] = 1000 + ++ordinal;
            snapshot.AddEntry("COMPONENT", componentIds[code], code, values, ordinal);
        }

        var row = 5000L;
        foreach (var line in Fixture.GetProperty("composition").EnumerateArray())
        {
            snapshot.AddEntry(
                "GAS_COMPOSITION",
                ++row,
                $"W{row}",
                new()
                {
                    ["CASE"] = ExpressionValue.Number(Winter),
                    ["COMPONENT"] = ExpressionValue.Number(componentIds[line.GetProperty("component").GetString()!]),
                    ["MOL_PCT"] = ExpressionValue.Number(line.GetProperty("molPct").GetDecimal()),
                },
                ordinal: (int)(row - 5000));
        }

        return snapshot;
    }

    /// <summary>Стенд модуля: справжній рушій і модуль, підмінені лише сховища.</summary>
    private sealed class Stand
    {
        public Stand()
        {
            Snapshot = Hse301GoldenTests.Snapshot();

            foreach (var (methodologyId, versionId) in new[] { (MethodologyId, VersionId), (SecondMethodologyId, SecondVersionId) })
            {
                Methodologies.GetPublishedVersionsAsync(methodologyId, Arg.Any<CancellationToken>())
                             .Returns([PublishedVersion(methodologyId, versionId)]);
                Methodologies.GetRulesAsync(versionId, Arg.Any<CancellationToken>())
                             .Returns([new MethodologyRule(versionId, EcrCode.Create("ALL"), "{}", priority: 10)]);
                Methodologies.GetFormulasAsync(versionId, Arg.Any<CancellationToken>()).Returns(Formulas(versionId));
                Methodologies.GetSubstancesAsync(versionId, Arg.Any<CancellationToken>()).Returns([]);
                Methodologies.GetOutputsAsync(versionId, Arg.Any<CancellationToken>()).Returns(Outputs(versionId));
                Methodologies.GetConstantsAsync(versionId, Arg.Any<CancellationToken>()).Returns(Constants(versionId));
            }

            Periods.FindPeriodBoundsAsync(DocumentId, Period, Arg.Any<CancellationToken>())
                   .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), BusinessDate));

            Bindings.ListOutputScalesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(new Dictionary<string, byte?>(StringComparer.OrdinalIgnoreCase));

            Units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
                new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
                {
                    ["kg"] = new(KilogramUnit, "kg", 1, 1m),
                    ["t"] = new(TonneUnit, "t", 1, 1000m),
                    ["g"] = new(GramUnit, "g", 1, 0.001m),
                },
                new Dictionary<string, int>(StringComparer.Ordinal)));

            RegistryStore.FindDefinitionsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                         .Returns(call => Definitions(call.Arg<IReadOnlyCollection<string>>()));

            Loader.LoadAsync(
                    Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateOnly>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
                  .Returns(Snapshot);

            foreach (var table in new[] { TableInstance, SecondTableInstance })
            {
                Rows.ResolveTableInstanceAsync(table, Arg.Any<CancellationToken>())
                    .Returns(new TableInstanceRef(table, DocumentId, 3, TemplateVersion, Period));
                Rows.GetRowIdsAsync(table, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                    .Returns(new Dictionary<string, long> { ["R1"] = table * 10 });
                Cells.ReadSliceAsync(table, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(EventCells(table * 10));
            }

            Metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>()).Returns(
                new TemplateVersionSnapshot(
                    TemplateVersion,
                    0,
                    [],
                    new Dictionary<int, ColumnDef>
                    {
                        [StreamColumn] = Column("Stream", CellDataType.Lookup),
                        [CaseColumn] = Column("HmbCase", CellDataType.String),
                        [VolumeColumn] = Column("Volume", CellDataType.Decimal),
                        [DurationColumn] = Column("Duration", CellDataType.Decimal),
                        [DensityColumn] = Column("Density", CellDataType.Decimal),
                    },
                    new Dictionary<(int TableDefId, string RowKey), RowDef>()));

            Module = new GenericCalculationModule(
                new RealFormulaEngine(),
                Methodologies,
                new ConstantResolver(Substitute.For<IConstantStore>()),
                new CalendarContext(),
                Units,
                Periods,
                Bindings,
                RegistryStore,
                Loader);
        }

        public InMemoryRegistrySnapshot Snapshot { get; }

        public IMethodologyStore Methodologies { get; } = Substitute.For<IMethodologyStore>();

        public IPeriodStore Periods { get; } = Substitute.For<IPeriodStore>();

        public ICalculationBindingStore Bindings { get; } = Substitute.For<ICalculationBindingStore>();

        public IUnitCatalog Units { get; } = Substitute.For<IUnitCatalog>();

        public IRegistryStore RegistryStore { get; } = Substitute.For<IRegistryStore>();

        public IRegistrySnapshotLoader Loader { get; } = Substitute.For<IRegistrySnapshotLoader>();

        public IRowStore Rows { get; } = Substitute.For<IRowStore>();

        public ICellStore Cells { get; } = Substitute.For<ICellStore>();

        public IMetadataCache Metadata { get; } = Substitute.For<IMetadataCache>();

        public GenericCalculationModule Module { get; }

        public void ClearReceivedCalls()
        {
            Methodologies.ClearReceivedCalls();
            Periods.ClearReceivedCalls();
            Bindings.ClearReceivedCalls();
            Units.ClearReceivedCalls();
            RegistryStore.ClearReceivedCalls();
            Loader.ClearReceivedCalls();
        }

        /// <summary>Рядок події: потік — <c>EntryRef</c> лише в <c>Strict</c>.</summary>
        public CalculationInput Input(NumericMode mode)
            => new(
                Descriptor(mode, VersionId, MethodologyId),
                DocumentId,
                TableInstance,
                new PeriodKey(Period),
                "R1",
                [
                    new CalculationArgument("Stream", null, null, null, mode == NumericMode.Strict ? Stream1D2 : null),
                    new CalculationArgument("HmbCase", null, Fixture.GetProperty("event").GetProperty("hmbCase").GetString(), null),
                    new CalculationArgument("Volume", Event("volumeSm3"), null, null),
                    new CalculationArgument("Duration", Event("durationS"), null, null),
                    new CalculationArgument("Density", Event("densityKgPerSm3"), null, null),
                ]);

        /// <summary>Стенд оркестратора: ті самі сховища в DI, справжні резолвер, збирач і писар.</summary>
        public ServiceProvider Services(List<CalculationOutput> written)
        {
            var results = Substitute.For<ICalculationResultStore>();
            results.WriteResultsAsync(
                    Arg.Any<long>(),
                    Arg.Do<IReadOnlyList<CalculationOutput>>(outputs =>
                    {
                        lock (written)
                        {
                            written.AddRange(outputs);
                        }
                    }),
                    Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            var services = new ServiceCollection();
            services.AddSingleton(Methodologies);
            services.AddSingleton(Periods);
            services.AddSingleton(Rows);
            services.AddSingleton(Cells);
            services.AddSingleton(Metadata);
            services.AddSingleton(Units);
            services.AddSingleton(results);
            services.AddSingleton(RegistryStore);
            services.AddSingleton(Loader);
            services.AddSingleton(Bindings);
            services.AddSingleton(Substitute.For<IConstantStore>());
            services.AddSingleton(Substitute.For<IUnitOfWork>());
            services.AddSingleton<IFormulaEngine, RealFormulaEngine>();
            services.AddSingleton<CalendarContext>();
            services.AddScoped<ConstantResolver>();
            services.AddScoped<MethodologyResolver>();
            services.AddScoped<CalculationInputBuilder>();
            services.AddScoped<CalculationOutputWriter>();
            services.AddScoped<ICalculationModule, GenericCalculationModule>();

            return services.BuildServiceProvider();
        }

        private static List<RegistryDef> Definitions(IReadOnlyCollection<string> codes)
        {
            var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["STREAM"] = StreamDef,
                ["STREAM_CASE"] = StreamCaseDef,
                ["COMPONENT"] = ComponentDef,
                ["GAS_COMPOSITION"] = GasCompositionDef,
            };

            return codes
                .Where(ids.ContainsKey)
                .Select(code =>
                {
                    var definition = new RegistryDef(
                        EcrCode.Create(code),
                        new LocalizedText(new Dictionary<string, string> { ["en"] = code }),
                        isTemporal: false);
                    typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(definition, ids[code]);
                    return definition;
                })
                .ToList();
        }

        private static List<CellRecord> EventCells(long rowId)
        {
            var hmbCase = Fixture.GetProperty("event").GetProperty("hmbCase").GetString();
            return
            [
                Cell(rowId, StreamColumn, new CellValueData { ValueRegistryEntryId = Stream1D2 }),
                Cell(rowId, CaseColumn, new CellValueData { ValueString = hmbCase }),
                Cell(rowId, VolumeColumn, new CellValueData { ValueNumeric = Event("volumeSm3") }),
                Cell(rowId, DurationColumn, new CellValueData { ValueNumeric = Event("durationS") }),
                Cell(rowId, DensityColumn, new CellValueData { ValueNumeric = Event("densityKgPerSm3") }),
            ];
        }

        private static CellRecord Cell(long rowId, int columnId, CellValueData value)
            => new(new CellAddress(new PeriodKey(Period), rowId, columnId), TableDefId: 3, value);

        private static ColumnDef Column(string code, CellDataType type)
            => new(
                tableDefId: 3,
                EcrCode.Create(code),
                new LocalizedText(new Dictionary<string, string> { ["en"] = code }),
                ordinal: 0,
                type);

        private static List<MethodologyFormula> Formulas(int versionId)
            => FormulaTexts
                .Select((f, i) =>
                {
                    var formula = new MethodologyFormula(versionId, EcrCode.Create(f.Code), f.Expression);
                    formula.SetEvaluationOrder(i + 1);
                    return formula;
                })
                .ToList();

        private static List<MethodologyOutput> Outputs(int versionId) =>
        [
            new(versionId, EcrCode.Create("MU"), GramPerMoleUnit),
            new(versionId, EcrCode.Create("S_WT"), PercentByMassUnit),
            new(versionId, EcrCode.Create("EF_RAW"), TonnePerTonneUnit),
            new(versionId, EcrCode.Create("EF"), TonnePerTonneUnit),
            new(versionId, EcrCode.Create("M"), TonneUnit),
            new(versionId, EcrCode.Create("SO2"), TonneUnit),
            new(versionId, EcrCode.Create("SO2_GS"), GramPerSecondUnit),
            new(versionId, EcrCode.Create("CO2_GHG"), TonneUnit),
        ];

        private static List<MethodologyConstant> Constants(int versionId)
            => Fixture.GetProperty("constants")
                .EnumerateObject()
                .Select(c =>
                {
                    var constant = new MethodologyConstant(versionId, EcrCode.Create(c.Name), c.Value.GetDecimal(), unitId: 1);
                    constant.SetScope(null, null);
                    return constant;
                })
                .ToList();

        private static MethodologyVersion PublishedVersion(int methodologyId, int versionId)
        {
            var utcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var version = new MethodologyVersion(
                methodologyId, "1.0.0.0", CalculationLevel.Configuration, createdByUserId: 1, utcNow);

            version.SetModes(NumericMode.Strict, CalendarMode.Actual, TraceLevel.ErrorsOnly);
            version.Publish(
                publishedByUserId: 2,
                changeReason: "стенд RT-23a",
                effectiveFrom: new DateOnly(2025, 1, 1),
                testsPassed: true,
                utcNow);

            typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(version, versionId);

            return version;
        }
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
