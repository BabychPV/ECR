// tests/Ecr.Calculations.Tests/Registries/C1101V5GoldenParityTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests.Registries;

/// <summary>
/// B21: справжні очікування золотого <c>PARITY_1</c> методології ECW_C11_01 V5 (live, версія id 80) — через СПРАВЖНІЙ
/// <see cref="GenericCalculationModule"/> на даних версії, а не вигадані числа.
/// </summary>
/// <remarks>
/// ⛔ Що було. <c>PARITY_1</c> (імпорт AF) має <c>expectedJson = {}</c>, і <c>GoldenSet.Judge</c> не звіряв нічого:
/// live-регресія (doc 96, рядок A37: 0 результатів, 64 кроки #VALUE) пройшла ворота зеленою. Золоті випадки Land
/// живуть лише в базі стенда (у репозиторії їх немає), тож тут закріплено ТЕ, що координатор задає через продукт
/// (<c>PUT /methodologies/15/versions/80/tests/PARITY_1</c>), і доказ, що з ним набір справді щось перевіряє.
///
/// Дані — дослівно з бази live (read-only, 12.11): вхід <c>PARITY_1</c> V5 (doc 96, 202609, NAE «6646», потік
/// «LPG (HMB) - СУГ (МТБ)», час роботи 2), формули гілки type2 (NAE 6646..6657 бере верх над потоком), правило
/// категорії, константи категорії <c>6646_LPG</c>, склад речовин версії (19), застосовність C11_01/потік (14 речовин),
/// довідники LAND_HSE400_STREAM/SUBSTANCE і склад LAND_HSE400_COMPOSITION (LPG_HMB) за 2026-07 і 2026-09. Формули
/// type1_* не взято: для NAE 6646 їхні ворота дають NULL, і вихід без числа не пишеться (див.
/// <see cref="C1101NumericGateLegacyTests"/>).
///
/// ⚠ Склад LPG_HMB за 2026-08 і 2026-09 на live — НУЛІ (усі 91 показник), тож за періодом входу PARITY_1 (202609)
/// кожен вихід = 0. Такий набір ловить #VALUE і зниклу гілку (вихід речовини відсутній), але не хибне число;
/// для чисел — варіант за 202607 нижче.
/// </remarks>
public sealed class C1101V5GoldenParityTests
{
    private const int MethodologyId = 15;
    private const int VersionId = 80;
    private const long DocumentId = 96;
    private const string Stream = "LPG (HMB) - СУГ (МТБ)";

    private const string Nae66 =
        "CONTAINS(@Land_NAE, '6646') or CONTAINS(@Land_NAE, '6647') or CONTAINS(@Land_NAE, '6648') or CONTAINS(@Land_NAE, '6649') "
        + "or CONTAINS(@Land_NAE, '6650') or CONTAINS(@Land_NAE, '6651') or CONTAINS(@Land_NAE, '6652') or CONTAINS(@Land_NAE, '6653') "
        + "or CONTAINS(@Land_NAE, '6654') or CONTAINS(@Land_NAE, '6655') or CONTAINS(@Land_NAE, '6656') or CONTAINS(@Land_NAE, '6657')";

    private const string Composition =
        "REGFIELD(REGFIND('LAND_HSE400_COMPOSITION', [Period].Year, [Period].Sequence, "
        + "REGFIELD(REGFIND('LAND_HSE400_STREAM', @Land_TypeFuelOrStream), 'PREFIX'), "
        + "REGFIELD(REGFIND('LAND_HSE400_SUBSTANCE', CURRENT_SUBSTANCE()), 'INDICATOR')), 'VALUE')";

    private const string Leak =
        "(CST.EFs_flanges_*CST.Number_flanges_+CST.EFs_pumps_*CST.Number_pumps_+CST.EFs_valves_*CST.Number_valves_"
        + "+CST.EFs_others_*CST.Number_others_)";

    private const string GsecType2 = "ECW_EC_gsec_XXX_type2";
    private const string TonsType2 = "ECW_EC_tons_XXX_type2";

    private static readonly string GsecExpression =
        $"if({Nae66}, if (@Land_Measure_OperatingTime__ECW_C11_01 = 0, 0,\n{Leak}/CST.k4_ECW_C11_01_*({Composition}/CST.k2_ECW_C11_01_)\n), NULL)";

