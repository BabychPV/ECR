// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.BatchCost.cs
using System.Data.Common;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Hotfix «integ-migrate-batch»: перенос 4.5 млн комірок падав по Execution Timeout 300 с. Сторож: вартість пачки
/// комірок не повинна рости з кількістю вже перенесених (на 210 тис. комірок ранні й пізні пачки читають однаково).
/// </summary>
/// <remarks>
/// ⚠ Тест не міряє секунди: він порівнює ЛОГІЧНІ ЧИТАННЯ (sys.dm_exec_sessions.logical_reads) пізніх і
/// ранніх пачок UPDATE комірок. Без курсора за ключем пізні пачки читають у рази більше за ранні.
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    private const int CostVolumeRows = 70_000;   // 70 тис. рядків × 3 = 210 тис. комірок = 42+ пачок

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Вартість_пачки_комірок_не_росте_з_кількістю_уже_перенесених_і_результат_еталонний()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var targetRows = await TargetRowDefsAsync(s).ConfigureAwait(true);
        await BulkInsertAsync(s, CostVolumeRows).ConfigureAwait(true);
        var expectedValues = (CostVolumeRows * 3L) + 2;

        var plan = new VersionMigrationPlan(
            new Dictionary<int, int> { [s.Doc.SheetDefId] = s.TargetSheetDefId },
            new Dictionary<int, int> { [s.Doc.TableDefId] = s.TargetTableDefId },
            [
                new VersionMigrationColumn(s.Doc.ColumnDefIds[0], s.TargetColumns["C1"], s.TargetTableDefId),
                new VersionMigrationColumn(s.Doc.ColumnDefIds[1], s.TargetColumns["C2"], s.TargetTableDefId),
                new VersionMigrationColumn(s.Doc.ColumnDefIds[2], s.TargetColumns["C3"], s.TargetTableDefId),
            ],
            new Dictionary<int, int> { [s.Doc.RowDefIds[0]] = targetRows[0], [s.Doc.RowDefIds[1]] = targetRows[1] },
            new Dictionary<int, int>(), [], [], expectedValues, 0, 0);

        var recorder = new ReadsRecorder();
        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString).AddInterceptors(recorder).Options))
        {
            await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true);
            await new DocumentVersionMigrationStore(db)
                .ApplyAsync(s.Doc.ProjectId, s.TargetVersionId, plan, CancellationToken.None).ConfigureAwait(true);
            await tx.CommitAsync().ConfigureAwait(true);
        }

        // Пачки UPDATE комірок (без останньої — вона неповна, лише дочищає хвіст).
        var reads = recorder.CellUpdateReads;
        Assert.True(reads.Count >= 20, $"Замало пачок для висновку: {reads.Count}.");
        var full = reads.Take(reads.Count - 1).ToList();
        var quarter = Math.Max(3, full.Count / 4);
        var early = full.Take(quarter).Average();
        var late = full.Skip(full.Count - quarter).Average();
        Console.WriteLine(FormattableString.Invariant(
            $"BATCH-COST batches={reads.Count} earlyAvgReads={early:F0} lateAvgReads={late:F0} ratio={late / early:F2}"));

        // ⛔ Предмет тесту: пізні пачки не дорожчі за ранні більш ніж у 2 рази (до фіксу — у рази повільніше).
        Assert.True(late <= early * 2, $"Вартість пачки росте: ранні {early:F0} читань, пізні {late:F0}.");

        // Результат еталонний: усе на нових колонках, нічого не лишилось на старих, рядки перенесено.
        await using var check = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var moved = await check.CellValues.AsNoTracking()
            .CountAsync(c => c.PeriodKeyValue == s.Doc.PeriodKey.Value && c.TableDefId == s.TargetTableDefId).ConfigureAwait(true);
        Assert.Equal(expectedValues + 1, moved);
        var left = await check.CellValues.AsNoTracking()
            .CountAsync(c => c.PeriodKeyValue == s.Doc.PeriodKey.Value && c.TableDefId == s.Doc.TableDefId).ConfigureAwait(true);
        Assert.Equal(0, left);
        var movedRows = await check.TableRows.AsNoTracking()
            .CountAsync(r => r.PeriodKeyValue == s.Doc.PeriodKey.Value && r.TableInstanceId == s.Doc.TableInstanceId
                             && r.RowDefId == targetRows[0]).ConfigureAwait(true);
        Assert.Equal(CostVolumeRows, movedRows);

    }

    /// <summary>Після кожної команди UPDATE комірок записує приріст logical_reads сесії.</summary>
    private sealed class ReadsRecorder : DbCommandInterceptor
    {
        private readonly List<long> _reads = [];
        private long _last = -1;

        public IReadOnlyList<long> CellUpdateReads => _reads;

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            var current = await SessionReadsAsync(command, cancellationToken).ConfigureAwait(false);
            if (_last >= 0
                && command.CommandText.Contains("UPDATE TOP", StringComparison.Ordinal)
                && command.CommandText.Contains("ColumnDefId = m.n", StringComparison.Ordinal)
                && command.CommandText.Contains("doc.CellValue", StringComparison.Ordinal))
            {
                _reads.Add(current - _last);
            }

            _last = current;
            return await base.NonQueryExecutedAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<long> SessionReadsAsync(DbCommand command, CancellationToken ct)
        {
            await using var probe = command.Connection!.CreateCommand();
            probe.Transaction = command.Transaction;
            probe.CommandText = "SELECT logical_reads FROM sys.dm_exec_sessions WHERE session_id = @@SPID";
            return Convert.ToInt64(await probe.ExecuteScalarAsync(ct).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
