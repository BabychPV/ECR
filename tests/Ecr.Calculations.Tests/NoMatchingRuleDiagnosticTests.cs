// tests/Ecr.Calculations.Tests/NoMatchingRuleDiagnosticTests.cs
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
/// L-4 (Land Demo): рядок, якому не підійшло жодне правило, не рахується МОВЧКИ.
/// Тепер прогін повертає діагностику «No matching rule … row N» (номер рядка й публічний
/// ключ; значень комірок немає), а решта рядків рахується як і раніше.
/// </summary>
/// <remarks>
/// ⚠ «Не підійшло» — це жодне правило жодної прив'язки цієї таблиці у прогоні: дві методології
/// на одну таблицю з доповняльними правилами (одна бере CO2, інша NOx) не дають діагностики
/// на рядки, які закрила хоч одна з них.
/// Мутація: прибрати підрахунок <c>UnmatchedRows</c> в <c>CalculationOrchestrator.RunAsync</c> —
/// червоні перший і третій тести.
/// </remarks>
public sealed class NoMatchingRuleDiagnosticTests
{
    private const long DocumentId = 700;
    private const long TableInstance = 500;
    private const int TemplateVersion = 3;
    private const int Period = 202601;
    private const int FuelColumn = 11;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-13.9")]
    public async Task Рядок_без_правила_дає_діагностику_з_номером_а_решта_рахується()
    {
        var provider = Arrange([(Methodology: 42, Version: 71, Rule: $$"""{"{{FuelColumn}}":"Gas"}""")]);

        var profile = await Run(provider, [42]);

        // Рядок 1 (Gas) закрило правило; рядки 2 (Diesel) і 3 (Coal) — ні.
        Assert.Equal(
            [new UnmatchedRow(TableInstance, 2, "R001"), new UnmatchedRow(TableInstance, 3, "R002")],
            profile.UnmatchedRows);
        Assert.Equal(1, profile.Stats.Single().Rows);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-13.9")]
    public async Task Усі_рядки_закрито_правилом_діагностики_немає()
    {
        var provider = Arrange([(Methodology: 42, Version: 71, Rule: "{}")]);

        var profile = await Run(provider, [42]);

        Assert.Empty(profile.UnmatchedRows);
        Assert.Equal(3, profile.Stats.Single().Rows);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-13.9")]
    public async Task Доповняльні_правила_двох_методологій_лишають_діагностику_лише_на_нікому_не_закриті()
    {
        var provider = Arrange(
        [
            (Methodology: 42, Version: 71, Rule: $$"""{"{{FuelColumn}}":"Gas"}"""),
            (Methodology: 43, Version: 72, Rule: $$"""{"{{FuelColumn}}":"Diesel"}"""),
        ]);

        var profile = await Run(provider, [42, 43]);

        // Coal (рядок 3) не закрила жодна з двох.
        Assert.Equal([new UnmatchedRow(TableInstance, 3, "R002")], profile.UnmatchedRows);
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

    /// <summary>Три рядки: Gas, Diesel, Coal у колонці палива. Кожна методологія — своє правило.</summary>
    private static ServiceProvider Arrange(
        IReadOnlyList<(int Methodology, int Version, string Rule)> methodologies)
    {
        var store = Substitute.For<IMethodologyStore>();
        var periods = Substitute.For<IPeriodStore>();

        foreach (var (methodology, version, rule) in methodologies)
        {
            var formula = new MethodologyFormula(version, EcrCode.Create("Total"), "1 + 1");
            formula.SetEvaluationOrder(1);

            store.GetPublishedVersionsAsync(methodology, Arg.Any<CancellationToken>())
                 .Returns([PublishedVersion(methodology, version)]);
            store.GetRulesAsync(version, Arg.Any<CancellationToken>())
                 .Returns([new MethodologyRule(version, EcrCode.Create("RULE"), rule, priority: 10)]);
            store.GetFormulasAsync(version, Arg.Any<CancellationToken>()).Returns([formula]);
            store.GetSubstancesAsync(version, Arg.Any<CancellationToken>()).Returns([]);
            store.GetOutputsAsync(version, Arg.Any<CancellationToken>())
                 .Returns([new MethodologyOutput(version, EcrCode.Create("Total"), unitId: 1)]);
        }

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
