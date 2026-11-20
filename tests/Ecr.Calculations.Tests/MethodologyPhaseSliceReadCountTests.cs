// tests/Ecr.Calculations.Tests/MethodologyPhaseSliceReadCountTests.cs
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
/// PSS-08: зріз таблиці читається РАЗ НА ПРОГІН фази методологій (документ × період), а не раз на прив'язку.
/// </summary>
/// <remarks>
/// ⛔ Що було. Кожна прив'язка методології до того самого екземпляра таблиці сама читала екземпляр, ключі рядків і
/// зріз комірок (<c>MethodologyResolver.ReadTableAsync</c>): п'ять методологій на одній таблиці Land — п'ять однакових
/// <c>ReadSliceAsync</c>. Між ними фаза методологій у <c>doc.CellValue</c> нічого не пише (лише <c>calc.*</c>), а лок
/// документа тримає задача, тож відповіді однакові.
/// <para>
/// ⚠ Тест міряє прогін ЦІЛКОМ, через <see cref="CalculationOrchestrator"/> з паралельними гілками (кожна — власний
/// scope), а не кеш окремо: вада була в тому, що гілки не ділили прочитаного.
/// </para>
/// </remarks>
public sealed class MethodologyPhaseSliceReadCountTests
{
    private const long DocumentId = 700;
    private const long TableA = 500;
    private const long TableB = 501;
    private const int TemplateVersion = 3;
    private const int Period = 202601;
    private const int Rows = 20;
    private const int Bindings = 5;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "PSS-08")]
    public async Task П_ять_прив_язок_на_одній_таблиці_читають_зріз_один_раз()
    {
        var cells = Substitute.For<ICellStore>();
        var rows = Substitute.For<IRowStore>();
        var periods = Substitute.For<IPeriodStore>();
        var provider = Arrange(periods, cells, rows);

        var profile = await Run(provider, periods, runId: 1, [.. Enumerable.Range(0, Bindings).Select(i => new CalculationBindingRef(TableA, 41 + i))]);

        // Рядки справді пораховано кожною прив'язкою — «одне читання» не через порожній прогін.
        Assert.Equal(Bindings * Rows, profile.Stats.Sum(s => s.Rows));

        await cells.Received(1).ReadSliceAsync(TableA, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        await rows.Received(1).GetRowIdsAsync(TableA, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());

        // Збирач входів теж бере екземпляр з кешу виклику (до PSS-08: 1 + 5 — резолвер і кожна прив'язка).
        await rows.Received(1).ResolveTableInstanceAsync(TableA, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "PSS-08")]
    public async Task Різні_екземпляри_таблиць_читаються_кожен_своїм_зрізом()
    {
        // ⛔ Ключ кешу — екземпляр (і період): зріз таблиці A не сміє віддатися прив'язці на таблиці B.
        var cells = Substitute.For<ICellStore>();
        var rows = Substitute.For<IRowStore>();
        var periods = Substitute.For<IPeriodStore>();
        var provider = Arrange(periods, cells, rows);

        cells.ReadSliceAsync(TableA, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(SliceOf(TableA, value: 2m));
        cells.ReadSliceAsync(TableB, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(SliceOf(TableB, value: 3m));

        var writer = provider.GetRequiredService<ICalculationResultStore>();
        await Run(provider, periods, runId: 1,
        [
            new CalculationBindingRef(TableA, 41),
            new CalculationBindingRef(TableB, 42),
            new CalculationBindingRef(TableA, 43),
            new CalculationBindingRef(TableB, 44),
        ]);

        await cells.Received(1).ReadSliceAsync(TableA, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        await cells.Received(1).ReadSliceAsync(TableB, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());

        // Числа: таблиця A — 2 × 10, таблиця B — 3 × 10 (формула `@Qty * 10`), без змішування.
        var written = WrittenOutputs(writer);
        Assert.All(written.Where(o => o.TableInstanceId == TableA), o => Assert.Equal(20m, o.Values.Single().Value));
        Assert.All(written.Where(o => o.TableInstanceId == TableB), o => Assert.Equal(30m, o.Values.Single().Value));
        Assert.Equal(4 * Rows, written.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "PSS-08")]
    public async Task Наступний_прогін_читає_зріз_заново_а_не_з_кешу_попереднього()
    {
        // ⛔ Кеш живе один виклик фази методологій (документ × період). Між викликами задача пише формули наступного
        // періоду, а перегляд місячних колонок збирає зріз з кількох періодів — старий зріз тут дав би чужі числа.
        var cells = Substitute.For<ICellStore>();
        var rows = Substitute.For<IRowStore>();
        var periods = Substitute.For<IPeriodStore>();
        var provider = Arrange(periods, cells, rows);
        var writer = provider.GetRequiredService<ICalculationResultStore>();

        cells.ReadSliceAsync(TableA, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(SliceOf(TableA, value: 2m), SliceOf(TableA, value: 5m));

        var orchestrator = Orchestrator(provider, periods);
        IReadOnlyList<CalculationBindingRef> bindings = [new(TableA, 41), new(TableA, 42)];
        await orchestrator.RunAsync(1, DocumentId, new PeriodKey(Period), bindings, NoOpProgress.Instance, CancellationToken.None);
        var first = WrittenOutputs(writer);
        writer.ClearReceivedCalls();

        // Той самий прогін (той самий Id — так кличе задача на кожен документ × період), новий виклик.
        await orchestrator.RunAsync(1, DocumentId, new PeriodKey(Period), bindings, NoOpProgress.Instance, CancellationToken.None);
        var second = WrittenOutputs(writer);

        await cells.Received(2).ReadSliceAsync(TableA, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        Assert.All(first, o => Assert.Equal(20m, o.Values.Single().Value));
        Assert.All(second, o => Assert.Equal(50m, o.Values.Single().Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "PSS-08")]
    public async Task Зріз_понад_бюджет_пам_яті_не_утримується_а_читається_знову()
    {
        // ⚠ Кеш тримає не більше MaxParallelism × MaxInputCellsPerBinding комірок (тут 1 × 20). Зріз A (20) вміщається,
        // зріз B — уже ні: його друга прив'язка читає сама, як до PSS-08, і числа від цього не змінюються.
        var cells = Substitute.For<ICellStore>();
        var rows = Substitute.For<IRowStore>();
        var periods = Substitute.For<IPeriodStore>();
        var provider = Arrange(periods, cells, rows);
        var writer = provider.GetRequiredService<ICalculationResultStore>();

        cells.ReadSliceAsync(TableA, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(SliceOf(TableA, value: 2m));
        cells.ReadSliceAsync(TableB, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(SliceOf(TableB, value: 3m));

        var orchestrator = new CalculationOrchestrator(
            provider.GetRequiredService<MethodologyResolver>(),
            periods,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new CalculationLimits { MaxParallelism = 1, MaxInputCellsPerBinding = Rows });

        await orchestrator.RunAsync(
            1,
            DocumentId,
            new PeriodKey(Period),
            [new(TableA, 41), new(TableB, 42), new(TableA, 43), new(TableB, 44)],
            NoOpProgress.Instance,
            CancellationToken.None);

        await cells.Received(1).ReadSliceAsync(TableA, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        await cells.Received(2).ReadSliceAsync(TableB, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());

        var written = WrittenOutputs(writer);
        Assert.Equal(4 * Rows, written.Count);
        Assert.All(written.Where(o => o.TableInstanceId == TableA), o => Assert.Equal(20m, o.Values.Single().Value));
        Assert.All(written.Where(o => o.TableInstanceId == TableB), o => Assert.Equal(30m, o.Values.Single().Value));
    }

    private static List<CalculationOutput> WrittenOutputs(ICalculationResultStore writer)
        => [.. writer.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICalculationResultStore.WriteResultsAsync))
            .SelectMany(c => c.GetArguments())
            .OfType<IEnumerable<CalculationOutput>>()
            .SelectMany(o => o)];

    /// <summary>Зріз: кожен рядок має <c>Qty = value</c>; рядки таблиці B мають інші Id, ніж рядки A.</summary>
    private static IReadOnlyList<CellRecord> SliceOf(long tableInstanceId, decimal value)
        => [.. Enumerable.Range(0, Rows).Select(i => new CellRecord(
            new CellAddress(new PeriodKey(Period), RowIdOf(tableInstanceId, i), QtyColumn),
            TableDef,
            new CellValueData { ValueNumeric = value }))];

    private static long RowIdOf(long tableInstanceId, int index) => (tableInstanceId * 10_000) + index;

    private const int QtyColumn = 9;
    private const int TableDef = 3;

    private static CalculationOrchestrator Orchestrator(ServiceProvider provider, IPeriodStore periods)
        => new(
            provider.GetRequiredService<MethodologyResolver>(),
            periods,
            provider.GetRequiredService<IServiceScopeFactory>());

    private static Task<ModuleProfile> Run(
        ServiceProvider provider, IPeriodStore periods, long runId, IReadOnlyList<CalculationBindingRef> bindings)
        => Orchestrator(provider, periods).RunAsync(
            runId, DocumentId, new PeriodKey(Period), bindings, NoOpProgress.Instance, CancellationToken.None);

    /// <summary>Стенд: справжні резолвер, збирач входів, модуль і писар — підмінені лише сховища.</summary>
    private static ServiceProvider Arrange(IPeriodStore periods, ICellStore cells, IRowStore rows)
    {
        var store = Substitute.For<IMethodologyStore>();
        for (var i = 0; i < Bindings; i++)
        {
            var methodologyId = 41 + i;
            var versionId = 71 + i;
            var formula = new MethodologyFormula(versionId, EcrCode.Create("Total"), "@Qty * 10");
            formula.SetEvaluationOrder(1);

            store.GetPublishedVersionsAsync(methodologyId, Arg.Any<CancellationToken>())
                 .Returns([PublishedVersion(methodologyId, versionId)]);
            store.GetRulesAsync(versionId, Arg.Any<CancellationToken>())
                 .Returns([new MethodologyRule(versionId, EcrCode.Create("ALL"), "{}", priority: 10)]);
            store.GetFormulasAsync(versionId, Arg.Any<CancellationToken>()).Returns([formula]);
            store.GetSubstancesAsync(versionId, Arg.Any<CancellationToken>()).Returns([]);
            store.GetOutputsAsync(versionId, Arg.Any<CancellationToken>())
                 .Returns([new MethodologyOutput(versionId, EcrCode.Create("Total"), unitId: 1)]);
        }

        periods.FindPeriodBoundsAsync(DocumentId, Period, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        foreach (var table in new[] { TableA, TableB })
        {
            rows.ResolveTableInstanceAsync(table, Arg.Any<CancellationToken>())
                .Returns(new TableInstanceRef(table, DocumentId, TableDef, TemplateVersion, Period));
            var instance = table;
            rows.GetRowIdsAsync(table, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                .Returns(Enumerable.Range(0, Rows).ToDictionary(i => $"R{i:D3}", i => RowIdOf(instance, i)));
            cells.ReadSliceAsync(table, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(SliceOf(table, 1m));
        }

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                TemplateVersion, 0, [],
                new Dictionary<int, ColumnDef> { [QtyColumn] = Column() },
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

    private static ColumnDef Column()
        => new(
            tableDefId: TableDef,
            EcrCode.Create("Qty"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Qty" }),
            ordinal: 0,
            CellDataType.Decimal);

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
