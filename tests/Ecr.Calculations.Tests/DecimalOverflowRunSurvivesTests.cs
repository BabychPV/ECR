// tests/Ecr.Calculations.Tests/DecimalOverflowRunSurvivesTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Рядок, на якому <see cref="decimal"/> переповнюється, дає помилку В СОБІ, а
/// прогін прив'язки доходить до кінця й пише решту рядків (аудит A2).
/// </summary>
/// <remarks>
/// ⛔ Що було. <c>1e15 * 1e15</c> у <c>Strict</c> кидав
/// <see cref="OverflowException"/> з <c>StrictDecimalArithmetic</c>;
/// <c>GenericCalculationModule</c> і <c>CalculationOrchestrator</c> його не
/// ловили, і <c>RecalculationJob</c> позначав <c>Failed</c> УВЕСЬ прогін — жоден
/// рядок прив'язки не записувався, хоча зіпсованим був один. У <c>Legacy</c> те
/// саме робив <c>Pow(10, 30)</c>: скінченний <c>double</c>, як і в NCalc, але
/// <c>(decimal)1e30</c> при звуженні для трейсу кидав.
///
/// ⚠ Тест іде ЦІЛКОМ через <see cref="CalculationOrchestrator"/> — справжні
/// резолвер, збирач входів, модуль і писар, підмінені лише сховища, — бо
/// вада була саме в тому, що виняток пролітав крізь усі ці шари.
/// </remarks>
public sealed class DecimalOverflowRunSurvivesTests
{
    private const int MethodologyId = 42;
    private const int VersionId = 71;
    private const long DocumentId = 700;
    private const long TableInstance = 500;
    private const int TemplateVersion = 3;
    private const int Period = 202601;
    private const int ColumnA = 21;
    private const int ColumnB = 22;

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.9")]
    [InlineData(NumericMode.Strict, "@A * @B", "1000000000000000", "1000000000000000", "#VALUE", "2", "3", "6")]
    [InlineData(NumericMode.Strict, "Pow(@A, @B)", "0", "-1", "#DIV/0", "2", "3", "8")]
    [InlineData(NumericMode.Strict, "Exp(@A) * @B", "70", "1", "#VALUE", "0", "3", "3")]
    [InlineData(NumericMode.Legacy, "Pow(@A, @B)", "10", "30", "#VALUE", "2", "3", "8")]
    public async Task Рядок_що_переповнюється_дає_помилку_а_сусідній_рядок_число(
        NumericMode mode,
        string expression,
        string badA,
        string badB,
        string expectedError,
        string goodA,
        string goodB,
        string expectedGood)
    {
        var written = new List<CalculationOutput>();
        var provider = Arrange(
            mode,
            expression,
            (Dec(badA), Dec(badB)),
            (Dec(goodA), Dec(goodB)),
            written);

        // ⛔ Головне: прогін НЕ кидає. До фіксу тут летів OverflowException /
        // DivideByZeroException, і `RecalculationJob` валив увесь прогін.
        var profile = await Run(provider);

        Assert.Equal(2, profile.Stats.Single().Rows);
        Assert.Equal(2, written.Count);

        // Зіпсований рядок: числа немає, у трейсі — помилка-значення, а не
        // «#NULL», за яким причини не видно.
        var bad = written.Single(o => o.SourceRowKey == "R1");
        Assert.Empty(bad.Values);
        var steps = bad.Trace.Where(s => s.StepCode == "Total").ToList();
        Assert.NotEmpty(steps);
        Assert.All(steps, s => Assert.Equal(expectedError, s.TraceJson));

        // Сусідній рядок тієї самої прив'язки порахований і записаний.
        var good = written.Single(o => o.SourceRowKey == "R2");
        Assert.Equal(Dec(expectedGood), Assert.Single(good.Values).Value);
    }

