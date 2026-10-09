// tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotLayoutBudgetTests.cs
using System.Data.Common;
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// AN-120 / L1-02: розкладений зріз (<c>R8</c>), що не влазить у бюджет кешу,
/// не перечитується цілком на кожну сторінку, а одночасні промахи читають його
/// один раз.
/// </summary>
/// <remarks>
/// Як і в <c>ReportSnapshotLayoutCacheTests</c>: кожен «запит» — окремий
/// <see cref="EcrDbContext"/> і окремий будівник поверх ОДНОГО
/// <see cref="IMemoryCache"/>, читання рахує <see cref="DbCommandCounter"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportSnapshotLayoutBudgetTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Категорія читання комірок зрізу в лічильнику.</summary>
    private const string ReadCells = "SELECT rpt.ReportRow";

    private static readonly ReportColumnCommand[] Columns =
    [
        new("OutputCode", "text"),
        new("Value", "number"),
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L1-02")]
    public async Task Зріз_понад_бюджет_кешується_без_комірок_і_сторінка_дочитує_лише_свої_рядки()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var snapshotId = await BuildGroupedAsync(chain);
        using var memory = new MemoryCache(new MemoryCacheOptions());

        // Зріз: 2 рядки × 2 колонки = 4 комірки, плюс 2 RowNo і 2 групи. Цілком
        // (8) у бюджет 5 не влазить, без комірок (4) — влазить.
        const int budget = 5;

        var first = await CountedAsync(
            memory, budget, b => b.RowsAsync(snapshotId, 0, 1, "en", CancellationToken.None));

        Assert.True(first.Seen[ReadCells] == 1, first.Seen.Format());
        Assert.Equal("E_CO2", Assert.Single(first.Page!.Rows).Cells["OutputCode"]);

        // ⛔ Мутаційний доказ: до AN-120 зріз понад бюджет не кешувався ЗОВСІМ
        // (`Remember` повертався без запису) — тут було б 0.
        Assert.Equal(1, memory.Count);

        var second = await CountedAsync(
            memory, budget, b => b.RowsAsync(snapshotId, first.Page.NextCursor!.Value, 1, "en", CancellationToken.None));

        // Одне читання — комірки лише рядків сторінки, і та сама відповідь, що й
        // з повного кешу: порядок груп і підсумок по ВСЬОМУ зрізу.
        Assert.True(second.Seen[ReadCells] == 1, second.Seen.Format());
        Assert.Equal("E_NOX", Assert.Single(second.Page!.Rows).Cells["OutputCode"]);
        Assert.Equal(5m, second.Page.Rows[0].Cells["Value"]);
        Assert.Null(second.Page.NextCursor);
        Assert.Equal(17.5m, Assert.Single(second.Page.Totals!).Value);
        Assert.Equal(["E_CO2", "E_NOX"], second.Page.Groups!.Select(g => g.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L1-02")]
    public async Task Одночасні_промахи_читають_зріз_один_раз()
    {
        const int callers = 5;

        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var snapshotId = await BuildGroupedAsync(chain);
        using var memory = new MemoryCache(new MemoryCacheOptions());

        var tally = new CommandTally();

        // ⚠ Ворота тримають ПЕРШЕ читання комірок, доки до них не дійдуть усі
        // п'ять викликачів або не мине запас часу. Без single-flight до воріт
        // доходять усі п'ять (5 читань); з ним — лише власник польоту, решта
        // чекають його результату. Тобто число не залежить від того, як
        // планувальник розклав потоки.
        var gate = new CellsGate(callers, TimeSpan.FromSeconds(2));

        var pages = await Task.WhenAll(Enumerable.Range(0, callers).Select(_ => Task.Run(async () =>
        {
            await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
                .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
                .AddInterceptors(new DbCommandCounter(tally), gate)
                .Options);

            return await new ReportSnapshotBuilder(db, new TestClock(Now), memory)
                .RowsAsync(snapshotId, 0, 10, "en", CancellationToken.None);
        })));

        // ⛔ Мутаційний доказ: прибери `SingleFlight` з `LaidOutAsync` — тут стане 5.
        var seen = tally.Snapshot();
        Assert.True(seen[ReadCells] == 1, seen.Format());

        Assert.All(pages, page => Assert.Equal(["E_CO2", "E_NOX"], page!.Rows.Select(r => r.Cells["OutputCode"])));
    }

    private async Task<(SnapshotRowsPage? Page, CommandTallySnapshot Seen)> CountedAsync(
        IMemoryCache memory, int budget, Func<ReportSnapshotBuilder, Task<SnapshotRowsPage?>> act)
    {
        var counter = new DbCommandCounter();
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .AddInterceptors(counter)
            .Options);

        var page = await act(new ReportSnapshotBuilder(db, new TestClock(Now), memory) { LaidOutCacheBudget = budget });

        return (page, counter.Tally.Snapshot());
    }

    /// <summary>Зріз двох результатів, згрупований за кодом із сумою значень.</summary>
    private static async Task<long> BuildGroupedAsync(TestDocumentBuilder chain)
    {
        await using var db = chain.CreateContext();
        var document = await chain.BuildAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];

        var methodology = new Methodology(
            EcrCode.Create($"RPTB_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var methodologyVersion = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.Add(methodologyVersion);

        var run = new CalculationRun(document.ProjectId, document.PeriodKey.Value, triggeredByUserId: null, Now);
        run.Complete("Succeeded", Now, "{}", errorMessage: null);
        run.MakeCurrent();
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();

        var unit = await db.Units.AsNoTracking().OrderBy(u => u.Id).FirstAsync();

        foreach (var (rowKey, output, value) in new[] { ("row-1", "E_CO2", 12.5m), ("row-2", "E_NOX", 5m) })
        {
            var text = value.ToString(CultureInfo.InvariantCulture);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO calc.CalculationResult
                    (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
                VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {methodologyVersion.Id},
                        {document.PeriodKey.Value}, {document.DocumentId}, {rowKey}, {output},
                        CAST({text} AS decimal(34,16)), {unit.Id})
                """);
        }

        var def = new ReportDef(
            EcrCode.Create($"RPB{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Layout budget test" }),
            isRegulatory: true);
        db.ReportDefs.Add(def);
        await db.SaveChangesAsync();

        var version = new ReportVersion(
            def.Id,
            "1.0",
            ReportDefinitionSpec.ColumnsJson(Columns),
            ReportDefinitionSpec.RulesJson(
                new("CalculationResults", Layout: new("OutputCode", [new("Value", "sum")])), Columns),
            Now);
        version.Publish();
        db.ReportVersions.Add(version);
        await db.SaveChangesAsync();

        return await new ReportSnapshotBuilder(db, new TestClock(Now), new MemoryCache(new MemoryCacheOptions()))
            .BuildAsync(version.Id, document.ProjectId, document.PeriodKey, null, CancellationToken.None);
    }

    /// <summary>Тримає читання комірок зрізу, доки до нього не дійдуть усі викликачі або не мине запас.</summary>
    private sealed class CellsGate(int expected, TimeSpan patience) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(CommandCategory.Of(command.CommandText), ReadCells, StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _arrived) >= expected)
                {
                    _all.TrySetResult();
                }

                await Task.WhenAny(_all.Task, Task.Delay(patience, cancellationToken)).ConfigureAwait(false);
            }

            return result;
        }
    }
}
