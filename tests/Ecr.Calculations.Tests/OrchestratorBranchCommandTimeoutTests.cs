// tests/Ecr.Calculations.Tests/OrchestratorBranchCommandTimeoutTests.cs
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
/// Кожна гілка пакета оркестратора (власний scope і EcrDbContext, Q-249) піднімає таймаут команд через
/// <see cref="IRecalcCommandTimeout"/>: таймаут задачі гілок не торкається, а запис результатів - саме там.
/// </summary>
/// <remarks>
/// Мутація: прибрати Apply() у CalculationOrchestrator - тест червоний (лічильник 0).
/// </remarks>
public sealed class OrchestratorBranchCommandTimeoutTests
{
    private const long DocumentId = 700;
    private const long TableInstance = 500;
    private const int TemplateVersion = 3;
    private const int Period = 202601;
    private const int FuelColumn = 11;
    private const int Published = 42;
    private const int Unpublished = 99;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Кожна_гілка_пакета_піднімає_таймаут_команд_свого_scope()
    {
        var counter = new ApplyCounter();
        var provider = Arrange(counter);

        var profile = await Run(provider, [Published]);

        Assert.Equal(3, profile.Stats.Single().Rows);
        Assert.Equal(1, counter.Applied);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Без_зареєстрованого_порту_перерахунок_працює_як_раніше()
    {
        var provider = Arrange(counter: null);

        var profile = await Run(provider, [Published]);

        Assert.Equal(3, profile.Stats.Single().Rows);
    }

    private sealed class ApplyCounter : IRecalcCommandTimeout
    {
        private int _applied;

        public int Applied => _applied;

        public void Apply() => Interlocked.Increment(ref _applied);
    }

    private static async Task<ModuleProfile> Run(ServiceProvider provider, int[] methodologies)
    {
        var orchestrator = new CalculationOrchestrator(
            provider.GetRequiredService<MethodologyResolver>(),
            provider.GetRequiredService<IPeriodStore>(),
            provider.GetRequiredService<IServiceScopeFactory>());

        return await orchestrator.RunAsync(
            calculationRunId: 1,
            DocumentId,
            new PeriodKey(Period),
            [.. methodologies.Select(m => new CalculationBindingRef(TableInstance, m))],
            NoOpProgress.Instance,
            CancellationToken.None);
    }

    private static ServiceProvider Arrange(ApplyCounter? counter)
    {
        var store = Substitute.For<IMethodologyStore>();
        var periods = Substitute.For<IPeriodStore>();

        const int version = 71;
        var formula = new MethodologyFormula(version, EcrCode.Create("Total"), "1 + 1");
        formula.SetEvaluationOrder(1);

        store.GetPublishedVersionsAsync(Published, Arg.Any<CancellationToken>())
             .Returns([PublishedVersion(Published, version)]);
        store.GetPublishedVersionsAsync(Unpublished, Arg.Any<CancellationToken>())
             .Returns(new List<MethodologyVersion>());
        store.GetRulesAsync(version, Arg.Any<CancellationToken>())
             .Returns([new MethodologyRule(version, EcrCode.Create("RULE"), "{}", priority: 10)]);
        store.GetFormulasAsync(version, Arg.Any<CancellationToken>()).Returns([formula]);
        store.GetSubstancesAsync(version, Arg.Any<CancellationToken>()).Returns([]);
        store.GetOutputsAsync(version, Arg.Any<CancellationToken>())
             .Returns([new MethodologyOutput(version, EcrCode.Create("Total"), unitId: 1)]);

        periods.FindPeriodBoundsAsync(DocumentId, Period, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        var rows = Substitute.For<IRowStore>();
        rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
            .Returns(new TableInstanceRef(TableInstance, DocumentId, 3, TemplateVersion, Period));
        rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, long> { ["R000"] = 1000, ["R001"] = 1001, ["R002"] = 1002 });

        var cells = Substitute.For<ICellStore>();
        cells.ReadSliceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(
        [
            Cell(1000, "Gas"),
            Cell(1001, "Diesel"),
            Cell(1002, "Coal"),
        ]);

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                TemplateVersion, 0, [],
                new Dictionary<int, ColumnDef>(),
                new Dictionary<(int TableDefId, string RowKey), RowDef>()));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["kg"] = new(1, "kg", 1, 1m) },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(periods);
        services.AddSingleton(rows);
        services.AddSingleton(cells);
        services.AddSingleton(metadata);
        services.AddSingleton(units);
        services.AddSingleton(Substitute.For<IConstantStore>());
        services.AddSingleton(Substitute.For<ICalculationBindingStore>());
        services.AddSingleton(Substitute.For<ICalculationResultStore>());
        services.AddSingleton(Substitute.For<IUnitOfWork>());
        services.AddSingleton<IFormulaEngine, RealFormulaEngine>();
        services.AddSingleton<CalendarContext>();
        services.AddScoped<ConstantResolver>();
        services.AddScoped<MethodologyResolver>();
        services.AddScoped<CalculationInputBuilder>();
        services.AddScoped<CalculationOutputWriter>();
        services.AddScoped<ICalculationModule, GenericCalculationModule>();
        if (counter is not null)
        {
            services.AddSingleton<IRecalcCommandTimeout>(counter);
        }

        return services.BuildServiceProvider();
    }

    private static CellRecord Cell(long rowId, string value)
        => new(
            new CellAddress(new PeriodKey(Period), rowId, FuelColumn),
            TableDefId: 3,
            new CellValueData { ValueString = value });

    private static MethodologyVersion PublishedVersion(int methodologyId, int versionId)
    {
        var utcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var version = new MethodologyVersion(
            methodologyId, "1.0.0.0", CalculationLevel.Configuration, createdByUserId: 1, utcNow);

        version.Publish(
            publishedByUserId: 2,
            changeReason: "стенд тесту",
            effectiveFrom: new DateOnly(2025, 1, 1),
            testsPassed: true,
            utcNow);

        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(version, versionId);

        return version;
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}