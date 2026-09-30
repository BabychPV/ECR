// tests/Ecr.Calculations.Tests/CalculationLevelNoModuleTests.cs
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
/// ФВ-9.2: рівень драбини виразності оголошується явно, і не всі обчислення —
/// формули. Методологія рівня, для якого немає зареєстрованого модуля
/// (<c>CalculationLevel.Module</c> без модуля), — помилка конфігурації
/// <c>ECR-CALC-0422</c> з ключем <c>noModule</c>, а не мовчазний порожній
/// результат.
/// </summary>
/// <remarks>
/// ⛔ «Мовчазний нуль» виглядав би як «викидів немає» у звіті. Контроль:
/// та сама прив'язка рівня <c>Configuration</c> обробляється й пише вихід —
/// без нього відмова на <c>Module</c> була б зеленою й на стенді, який не
/// рахує нічого. Тест іде через справжній <see cref="CalculationOrchestrator"/>
/// (форма стенда — як у <c>DecimalOverflowRunSurvivesTests</c>).
/// </remarks>
public sealed class CalculationLevelNoModuleTests
{
    private const int MethodologyId = 42;
    private const int VersionId = 71;
    private const long DocumentId = 700;
    private const long TableInstance = 500;
    private const int TemplateVersion = 3;
    private const int Period = 202601;
    private const int ColumnA = 21;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.2")]
    public async Task Методологія_рівня_Module_без_зареєстрованого_модуля_відхиляється_noModule()
    {
        var written = new List<CalculationOutput>();
        await using var provider = Arrange(CalculationLevel.Module, written);

        var error = await Assert.ThrowsAsync<DomainException>(() => Run(provider));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0422.noModule", error.Details!["messageKey"]);
        Assert.Equal("Module", error.Details["level"]);

        // Не «нуль»: жодного виходу не записано.
        Assert.Empty(written);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-9.2")]
    public async Task Контроль_методологія_рівня_Configuration_обробляється_модулем_і_пише_вихід()
    {
        var written = new List<CalculationOutput>();
        await using var provider = Arrange(CalculationLevel.Configuration, written);

        var profile = await Run(provider);

        Assert.Equal(1, profile.Stats.Single().Rows);
        Assert.Equal(2m, Assert.Single(Assert.Single(written).Values).Value);
    }

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

    /// <summary>Стенд: одна прив'язка, один рядок, формула <c>Total = @A * 2</c>.</summary>
    private static ServiceProvider Arrange(CalculationLevel level, List<CalculationOutput> written)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create("Total"), "@A * 2");
        formula.SetEvaluationOrder(1);

        var store = Substitute.For<IMethodologyStore>();
        store.GetPublishedVersionsAsync(MethodologyId, Arg.Any<CancellationToken>())
             .Returns([PublishedVersion(level)]);
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
            .Returns(new Dictionary<string, long> { ["R1"] = 1001 });

        var cells = Substitute.For<ICellStore>();
        cells.ReadSliceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(
        [
            new CellRecord(
                new CellAddress(new PeriodKey(Period), 1001, ColumnA),
                TableDefId: 3,
                new CellValueData { ValueNumeric = 1m }),
        ]);

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                TemplateVersion,
                0,
                [],
                new Dictionary<int, ColumnDef>
                {
                    [ColumnA] = new(
                        tableDefId: 3,
                        EcrCode.Create("A"),
                        new LocalizedText(new Dictionary<string, string> { ["en"] = "A" }),
                        ordinal: 0,
                        CellDataType.Decimal),
                },
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

    private static MethodologyVersion PublishedVersion(CalculationLevel level)
    {
        var utcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var version = new MethodologyVersion(MethodologyId, "1.0.0.0", level, createdByUserId: 1, utcNow);

        version.SetModes(NumericMode.Strict, CalendarMode.Actual, TraceLevel.ErrorsOnly);
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
