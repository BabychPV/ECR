// tests/Ecr.Calculations.Tests/ConstantReadsPerBindingTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Константи методології читаються з бази РАЗ НА ПРИВ'ЯЗКУ, а не на кожен
/// рядок × речовину × код (аудит P1).
/// </summary>
/// <remarks>
/// ⛔ Що було. <c>CAL-06</c> переніс склад версії (формули, речовини, виходи)
/// на рівень прив'язки, а константи лишились на рівні рядка:
/// <c>GenericCalculationModule.ExecuteAsync</c> на кожну речовину кожного
/// рядка кликав <c>ConstantResolver.ResolveAsync</c> на кожен код, і той щоразу
/// йшов у <c>ConstantStore.GetCandidatesAsync</c> окремим <c>SELECT</c>.
/// 500 рядків × 20 речовин × 30 констант — 300 тис. запитів на ОДНУ прив'язку.
///
/// ⚠ Міряє лічильник EF (<see cref="DbCommandCounter"/>) над СПРАВЖНІМИ
/// <see cref="MethodologyStore"/> і <see cref="ConstantStore"/>: усі звернення
/// цього шляху випускає EF. Решта портів підмінена — тож у числі лише
/// звернення методології, за які й відповідає цей рядок роботи. Прогін іде
/// цілком, через <see cref="CalculationOrchestrator"/>: вада була в тому, хто
/// і скільки разів кличе модуль, а не в самому модулі.
/// </remarks>
[Collection("SqlServer")]
public sealed class ConstantReadsPerBindingTests(SqlServerFixture sql)
{
    private const long DocumentId = 7_700;
    private const long TableInstance = 7_701;
    private const int TemplateVersion = 3;
    private const int Period = 202601;
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// 3 і 30 рядків дають ОДНАКОВЕ число SQL-команд, і константи серед них —
    /// рівно одна.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація, що валить тест: повернути в <c>ExecuteAsync</c> похід у
    /// сховище за кодом (<c>constants.ResolveAsync</c>) — на 3 рядках це
    /// 3 × 2 = 6 запитів констант, на 30 — 60, і рівність розвалюється.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Число_SQL_команд_прогону_не_залежить_від_кількості_рядків()
    {
        var methodologyId = await SeedAsync();

        var few = await MeasureAsync(methodologyId, rowCount: 3);
        var many = await MeasureAsync(methodologyId, rowCount: 30);

        // ⛔ Рядки справді порахувались, і константи в них справді дійшли:
        // «мало запитів» досягалось би й тим, що прогін не порахував нічого
        // або формула отримала `#REF` замість константи.
        Assert.Equal(3, few.Rows);
        Assert.Equal(30, many.Rows);
        Assert.Equal(3, few.Values.Count);
        Assert.Equal(30, many.Values.Count);
        Assert.All(few.Values.Concat(many.Values), v => Assert.Equal(3.25m, v));

        // Головне твердження P1.
        Assert.True(
            few.Tally.Total == many.Tally.Total,
            "Число звернень залежить від кількості рядків.\n3 рядки:\n"
            + few.Tally.Format() + "\n30 рядків:\n" + many.Tally.Format());

        // ⚠ Константи версії — одним запитом на прив'язку, скільки б кодів не
        // згадували формули (тут їх два).
        Assert.Equal(1, many.Tally["SELECT calc.MethodologyConstant"]);
    }

    /// <summary>Прогін однієї прив'язки на <paramref name="rowCount"/> рядків.</summary>
    private async Task<Measurement> MeasureAsync(int methodologyId, int rowCount)
    {
        var counter = new DbCommandCounter();
        var options = new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .AddInterceptors(counter)
            .Options;

        var written = new List<CalculationOutput>();
        var results = Substitute.For<ICalculationResultStore>();
        results
            .WriteResultsAsync(
                Arg.Any<long>(),
                Arg.Do<IReadOnlyList<CalculationOutput>>(o => written.AddRange(o)),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, Period, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        await using var provider = BuildProvider(options, periods, results, rowCount);

        var orchestrator = new CalculationOrchestrator(
            provider.GetRequiredService<MethodologyResolver>(),
            periods,
            provider.GetRequiredService<IServiceScopeFactory>());

        counter.Tally.Reset();

        var profile = await orchestrator.RunAsync(
            calculationRunId: 1,
            DocumentId,
            new PeriodKey(Period),
            [new CalculationBindingRef(TableInstance, methodologyId)],
            NoOpProgress.Instance,
            CancellationToken.None);

        return new Measurement(
            profile.Stats.Single().Rows,
            [.. written.SelectMany(o => o.Values).Select(v => v.Value)],
            counter.Tally.Snapshot());
    }

    /// <summary>
    /// Стенд: справжні сховища методології й констант над тестовою базою,
    /// підмінене все інше.
    /// </summary>
    private static ServiceProvider BuildProvider(
        DbContextOptions<EcrDbContext> options,
        IPeriodStore periods,
        ICalculationResultStore results,
        int rowCount)
    {
        var rows = Substitute.For<IRowStore>();
        rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
            .Returns(new TableInstanceRef(TableInstance, DocumentId, 3, TemplateVersion, Period));
        rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(0, rowCount).ToDictionary(i => $"R{i:D3}", i => (long)(1000 + i)));

        var cells = Substitute.For<ICellStore>();
        cells.ReadSliceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns([]);

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
        services.AddScoped(_ => new EcrDbContext(options));
        services.AddScoped<IMethodologyStore>(sp => new MethodologyStore(sp.GetRequiredService<EcrDbContext>()));
        services.AddScoped<IConstantStore>(sp => new ConstantStore(sp.GetRequiredService<EcrDbContext>()));
        services.AddSingleton(periods);
        services.AddSingleton(rows);
        services.AddSingleton(cells);
        services.AddSingleton(metadata);
        services.AddSingleton(units);
        services.AddSingleton(results);
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

    /// <summary>
    /// Опублікована методологія: <c>tons = CST.k1 * 2 + CST.k2</c>, дві загальні
    /// константи, правило «усі рядки».
    /// </summary>
    private async Task<int> SeedAsync()
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options);

        var unit = await db.Units.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];

        var methodology = new Methodology(
            EcrCode.Create($"P1_{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = $"P1 {tag}" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(
            methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        db.MethodologyFormulas.Add(version.AddFormula(
            EcrCode.Create("tons"), "CST.k1 * 2 + CST.k2", FormulaResultType.Number, unit));
        db.MethodologyConstants.Add(version.AddNumericConstant(EcrCode.Create("k1"), 1.5m, unit));
        db.MethodologyConstants.Add(version.AddNumericConstant(EcrCode.Create("k2"), 0.25m, unit));
        db.MethodologyOutputs.Add(version.AddOutput(EcrCode.Create("tons"), unit, 1));
        db.MethodologyRules.Add(version.AddRule(EcrCode.Create("all"), "{}", 10));

        // Чотири очі (D-40): публікує інший користувач, ніж автор.
        version.Publish(publishedByUserId: 2, "P1", new DateOnly(2025, 1, 1), testsPassed: true, Now);
        await db.SaveChangesAsync();

        return methodology.Id;
    }

    private sealed record Measurement(int Rows, IReadOnlyList<decimal> Values, CommandTallySnapshot Tally);

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
