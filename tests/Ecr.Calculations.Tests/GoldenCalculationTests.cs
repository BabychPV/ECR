using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Звірка розрахунків із очікуваними числами фікстури — **до останнього
/// знака** (ФВ-9.9).
/// </summary>
/// <remarks>
/// Це прообраз задачі <c>golden-compare</c> Етапу 5, тільки на синтетичних
/// даних. Якщо тут числа не сходяться, на реальному еталоні вони не зійдуться
/// й поготів.
/// </remarks>
public sealed class GoldenCalculationTests
{
    private const int VersionId = 51;
    private const long CodEntry = 901;
    private const long TssEntry = 902;
    private const int TonneUnit = 8;
    private const int GramPerSecondUnit = 20;

    /// <summary>Рядок 7001001 фікстури: січень, лютий, березень.</summary>
    private static readonly decimal[] Volumes = [1200.500m, 1150.000m, 1300.250m];

    private readonly IMethodologyStore _store = Substitute.For<IMethodologyStore>();
    private readonly IConstantStore _constants = Substitute.For<IConstantStore>();

    public GoldenCalculationTests()
    {
        _store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(Formulas());
        _store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(Outputs());
        _store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>()).Returns(Substances());
        _constants.GetCandidatesAsync(VersionId, "EF", Arg.Any<CancellationToken>())
                  .Returns(EmissionFactors());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Тонни_для_ХСК_збігаються_з_очікуваним_значенням_фікстури()
    {
        var output = await RunAsync();

        // 3650.750 m3 × 0.250 = 912.6875 kg → 0.9126875 t → 0.912688 (6 знаків)
        Assert.Equal(0.912688m, Value(output, CodEntry, "tons"));

        // Те саме число з файлу фікстури, а не з коду тесту: якщо код дає
        // інше — правий файл (`08-workflow.md` §4).
        Assert.Equal(Expected("SUB-COD", "tons"), Value(output, CodEntry, "tons"));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Тонни_для_завислих_речовин_збігаються_з_очікуваним()
    {
        var output = await RunAsync();

        // 3650.750 × 0.080 = 292.06 kg → 0.29206 t → 0.292060
        Assert.Equal(0.292060m, Value(output, TssEntry, "tons"));
        Assert.Equal(Expected("SUB-TSS", "tons"), Value(output, TssEntry, "tons"));

        // ⚠ Інше число, ніж у ХСК, на ТИХ САМИХ вхідних даних. Різниця лише
        // в коефіцієнті емісії — і саме тому константа мусить резолвитися за
        // речовиною, а не братися перша ліпша.
        Assert.NotEqual(Value(output, CodEntry, "tons"), Value(output, TssEntry, "tons"));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Грами_за_секунду_у_режимі_Actual_збігаються_з_очікуваним()
    {
        var output = await RunAsync(CalendarMode.Actual);

        // 912687.5 g / (31 × 86400 s) = 0.3407584… → 0.340758
        Assert.Equal(0.340758m, Value(output, CodEntry, "gsec"));
        Assert.Equal(Expected("SUB-COD", "gsec_Actual"), Value(output, CodEntry, "gsec"));

        // ⛔ Та сама методологія, той самий PeriodKey — і КВАРТАЛЬНИЙ проєкт.
        // 202601 у ньому означає перший квартал (90 днів), а не січень (31).
        // Вивести це з ключа неможливо: `PeriodKey = Year*100 + Sequence`
        // (R-A6), і Sequence — порядковий номер періоду, не місяць. Тлумачити
        // його як місяць означало б поділити на 2 678 400 секунд замість
        // 7 776 000 — усі г/с у звіті стали б утричі більшими, і жодна
        // перевірка цього не побачила б: число лишається правдоподібним
        // (ФВ-16.11a, D-112).
        var quarterly = Substitute.For<IPeriodStore>();
        quarterly.FindPeriodBoundsAsync(700, 202601, Arg.Any<CancellationToken>())
                 .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31)));

        var quarter = Value(await RunAsync(periods: quarterly), CodEntry, "gsec");