    private static readonly string TonsExpression =
        $"if({Nae66}, {Leak}/CST.k1_ECW_C11_01_*({Composition}/CST.k2_ECW_C11_01_)*@Land_Measure_OperatingTime__ECW_C11_01, NULL)";

    // Правило категорії V5 дослівно (calc.CategoryRule, версія 80).
    private const string CategoryRule =
        "if(SUBSTANCE_APPLICABLE('LAND_SUBSTANCE_APPLICABILITY','ECW_C11_01','Stream',@Land_TypeFuelOrStream),"
        + "if(in(@Land_TypeFuelOrStream,'LPG (HMB) - СУГ (МТБ)','LPG (Internal) - СУГ (Внутренний)'),@Land_NAE+'_LPG',"
        + "if(in(@Land_TypeFuelOrStream,'Makat gas - Макатский газ','Fuel gas (Makat) - Топливный газ (Makat)',"
        + "'Fuel gas (HMB) - Топливный газ (МТБ)','Fuel gas (Export) - Топливный газ (Товарный)',"
        + "'Fuel Gas (Internal) - Топливный газ (Внутренний)','Fuel gas (Internal) - Топливный газ (Внутренний)'),@Land_NAE+'_FG',"
        + "if(in(@Land_TypeFuelOrStream,'1-204 sludge - 1-204 шлам'),@Land_NAE+'_sludge',"
        + "if(in(@Land_TypeFuelOrStream,'spent caustic - отработанный каустик'),@Land_NAE+'_caustic',"
        + "if(in(@Land_TypeFuelOrStream,'Waste water'),@Land_NAE+'_Wwater',"
        + "if(in(@Land_TypeFuelOrStream,'401'),@Land_NAE+'_401',"
        + "if(in(@Land_TypeFuelOrStream,'14'),@Land_NAE+'_14',"
        + "if(in(@Land_TypeFuelOrStream,'452'),@Land_NAE+'_452',"
        + "if(in(@Land_TypeFuelOrStream,'Diesel - Дизель','Diesel - Дизельное топливо'),@Land_NAE+'_Diesel',''))))))))),'')";

    /// <summary>Склад версії 80: запис речовини → SUBSTANCE_CODE (19 речовин, порядок версії).</summary>
    private static readonly (long Entry, string Code)[] Substances =
    [
        (6042, "330"), (6044, "333"), (6045, "334"), (6049, "370"), (6051, "415"), (6052, "416"), (6055, "602"),
        (6056, "616"), (6057, "621"), (6058, "627"), (6069, "1052"), (6084, "1702"), (6085, "1707"), (6086, "1715"),
        (6088, "1720"), (6089, "1728"), (6090, "1852"), (6092, "1880"), (6099, "2754"),
    ];

    /// <summary>LAND_HSE400_SUBSTANCE: SUBSTANCE_CODE → INDICATOR (live, 15 записів).</summary>
    private static readonly (string Code, string Indicator)[] Indicators =
    [
        ("333", "H2S_mass"), ("334", "CS2_mass"), ("370", "COS_mass"), ("415", "C1_C5_mass"), ("416", "C6_C10_mass"),
        ("602", "Benzene_mass"), ("616", "pXylene_mass"), ("621", "Toluene_mass"), ("627", "EBenzene_mass"),
        ("1702", "nBMercaptan_mass"), ("1707", "DimethilSulfide_mass"), ("1715", "MMercaptan_mass"),
        ("1720", "nPMercaptan_mass"), ("1728", "EMercaptan_mass"), ("2754", "C12_C19_mass"),
    ];

    /// <summary>LAND_SUBSTANCE_APPLICABILITY: ECW_C11_01 / Stream / «LPG (HMB) - СУГ (МТБ)» (live, 14 записів + маркер).</summary>
    private static readonly string[] Applicable =
        ["333", "334", "370", "415", "416", "602", "616", "621", "627", "1702", "1715", "1720", "1728", "2754"];