    private static decimal Dec(string value)
        => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<ModuleProfile> Run(ServiceProvider provider)
    {
        var orchestrator = new CalculationOrchestrator(
            provider.GetRequiredService<MethodologyResolver>(),
            provider.GetRequiredService<IPeriodStore>(),
            provider.GetRequiredService<IServiceScopeFactory>());

        return await orchestrator.RunAsync(
            calculationRunId: 1,
            DocumentId,
            new PeriodKey(Period),
            [new CalculationBindingRef(TableInstance, MethodologyId)],
            NoOpProgress.Instance,
            CancellationToken.None);
    }

    /// <summary>
    /// Стенд: одна прив'язка, два рядки — <c>R1</c> із «поганими» аргументами,
    /// <c>R2</c> з нормальними; формула <c>Total</c> — водночас і вихід.
    /// </summary>
    private static ServiceProvider Arrange(
        NumericMode mode,
        string expression,
        (decimal A, decimal B) bad,
        (decimal A, decimal B) good,
        List<CalculationOutput> written)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create("Total"), expression);
        formula.SetEvaluationOrder(1);

        var store = Substitute.For<IMethodologyStore>();
        store.GetPublishedVersionsAsync(MethodologyId, Arg.Any<CancellationToken>())
             .Returns([PublishedVersion(mode)]);
        store.GetRulesAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologyRule(VersionId, EcrCode.Create("ALL"), "{}", priority: 10)]);
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns([formula]);
        store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>()).Returns([]);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologyOutput(VersionId, EcrCode.Create("Total"), unitId: 1)]);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, Period, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        var rows = Substitute.For<IRowStore>();
        rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
            .Returns(new TableInstanceRef(TableInstance, DocumentId, 3, TemplateVersion, Period));
        rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, long> { ["R1"] = 1001, ["R2"] = 1002 });

        var cells = Substitute.For<ICellStore>();
        cells.ReadSliceAsync(TableInstance, Arg.Any<CancellationToken>()).Returns(
        [
            Cell(1001, ColumnA, bad.A),
            Cell(1001, ColumnB, bad.B),
            Cell(1002, ColumnA, good.A),
            Cell(1002, ColumnB, good.B),
        ]);

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                TemplateVersion,
                0,
                [],
                new Dictionary<int, ColumnDef> { [ColumnA] = Column("A"), [ColumnB] = Column("B") },
                new Dictionary<(int TableDefId, string RowKey), RowDef>()));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["kg"] = new(1, "kg", 1, 1m) },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var results = Substitute.For<ICalculationResultStore>();
        results.WriteResultsAsync(
                Arg.Any<long>(),
                Arg.Do<IReadOnlyList<CalculationOutput>>(written.AddRange),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(periods);
        services.AddSingleton(rows);
        services.AddSingleton(cells);
        services.AddSingleton(metadata);
        services.AddSingleton(units);
        services.AddSingleton(results);
        services.AddSingleton(Substitute.For<IConstantStore>());
        services.AddSingleton(Substitute.For<ICalculationBindingStore>());
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

    private static CellRecord Cell(long rowId, int columnId, decimal value)
        => new(
            new CellAddress(new PeriodKey(Period), rowId, columnId),
            TableDefId: 3,
            new CellValueData { ValueNumeric = value });

    private static ColumnDef Column(string code)
        => new(
            tableDefId: 3,
            EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }),
            ordinal: 0,
            CellDataType.Decimal);

    /// <summary>Опублікована версія заданого режиму з проставленим ідентифікатором.</summary>
    private static MethodologyVersion PublishedVersion(NumericMode mode)
    {
        var utcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var version = new MethodologyVersion(
            MethodologyId, "1.0.0.0", CalculationLevel.Configuration, createdByUserId: 1, utcNow);

        // ⚠ Режим задається ДО публікації: після неї він заморожений.
        version.SetModes(mode, CalendarMode.Actual, TraceLevel.ErrorsOnly);
        version.Publish(
            publishedByUserId: 2,
            changeReason: "стенд тесту",
            effectiveFrom: new DateOnly(2025, 1, 1),
            testsPassed: true,
            utcNow);

        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(version, VersionId);

        return version;
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
