// tests/Ecr.Calculations.Tests/CategoryRuleRowRejectionTests.cs
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
/// L2-6 (регресія Land RC12): помилка правила категорії на ОДНОМУ рядку відмовляє ЛИШЕ цьому рядку
/// (дизайн <c>DESIGN-category-rule.md</c> §5), а не валить перерахунок усього документа.
/// </summary>
/// <remarks>
/// Документ із трьох рядків: у другого порожній ключ категорії. Було: виняток <c>ECR-CALC-0422
/// categoryRuleFailed</c> летів крізь оркестратор, 0 результатів навіть для справних рядків. Має бути:
/// рядки 1 і 3 пораховані, рядок 2 відхилений з номером.
/// Мутація: прибрати <c>catch</c> відмови рядка в <c>CalculationOrchestrator.ExecuteAsync</c> — червоні обидва тести.
/// </remarks>
public sealed class CategoryRuleRowRejectionTests
{
    private const long DocumentId = 700;
    private const long TableInstance = 500;
    private const int TemplateVersion = 3;
    private const int Period = 202601;
    private const int FuelColumn = 11;
    private const int Methodology = 42;
    private const int Version = 71;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Порожній_ключ_категорії_одного_рядка_не_валить_решту_рядків()
    {
        var profile = await RunAsync(["Gas", "", "Coal"]);

        // Рядки 1 і 3 пораховані, прогін не впав.
        Assert.Equal(2, profile.Stats.Single().Rows);
        Assert.Empty(profile.UnmatchedRows);

        // Рядок 2 відхилено з номером і публічним ключем; значень комірок у діагностиці немає.
        Assert.Equal([new RejectedRow(TableInstance, 2, "R001")], profile.RejectedRows);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Усі_ключі_справні_відмов_немає()
    {
        var profile = await RunAsync(["Gas", "Diesel", "Coal"]);

        Assert.Equal(3, profile.Stats.Single().Rows);
        Assert.Empty(profile.RejectedRows);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Кілька_відхилених_рядків_ідуть_за_порядком_номерів()
    {
        var profile = await RunAsync(["", "Diesel", ""]);

        Assert.Equal(1, profile.Stats.Single().Rows);
        Assert.Equal(
            [new RejectedRow(TableInstance, 1, "R000"), new RejectedRow(TableInstance, 3, "R002")],
            profile.RejectedRows);
    }

    private static async Task<ModuleProfile> RunAsync(string[] fuelByRow)
    {
        using var provider = Arrange(fuelByRow);
        var orchestrator = new CalculationOrchestrator(
            provider.GetRequiredService<MethodologyResolver>(),
            provider.GetRequiredService<IPeriodStore>(),
            provider.GetRequiredService<IServiceScopeFactory>());

        return await orchestrator.RunAsync(
            calculationRunId: 1,
            DocumentId,
            new PeriodKey(Period),
            [new CalculationBindingRef(TableInstance, Methodology)],
            NoOpProgress.Instance,
            CancellationToken.None);
    }

    private static ServiceProvider Arrange(string[] fuelByRow)
    {
        var store = Substitute.For<IMethodologyStore>();
        var periods = Substitute.For<IPeriodStore>();

        var formula = new MethodologyFormula(Version, EcrCode.Create("Total"), "1 + 1");
        formula.SetEvaluationOrder(1);

        store.GetPublishedVersionsAsync(Methodology, Arg.Any<CancellationToken>())
             .Returns([PublishedVersion(Methodology, Version)]);
        store.GetRulesAsync(Version, Arg.Any<CancellationToken>())
             .Returns([new MethodologyRule(Version, EcrCode.Create("RULE"), "{}", priority: 10)]);
        store.GetFormulasAsync(Version, Arg.Any<CancellationToken>()).Returns([formula]);
        store.GetSubstancesAsync(Version, Arg.Any<CancellationToken>()).Returns([]);
        store.GetOutputsAsync(Version, Arg.Any<CancellationToken>())
             .Returns([new MethodologyOutput(Version, EcrCode.Create("Total"), unitId: 1)]);
        store.GetCategoryRuleAsync(Version, Arg.Any<CancellationToken>()).Returns("@Fuel");

        periods.FindPeriodBoundsAsync(DocumentId, Period, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        var rows = Substitute.For<IRowStore>();
        rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
            .Returns(new TableInstanceRef(TableInstance, DocumentId, 3, TemplateVersion, Period));
        var rowIds = new Dictionary<string, long>();
        var records = new List<CellRecord>();
        for (var i = 0; i < fuelByRow.Length; i++)
        {
            rowIds[$"R{i:000}"] = 1000 + i;
            records.Add(new CellRecord(
                new CellAddress(new PeriodKey(Period), 1000 + i, FuelColumn),
                TableDefId: 3,
                new CellValueData { ValueString = fuelByRow[i] }));
        }

        rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(rowIds);

        var cells = Substitute.For<ICellStore>();
        cells.ReadSliceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(records);

        var fuel = new ColumnDef(
            tableDefId: 3,
            EcrCode.Create("Fuel"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Fuel" }),
            ordinal: 0,
            CellDataType.String);

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                TemplateVersion, 0, [],
                new Dictionary<int, ColumnDef> { [FuelColumn] = fuel },
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