    /// <summary>LAND_HSE400_COMPOSITION, LPG_HMB, 2026-07: VALUE показників застосовних речовин (live).</summary>
    private static readonly Dictionary<string, decimal> July = new(StringComparer.Ordinal)
    {
        ["Benzene_mass"] = 0.0000023792191116m,
        ["C1_C5_mass"] = 99.9578994881403130m,
        ["C12_C19_mass"] = 0m,
        ["C6_C10_mass"] = 0.0001093096846134m,
        ["COS_mass"] = 0.0400579006921433m,
        ["CS2_mass"] = 0.0006316097733588m,
        ["EBenzene_mass"] = 0.0000000000017033m,
        ["EMercaptan_mass"] = 0.0004964849892706m,
        ["H2S_mass"] = 0m,
        ["MMercaptan_mass"] = 0.0008027227883771m,
        ["nBMercaptan_mass"] = 0.0000000011161376m,
        ["nPMercaptan_mass"] = 0.0000000907301759m,
        ["pXylene_mass"] = 0.0000000000070591m,
        ["Toluene_mass"] = 0.0000000076636112m,
    };

    /// <summary>
    /// Точний <c>expectedJson</c> для <c>PARITY_1</c> V5 з ІСНУЮЧИМ входом (202609): кожна застосовна речовина обох
    /// виходів type2 — 0 (склад LPG_HMB за 2026-09 на live нульовий). Ключі — поречовинні, тож відсутня речовина
    /// (гілка не спрацювала, #VALUE) — розбіжність, а не «нема з чим порівняти».
    /// </summary>
    internal static Dictionary<string, decimal> SeptemberExpected()
    {
        var expected = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (entry, code) in Substances.Where(s => Applicable.Contains(s.Code)))
        {
            expected[$"{TonsType2}{GoldenSet.SubstanceSeparator}{entry}"] = 0m;
            expected[$"{GsecType2}{GoldenSet.SubstanceSeparator}{entry}"] = 0m;
        }

