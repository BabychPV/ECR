// tests/Ecr.Calculations.Tests/MissingInputsRowIssueTests.cs
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
/// AN-164 (live doc 96, LTD-A41 C12_02, 11.11): рядок закрито правилом, але формулам бракує аргументів
/// (<c>Land_Measure_MaximumConsumption?</c>, <c>Density0</c>, … без комірок) — 0 результатів, 0 невдалих кроків (вихід
/// <c>#NULL</c> рахується як «немає значення») і жодної проблеми: тихий нуль. Тепер такий рядок лишає слід —
/// <see cref="ModuleProfile.MissingInputRows"/> (з нього завершення пише проблему <c>MissingInputs</c>).
/// </summary>
/// <remarks>
/// Мутація: прибрати додавання в <c>missingInputs</c> у <c>CalculationOrchestrator.ExecuteAsync</c> (або
/// <c>MissingArguments</c> у <c>GenericCalculationModule.ExecuteAsync</c>) — червоніють перші два тести.
/// </remarks>
public sealed class MissingInputsRowIssueTests
{
    private const long DocumentId = 700;
    private const long TableInstance = 500;
    private const int TemplateVersion = 3;
    private const int Period = 202601;
    private const int FuelColumn = 11;
    private const int QtyColumn = 12;
    private const int Methodology = 42;
    private const int Version = 71;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Рядок_без_необовязкового_аргументу_не_дає_значень_і_лишає_слід()
    {
        var profile = await RunAsync("@Qty? * 2", [5m, null, 7m]);

        // Рядки 1 і 3 дали значення; рядок 2 — ні, бо Qty немає: слід з номером, ключем і методологією.
        Assert.Equal([new RejectedRow(TableInstance, 2, "R001", DocumentId, Methodology)], profile.MissingInputRows);
        Assert.Empty(profile.RejectedRows);
        Assert.Empty(profile.UnmatchedRows);
    }

    /// <summary>
    /// AN-164b: прогін записує, ЯКИХ аргументів бракувало рядку, і вони доживають до <c>ModulesProfileJson</c> — звідти їх
    /// називає читання проблем (а не здогад за формулами). Мутація: не передати <c>MissingArguments</c> у профіль —
    /// червоніє.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Аргументи_яких_бракувало_доживають_до_профілю_прогону()
    {
        var profile = await RunAsync("@Qty? * 2 + @Density? * 0", [null]);

        var row = Assert.Single(profile.MissingInputRows);
        Assert.Equal(["Qty", "Density"], profile.MissingArgumentsOf(row));
        Assert.Equal(
            ["Qty", "Density"],
            ModuleProfile.ReadMissingArguments(profile.ToJson(), DocumentId)[(TableInstance, "R000", Methodology)]);
        Assert.Empty(ModuleProfile.ReadMissingArguments(profile.ToJson(), DocumentId + 1));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Рядок_без_обовязкового_аргументу_ARG_теж_лишає_слід()
    {
        var profile = await RunAsync("@Qty * 2", [null, 3m]);

        Assert.Equal([new RejectedRow(TableInstance, 1, "R000", DocumentId, Methodology)], profile.MissingInputRows);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Усі_аргументи_є_сліду_немає()
    {
        var profile = await RunAsync("@Qty * 2", [1m, 2m]);

        Assert.Empty(profile.MissingInputRows);
    }

    /// <summary>
    /// Рядок без значення, але з усіма аргументами (гілка не обрана: <c>if(..., NULL)</c>) — не «бракує входів»: сліду
    /// <c>MissingInputs</c> немає, бо заповнювати нічого.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Рядок_без_значення_але_з_усіма_аргументами_сліду_не_дає()
    {
        var profile = await RunAsync("if(@Qty > 10, @Qty, NULL)", [1m, 20m]);

        Assert.Empty(profile.MissingInputRows);
    }

    private static async Task<ModuleProfile> RunAsync(string expression, decimal?[] qtyByRow)
    {
        using var provider = Arrange(expression, qtyByRow);
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

    private static ServiceProvider Arrange(string expression, decimal?[] qtyByRow)
    {
        var store = Substitute.For<IMethodologyStore>();
        var periods = Substitute.For<IPeriodStore>();

        var formula = new MethodologyFormula(Version, EcrCode.Create("Total"), expression);
        formula.SetEvaluationOrder(1);

        store.GetPublishedVersionsAsync(Methodology, Arg.Any<CancellationToken>())
             .Returns([PublishedVersion(Methodology, Version)]);
        store.GetRulesAsync(Version, Arg.Any<CancellationToken>())
             .Returns([new MethodologyRule(Version, EcrCode.Create("RULE"), $$"""{"{{FuelColumn}}":"Gas"}""", priority: 10)]);
        store.GetFormulasAsync(Version, Arg.Any<CancellationToken>()).Returns([formula]);
        store.GetSubstancesAsync(Version, Arg.Any<CancellationToken>()).Returns([]);
        store.GetOutputsAsync(Version, Arg.Any<CancellationToken>())
             .Returns([new MethodologyOutput(Version, EcrCode.Create("Total"), unitId: 1)]);

        periods.FindPeriodBoundsAsync(DocumentId, Period, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        var rows = Substitute.For<IRowStore>();
        rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
            .Returns(new TableInstanceRef(TableInstance, DocumentId, 3, TemplateVersion, Period));
        var rowIds = new Dictionary<string, long>();
        var records = new List<CellRecord>();
        for (var i = 0; i < qtyByRow.Length; i++)
        {
            rowIds[$"R{i:000}"] = 1000 + i;
            records.Add(new CellRecord(
                new CellAddress(new PeriodKey(Period), 1000 + i, FuelColumn),
                TableDefId: 3,
                new CellValueData { ValueString = "Gas" }));
            if (qtyByRow[i] is { } qty)
            {
                records.Add(new CellRecord(
                    new CellAddress(new PeriodKey(Period), 1000 + i, QtyColumn),
                    TableDefId: 3,
                    new CellValueData { ValueNumeric = qty }));
            }
        }

        rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(rowIds);

        var cells = Substitute.For<ICellStore>();
        cells.ReadSliceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(records);

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                TemplateVersion, 0, [],
                new Dictionary<int, ColumnDef>
                {
                    [FuelColumn] = Column("Fuel", 0, CellDataType.String),
                    [QtyColumn] = Column("Qty", 1, CellDataType.Decimal),
                },
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

    private static ColumnDef Column(string code, int ordinal, CellDataType type)
        => new(
            tableDefId: 3,
            EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }),
            ordinal,
            type);

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