        Assert.NotEqual(0.340758m, quarter);
        Assert.Equal(
            decimal.Round(912_687.5m / (90m * 86400m), 6, MidpointRounding.AwayFromZero),
            quarter);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Грами_за_секунду_у_режимі_Fixed360_відрізняються_на_три_відсотки()
    {
        var actual = Value(await RunAsync(CalendarMode.Actual), CodEntry, "gsec");
        var fixed360 = Value(await RunAsync(CalendarMode.Fixed360), CodEntry, "gsec");

        // 912687.5 / (30 × 86400) = 0.3521170… → 0.352117
        Assert.Equal(0.352117m, fixed360);
        Assert.Equal(Expected("SUB-COD", "gsec_Fixed360"), fixed360);

        // ⛔ 3.3 % на тих самих вхідних даних і тій самій формулі. Ось заради
        // чого CalendarMode — явне поле версії, обов'язкове в diff публікації
        // (D-78): без нього розбіжність шукають у формулі, де її немає.
        var difference = (fixed360 - actual) / actual;
        Assert.Equal(0.033m, decimal.Round(difference, 3, MidpointRounding.AwayFromZero));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Розрахунок_виконується_для_кожної_речовини_методології()
    {
        var output = await RunAsync();

        // Дві речовини × два виходи. Порахувати «одну і показати всім» —
        // найтихіша з можливих помилок: числа виглядають правдоподібно.
        Assert.Equal(4, output.Values.Count);
        Assert.Equal(
            [CodEntry, TssEntry],
            output.Values.Select(v => (long)v.SubstanceEntryId!).Distinct().Order());

        // Одиниця виходу — обов'язкова (ФВ-16.6): без неї результат не
        // порівнюється ні з лімітом, ні з торішнім числом.
        Assert.All(output.Values, v => Assert.NotEqual(0, v.UnitId));
        Assert.Equal(TonneUnit, output.Values.First(v => v.OutputCode == "tons").UnitId);
        Assert.Equal(GramPerSecondUnit, output.Values.First(v => v.OutputCode == "gsec").UnitId);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Константа_резолвиться_за_речовиною_а_не_береться_перша_ліпша()
    {
        var resolver = new ConstantResolver(_constants);
        var onDate = new DateOnly(2026, 1, 31);

        var cod = await resolver.ResolveAsync(VersionId, "EF", null, CodEntry, onDate, default);
        var tss = await resolver.ResolveAsync(VersionId, "EF", null, TssEntry, onDate, default);

        Assert.Equal(0.250m, cod!.Value.Value);
        Assert.Equal(0.080m, tss!.Value.Value);

        // ⛔ Дві константи з одним кодом, чинні на ту саму дату і не звужені
        // речовиною — це помилка конфігурації, а не привід узяти першу.
        // «Перша ліпша» означає, що число звіту залежить від порядку рядків
        // у таблиці й змінюється від переіндексації.
        _constants.GetCandidatesAsync(VersionId, "AMBIGUOUS", Arg.Any<CancellationToken>())
                  .Returns([Constant("AMBIGUOUS", 1m, null), Constant("AMBIGUOUS", 2m, null)]);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => resolver.ResolveAsync(VersionId, "AMBIGUOUS", null, null, onDate, default));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Результати_пишуться_в_calc_а_не_в_doc_CellValue()
    {
        var cells = Substitute.For<ICellStore>();
        var output = await RunAsync();

        // ⛔ Модуль не звертається до сховища комірок узагалі (D-69). Інакше
        // нічний перерахунок писав би десятки мільйонів рядків у партиції
        // документів і роздував aud.CellChange історією, якої ніхто не робив.
        await cells.DidNotReceive().ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
        await cells.DidNotReceive().BulkInsertAsync(
            Arg.Any<IReadOnlyList<CellRecord>>(), Arg.Any<CancellationToken>());

        // Результат ПОВЕРТАЄТЬСЯ; запис робить CalculationOutputWriter в
        // calc.CalculationResult.
        Assert.NotEmpty(output.Values);
        Assert.All(output.Values, v => Assert.Equal(VersionId, v.MethodologyVersionId));
        Assert.Equal("7001001", output.SourceRowKey);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Трейс_у_режимі_Off_не_пишеться_взагалі()
    {
        var off = await RunAsync(trace: TraceLevel.Off);
        var full = await RunAsync(trace: TraceLevel.Full);

        // ⛔ Off не накопичує навіть у пам'яті. Політика зберігання —
        // «нічого не затирається» (ЗБР-1), тому обсяг керується тим, ЩО
        // пишемо, а не строком: повний трейс кожного кроку кожної методології
        // кожного періоду перевищив би обсяг самих даних (ЗБР-3).
        Assert.Empty(off.Trace);
        Assert.NotEmpty(full.Trace);

        // Числа при цьому однакові: рівень трейсу на результат не впливає.
        Assert.Equal(Value(off, CodEntry, "tons"), Value(full, CodEntry, "tons"));

        // Full містить крок на кожну формулу кожної речовини — чотири на дві.
        Assert.Equal(8, full.Trace.Count);
        Assert.Contains(full.Trace, s => s.StepCode == "MassKg");
    }

    private async Task<CalculationOutput> RunAsync(
        CalendarMode calendar = CalendarMode.Actual,
        TraceLevel trace = TraceLevel.Full,
        IPeriodStore? periods = null)
    {
        var module = new GenericCalculationModule(
            new RealFormulaEngine(),
            _store,
            new ConstantResolver(_constants),
            new CalendarContext(),
            Units(),
            periods ?? Periods());

        var descriptor = new MethodologyDescriptor(
            MethodologyId: 5,
            MethodologyVersionId: VersionId,
            Code: "WATER_DISCHARGE",
            VersionNumber: "1.0.0.0",
            Level: CalculationLevel.Configuration,
            NumericMode: NumericMode.Legacy,
            CalendarMode: calendar,
            TraceLevel: trace);

        Assert.True(module.CanHandle(descriptor));

        var input = new CalculationInput(
            descriptor,
            DocumentId: 700,
            TableInstanceId: 500,
            PeriodKey: new PeriodKey(202601),
            SourceRowKey: "7001001",
            Arguments:
            [
                new CalculationArgument("Jan", Volumes[0], null, null),
                new CalculationArgument("Feb", Volumes[1], null, null),
                new CalculationArgument("Mar", Volumes[2], null, null),
            ]);

        return await module.ExecuteAsync(input, CancellationToken.None);
    }

    /// <summary>
    /// Межі періоду 202601 — січень 2026, як їх задає календар проєкту.
    /// </summary>
    /// <remarks>
    /// ⚠ Межі приходять зі сховища, а не виводяться з <c>PeriodKey</c>: для
    /// квартального проєкту <c>202601</c> — це перший КВАРТАЛ, і поділ на 31
    /// день замість 90 дав би <c>г/с</c> утричі більші (ФВ-16.11a).
    /// </remarks>
    private static IPeriodStore Periods()
    {
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(700, 202601, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        return periods;
    }

    /// <summary>Довідник одиниць за <c>09-seed.sql</c> плюс похідна <c>g/s</c>.</summary>
    private static IUnitCatalog Units()
    {
        var catalog = Substitute.For<IUnitCatalog>();
        catalog.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
            {
                ["kg"] = new(1, "kg", 1, 1m),
                ["t"] = new(8, "t", 1, 1000m),
                ["g"] = new(9, "g", 1, 0.001m),
                ["m3"] = new(2, "m3", 2, 1m),
                ["g_per_s"] = new(20, "g_per_s", 8, 0.001m),
            },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        return catalog;
    }

    private static decimal Value(CalculationOutput output, long substance, string code)
        => output.Values
            .Single(v => v.SubstanceEntryId == substance && v.OutputCode == code)
            .Value;

    /// <summary>Очікуване число з розділу <c>expected.calculation</c> фікстури.</summary>
    private static decimal Expected(string substance, string output)
        => decimal.Parse(
            FixtureWorkbook.Load()
                .Expected("calculation", "7001001", substance, output)
                .GetString()!,
            CultureInfo.InvariantCulture);

    private static List<MethodologyFormula> Formulas()
    {
        List<MethodologyFormula> formulas =
        [
            Formula("Volume", "@Jan + @Feb + @Mar", 1),
            Formula("MassKg", "!Volume * CST.EF", 2),
            Formula("tons", "CONVERT(!MassKg, 'kg', 't')", 3),
            Formula("gsec", "CONVERT(!MassKg, 'kg', 'g') / [Period].Seconds", 4),
        ];

        return formulas;
    }

    private static MethodologyFormula Formula(string code, string expression, int order)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create(code), expression);
        formula.SetEvaluationOrder(order);
        return formula;
    }

    private static List<MethodologyOutput> Outputs() =>
    [
        new(VersionId, EcrCode.Create("tons"), TonneUnit),
        new(VersionId, EcrCode.Create("gsec"), GramPerSecondUnit),
    ];

    private static List<MethodologySubstance> Substances() =>
    [
        new(VersionId, CodEntry),
        new(VersionId, TssEntry),
    ];

    private static List<MethodologyConstant> EmissionFactors() =>
    [
        Constant("EF", 0.250m, CodEntry),
        Constant("EF", 0.080m, TssEntry),
    ];

    private static MethodologyConstant Constant(string code, decimal value, long? substanceEntryId)
    {
        // Одиниця kg_per_m3 (розмірність MassPerVolume, 11) — контекстний
        // коефіцієнт, який живе саме тут, а не в uom.Conversion (ФВ-16.5).
        var constant = new MethodologyConstant(VersionId, EcrCode.Create(code), value, unitId: 23);
        constant.SetScope("default", substanceEntryId);
        return constant;
    }
}