        return expected;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task PARITY_1_V5_за_202609_дає_нулі_type2_рівно_для_14_застосовних_речовин()
    {
        var output = await Run(202609);

        var expected = SeptemberExpected();
        Assert.Equal(28, expected.Count);
        Assert.Equal(28, output.Values.Count);
        Assert.All(output.Values, v => Assert.True(v.OutputCode is TonsType2 or GsecType2, v.OutputCode));
        Assert.All(output.Values, v => Assert.Equal(0m, v.Value));
        Assert.Equal(
            Substances.Where(s => Applicable.Contains(s.Code)).Select(s => s.Entry).Order(),
            output.Values.Where(v => v.OutputCode == TonsType2).Select(v => (long)v.SubstanceEntryId!.Value).Order());
        Assert.DoesNotContain(output.Trace, s => s.TraceJson is not null && s.TraceJson.Contains("#VALUE", StringComparison.Ordinal));

        // Той самий набір через ворота: з очікуваннями — зелений; з `{}` (як зараз на live) — уже НЕ зелений (B21).
        Assert.True(GoldenSet.Judge(Case(expected), output).IsGreen);
        Assert.False(GoldenSet.Judge(Case(new Dictionary<string, decimal>()), output).IsGreen);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void PARITY_1_V5_ловить_регресію_0_результатів_яку_пропускав_порожній_набір()
    {
        // live A37 до #843: 0 результатів. Порожній вихід із очікуваннями — 28 розбіжностей «виходу не було».
        var empty = new CalculationOutput(DocumentId, "PARITY_1", [], []);

        var verdict = GoldenSet.Judge(Case(SeptemberExpected()), empty);

        Assert.False(verdict.IsGreen);
        Assert.Equal(28, verdict.Mismatches.Count);
        Assert.All(verdict.Mismatches, m => Assert.Null(m.Actual));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task PARITY_1_V5_за_202607_дає_числа_витоку_на_склад_LPG_HMB()
    {
        var output = await Run(202607);

        Assert.Equal(28, output.Values.Count);

        // Витік категорії 6646_LPG: 0.00039·12 + 0.0024·0 + 0.00000036·1 + 0.0088·0 = 0.00468036.
        // gsec = витік / k4 (3.6) · VALUE / k2 (100); tons = витік / k1 (1000) · VALUE / 100 · час роботи (2).
        foreach (var (entry, code) in Substances.Where(s => Applicable.Contains(s.Code)))
        {
            var x = July[Indicators.Single(i => i.Code == code).Indicator];
            var gsec = Assert.Single(output.Values, v => v.OutputCode == GsecType2 && v.SubstanceEntryId == entry);
            var tons = Assert.Single(output.Values, v => v.OutputCode == TonsType2 && v.SubstanceEntryId == entry);

            Assert.Equal(0.00468036m / 3.6m * (x / 100m), gsec.Value, 15);
            Assert.Equal(0.00468036m / 1000m * (x / 100m) * 2m, tons.Value, 15);
        }

        // Закріплено числом (C1–C5, речовина 415): 0.0013001 · 0.999578994881403130 і 0.00000936072 · 0.999578994881403130.
        Assert.Equal(0.001299552651245312m, Assert.Single(output.Values, v => v.OutputCode == GsecType2 && v.SubstanceEntryId == 6051).Value, 15);
        Assert.Equal(0.000009356779088966m, Assert.Single(output.Values, v => v.OutputCode == TonsType2 && v.SubstanceEntryId == 6051).Value, 15);
    }

    private static MethodologyTestCase Case(Dictionary<string, decimal> expected)
        => new("PARITY_1", Input(202609), expected, Tolerance: 0.000001m);

    private static CalculationInput Input(int period) => new(
        new MethodologyDescriptor(
            MethodologyId, VersionId, Code: "ECW_C11_01", VersionNumber: "V5",
            Level: CalculationLevel.Configuration, NumericMode: NumericMode.Legacy,
            CalendarMode: CalendarMode.Actual, TraceLevel: TraceLevel.ErrorsOnly),
        DocumentId,
        TableInstanceId: 0,
        PeriodKey: new PeriodKey(period),
        SourceRowKey: "PARITY_1",

        // Аргументи PARITY_1 V5 дослівно (calc.TestCase, версія 80).
        Arguments:
        [
            new CalculationArgument("CompositionMass", 2m, null, null),
            new CalculationArgument("Land_Measure_OperatingTime", 2m, null, null),
            new CalculationArgument("Land_NAE", null, "6646", null),
            new CalculationArgument("Land_TypeFuelOrStream", null, Stream, null),
            new CalculationArgument("Land_Measure_OperatingTime__ECW_C11_01", 2m, null, null),
        ]);

    private static async Task<CalculationOutput> Run(int period)
    {
        var formulas = new List<MethodologyFormula>();
        var outputs = new List<MethodologyOutput>();
        var order = 0;
        foreach (var (code, expression) in new[] { (GsecType2, GsecExpression), (TonsType2, TonsExpression) })
        {
            var formula = new MethodologyFormula(VersionId, EcrCode.Create(code), expression);
            formula.SetScope(MethodologyFormulaScope.Substance);
            formula.SetEvaluationOrder(++order);
            formulas.Add(formula);
            outputs.Add(new MethodologyOutput(VersionId, EcrCode.Create(code), unitId: 1, ordinal: order));
        }

        var store = Substitute.For<IMethodologyStore>();
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(formulas);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(outputs);
        store.GetCategoryRuleAsync(VersionId, Arg.Any<CancellationToken>()).Returns(CategoryRule);
        store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns(Substances.Select((s, i) => new MethodologySubstance(VersionId, s.Entry, i)).ToList());
        store.GetSubstanceCodesAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns((IReadOnlyDictionary<long, string>)Substances.ToDictionary(s => s.Entry, s => s.Code));
        store.GetConstantsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(Constants());

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, period, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(
                   new DateOnly(period / 100, period % 100, 1),
                   new DateOnly(period / 100, period % 100, DateTime.DaysInMonth(period / 100, period % 100))));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["kg"] = new(1, "kg", 1, 1m) },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        string[] registryCodes =
            ["LAND_SUBSTANCE_APPLICABILITY", "LAND_HSE400_STREAM", "LAND_HSE400_SUBSTANCE", "LAND_HSE400_COMPOSITION"];
        var registries = Substitute.For<IRegistryStore>();
        registries.FindDefinitionsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                  .Returns(registryCodes
                      .Select(code => new RegistryDef(
                          EcrCode.Create(code),
                          new LocalizedText(new Dictionary<string, string> { ["en"] = code }),
                          isTemporal: false))
                      .ToList());
        var loader = Substitute.For<IRegistrySnapshotLoader>();
        loader.LoadAsync(
                  Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateOnly>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
              .Returns(Snapshot());

        var module = new GenericCalculationModule(
            new RealFormulaEngine(), store, new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(), units, periods, Substitute.For<ICalculationBindingStore>(), registries, loader);

        return await module.ExecuteAsync(Input(period), CancellationToken.None);
    }

    /// <summary>Константи категорії 6646_LPG версії 80 (live, без речовини, чинні з 2024-01-01).</summary>
    private static List<MethodologyConstant> Constants()
    {
        (string Code, decimal Value)[] rows =
        [
            ("EFs_flanges_", 0.00039m), ("EFs_others_", 0.0088m), ("EFs_pumps_", 0.0024m), ("EFs_valves_", 0.00000036m),
            ("k1_ECW_C11_01_", 1000m), ("k2_ECW_C11_01_", 100m), ("k3_ECW_C11_01_", 24m), ("k4_ECW_C11_01_", 3.6m),
            ("Number_flanges_", 12m), ("Number_others_", 0m), ("Number_pumps_", 0m), ("Number_valves_", 1m),
        ];

        return
        [
            .. rows.Select(r =>
            {
                var constant = new MethodologyConstant(VersionId, EcrCode.Create(r.Code), r.Value, unitId: 1);
                constant.SetScope("6646_LPG", substanceEntryId: null);
                return constant;
            }),
        ];
    }

    private static InMemoryRegistrySnapshot Snapshot()
    {
        var snapshot = new InMemoryRegistrySnapshot()
            .AddRegistry(
                "LAND_SUBSTANCE_APPLICABILITY", ["METHODOLOGY", "SELECTOR_KIND", "SELECTOR_VALUE", "SUBSTANCE_CODE"],
                primaryKey: ["METHODOLOGY", "SELECTOR_KIND", "SELECTOR_VALUE", "SUBSTANCE_CODE"])
            .AddRegistry("LAND_HSE400_STREAM", ["TEXT", "PREFIX"], primaryKey: ["TEXT"])
            .AddRegistry("LAND_HSE400_SUBSTANCE", ["SUBSTANCE_CODE", "INDICATOR"], primaryKey: ["SUBSTANCE_CODE"])
            .AddRegistry(
                "LAND_HSE400_COMPOSITION", ["YEAR", "MONTH", "STREAM", "INDICATOR", "VALUE"],
                primaryKey: ["YEAR", "MONTH", "STREAM", "INDICATOR"]);

        static Dictionary<string, ExpressionValue> Applicability(string kind, string value, string code) => new()
        {
            ["METHODOLOGY"] = ExpressionValue.Text("ECW_C11_01"),
            ["SELECTOR_KIND"] = ExpressionValue.Text(kind),
            ["SELECTOR_VALUE"] = ExpressionValue.Text(value),
            ["SUBSTANCE_CODE"] = ExpressionValue.Text(code),
        };

        snapshot.AddEntry("LAND_SUBSTANCE_APPLICABILITY", 657, "A657", Applicability("*", "*", "*"), ordinal: 1);
        long id = 693;
        foreach (var code in Applicable)
        {
            snapshot.AddEntry("LAND_SUBSTANCE_APPLICABILITY", id, $"A{id}", Applicability("Stream", Stream, code), ordinal: (int)id);
            id++;
        }

        snapshot.AddEntry(
            "LAND_HSE400_STREAM", 639, "SE870A25657",
            new() { ["TEXT"] = ExpressionValue.Text(Stream), ["PREFIX"] = ExpressionValue.Text("LPG_HMB") },
            ordinal: 1);

        long indicatorId = 642;
        foreach (var (code, indicator) in Indicators)
        {
            snapshot.AddEntry(
                "LAND_HSE400_SUBSTANCE", indicatorId, $"S{indicatorId}",
                new() { ["SUBSTANCE_CODE"] = ExpressionValue.Text(code), ["INDICATOR"] = ExpressionValue.Text(indicator) },
                ordinal: (int)indicatorId);
            indicatorId++;
        }

        long compositionId = 90_000;
        foreach (var (month, values) in new[]
                 {
                     (7, July),
                     (9, Indicators.ToDictionary(i => i.Indicator, _ => 0m, StringComparer.Ordinal)),
                 })
        {
            foreach (var (indicator, value) in values)
            {
                snapshot.AddEntry(
                    "LAND_HSE400_COMPOSITION", compositionId, $"C{compositionId}",
                    new()
                    {
                        ["YEAR"] = ExpressionValue.Number(2026m),
                        ["MONTH"] = ExpressionValue.Number(month),
                        ["STREAM"] = ExpressionValue.Text("LPG_HMB"),
                        ["INDICATOR"] = ExpressionValue.Text(indicator),
                        ["VALUE"] = ExpressionValue.Number(value),
                    },
                    ordinal: (int)(compositionId - 90_000));
                compositionId++;
            }
        }

        return snapshot;
    }
}
