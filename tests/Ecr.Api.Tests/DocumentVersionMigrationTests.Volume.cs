// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.Volume.cs
using System.Data.Common;
using System.Globalization;
using System.Text;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// N-3 (регресія Land RC11): перенос версії на проєкті з ~138 тис. значень падав у <c>500</c> —
/// єдиний пакет SQL <c>ApplyAsync</c> не вкладався в глобальний <c>CommandTimeout</c> (60 с).
/// </summary>
/// <remarks>
/// ⚠ Тест не міряє секунди (плаваючий): він рахує КОМАНДИ. Фікс — перенос іде пачками
/// не більше <see cref="DocumentVersionMigrationStore.BatchRows"/> рядків на команду й кожна
/// команда несе явний <see cref="DocumentVersionMigrationStore.ApplyCommandTimeoutSeconds"/>,
/// а не глобальний. До фіксу весь перенос був однією командою з таймаутом 60 с.
/// Обсяг понад десяток тисяч значень задає змінна <c>ECR_MIGRATION_VOLUME_ROWS</c> (рядків по 3 значення;
/// 46000 рядків ≈ 138 тис. значень Land RC11) — для ручного заміру часу.
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    private const int GlobalCommandTimeoutSeconds = 60;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Перенос_великого_обсягу_йде_пачками_з_подовженим_таймаутом_і_нічого_не_губить()
    {
        var volumeRows = int.TryParse(
            Environment.GetEnvironmentVariable("ECR_MIGRATION_VOLUME_ROWS"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var configured) && configured > 0
            ? configured
            : (DocumentVersionMigrationStore.BatchRows * 2) + 1_000;   // 11 тис. рядків = 33 тис. значень = 7+ пачок

        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var targetRows = await TargetRowDefsAsync(s).ConfigureAwait(true);
        await BulkInsertAsync(s, volumeRows).ConfigureAwait(true);

        var expectedValues = (volumeRows * 3L) + 2;   // 3 колонки на рядок + «a» і 42 з ArrangeAsync
        var plan = new VersionMigrationPlan(
            Sheets: new Dictionary<int, int> { [s.Doc.SheetDefId] = s.TargetSheetDefId },
            Tables: new Dictionary<int, int> { [s.Doc.TableDefId] = s.TargetTableDefId },
            Columns:
            [
                new VersionMigrationColumn(s.Doc.ColumnDefIds[0], s.TargetColumns["C1"], s.TargetTableDefId),
                new VersionMigrationColumn(s.Doc.ColumnDefIds[1], s.TargetColumns["C2"], s.TargetTableDefId),
                new VersionMigrationColumn(s.Doc.ColumnDefIds[2], s.TargetColumns["C3"], s.TargetTableDefId),
            ],
            Rows: new Dictionary<int, int> { [s.Doc.RowDefIds[0]] = targetRows[0], [s.Doc.RowDefIds[1]] = targetRows[1] },
            HeaderFields: new Dictionary<int, int>(),
            NewRows: [],
            Items: [],
            TransferredValues: expectedValues,
            LostValues: 0,
            GuardedValues: 0);

        var recorder = new TimingRecorder();
        var rangeStats = new List<MigrationRangeStat>();
        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.CommandTimeout(GlobalCommandTimeoutSeconds))
            .AddInterceptors(recorder)
            .Options))
        {
            // Діапазон 4000 рядків TableRow (≈ 12 тис. комірок) — щоб обсяг тесту дав кілька діапазонів-комітів.
            var store = new DocumentVersionMigrationStore(db) { RangeRows = 4_000, RangeObserver = rangeStats.Add };
            await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true);
            var scopeWatch = System.Diagnostics.Stopwatch.StartNew();
            var scope = await store.ReadScopeAsync(s.Doc.ProjectId, CancellationToken.None).ConfigureAwait(true);
            scopeWatch.Stop();
            Assert.Equal(expectedValues, scope.Cells.Sum(c => c.Values));   // лише непорожні: порожня C3 з ArrangeAsync не рахується
            Console.WriteLine(FormattableString.Invariant($"N3-VOLUME readScopeMs={scopeWatch.ElapsedMilliseconds}"));
            recorder.Clear();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await store.ApplyAsync(s.Doc.ProjectId, s.TargetVersionId, plan, CancellationToken.None).ConfigureAwait(true);
            watch.Stop();
            await tx.CommitAsync().ConfigureAwait(true);

            Console.WriteLine(FormattableString.Invariant(
                $"N3-VOLUME values={expectedValues} applyMs={watch.ElapsedMilliseconds} commands={recorder.Commands.Count}"));
            foreach (var line in recorder.Summary())
            {
                Console.WriteLine(line);
            }
        }

        // ⛔ Предмет тесту 1: кожна команда переносу має явний подовжений таймаут, а не глобальні 60 с.
        Assert.NotEmpty(recorder.Commands);
        Assert.All(recorder.Commands, c => Assert.Equal(DocumentVersionMigrationStore.ApplyCommandTimeoutSeconds, c.TimeoutSeconds));
        Assert.True(DocumentVersionMigrationStore.ApplyCommandTimeoutSeconds > GlobalCommandTimeoutSeconds);

        // ⛔ Предмет тесту 2: комірки йдуть діапазонами за ключем з окремим комітом на кожен. Стільки значень одним
        // оператором — це і був дефект. (Діапазони виконує власне з'єднання сховища, тож у перехоплювачі контексту їх немає.)
        var cellRanges = rangeStats.Count(r => r.Kind == "cells");
        var minRanges = (int)Math.Ceiling((volumeRows + 2) / 4_000d);
        Assert.True(cellRanges >= minRanges, $"Діапазонів комірок: {cellRanges}, а для {volumeRows} рядків мало бути ≥ {minRanges}.");
        Assert.All(rangeStats.Where(r => r.Kind == "cells"), r => Assert.True(r.Rows <= 4_000 * 4L, $"Діапазон завеликий: {r.Rows} рядків."));
        Assert.Equal(expectedValues + 1 + volumeRows, rangeStats.Where(r => r.Kind == "cells").Sum(r => r.Rows));   // комірки (+порожня C3) + рядки з описом

        // ⛔ Предмет тесту 3: пачки не порушили результат — усі значення на нових колонках, версія проєкту перемкнута.
        await using var check = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var moved = await check.CellValues.AsNoTracking()
            .CountAsync(c => c.PeriodKeyValue == s.Doc.PeriodKey.Value && c.TableDefId == s.TargetTableDefId
                             && (c.ColumnDefId == s.TargetColumns["C1"] || c.ColumnDefId == s.TargetColumns["C2"]
                                 || c.ColumnDefId == s.TargetColumns["C3"])).ConfigureAwait(true);
        Assert.Equal(expectedValues + 1, moved);   // + порожня комірка C3 з ArrangeAsync

        var leftBehind = await check.CellValues.AsNoTracking()
            .CountAsync(c => c.PeriodKeyValue == s.Doc.PeriodKey.Value && c.TableDefId == s.Doc.TableDefId).ConfigureAwait(true);
        Assert.Equal(0, leftBehind);

        var movedRows = await check.TableRows.AsNoTracking()
            .CountAsync(r => r.PeriodKeyValue == s.Doc.PeriodKey.Value && r.TableInstanceId == s.Doc.TableInstanceId
                             && r.RowDefId == targetRows[0]).ConfigureAwait(true);
        Assert.Equal(volumeRows, movedRows);
    }

    private async Task<string> SnapshotCountsAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var version = await db.Projects.AsNoTracking().Where(p => p.Id == s.Doc.ProjectId)
            .Select(p => p.TemplateVersionId).SingleAsync().ConfigureAwait(false);
        var oldCells = await db.CellValues.AsNoTracking()
            .CountAsync(c => c.PeriodKeyValue == s.Doc.PeriodKey.Value && c.TableDefId == s.Doc.TableDefId).ConfigureAwait(false);
        var newCells = await db.CellValues.AsNoTracking()
            .CountAsync(c => c.PeriodKeyValue == s.Doc.PeriodKey.Value && c.TableDefId == s.TargetTableDefId).ConfigureAwait(false);
        var oldRows = await db.TableRows.AsNoTracking()
            .CountAsync(r => r.PeriodKeyValue == s.Doc.PeriodKey.Value && r.RowDefId == s.Doc.RowDefIds[0]).ConfigureAwait(false);

        return $"v{version}|old{oldCells}|new{newCells}|rows{oldRows}";
    }

    private async Task<IReadOnlyList<int>> TargetRowDefsAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.RowDefs.AsNoTracking().Where(r => r.TableDefId == s.TargetTableDefId)
            .OrderBy(r => r.Ordinal).Select(r => r.Id).ToListAsync().ConfigureAwait(false);
    }

    /// <summary>Додає <paramref name="rows"/> рядків (по 3 значення) в екземпляр таблиці документа — одним набором SQL.</summary>
    private async Task BulkInsertAsync(Scenario s, int rows)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 600;
        command.CommandText = """
            ;WITH n AS (
                SELECT TOP (@rows) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i
                FROM sys.all_columns a CROSS JOIN sys.all_columns b)
            INSERT doc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, IsOrphaned, ModifiedAt)
            SELECT @pk, NEXT VALUE FOR doc.TableRowSeq, @ti, CONCAT(N'V', n.i, N'_', @tag), @rowDef, 100 + n.i, 0, 0, SYSUTCDATETIME()
            FROM n;

            INSERT doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric, IsCalculated, IsEmpty)
            SELECT r.PeriodKey, r.Id, c.ColumnDefId, @td,
                   CASE WHEN c.K = 1 THEN N'text' END, CASE WHEN c.K > 1 THEN 1.5 END, 0, 0
            FROM   doc.TableRow r
            CROSS JOIN (VALUES (1, @c1), (2, @c2), (3, @c3)) AS c(K, ColumnDefId)
            WHERE  r.PeriodKey = @pk AND r.TableInstanceId = @ti AND r.RowKey LIKE N'V%';
            """;
        command.Parameters.Add(new SqlParameter("@rows", rows));
        command.Parameters.Add(new SqlParameter("@pk", s.Doc.PeriodKey.Value));
        command.Parameters.Add(new SqlParameter("@ti", s.Doc.TableInstanceId));
        command.Parameters.Add(new SqlParameter("@tag", s.Tag));
        command.Parameters.Add(new SqlParameter("@rowDef", s.Doc.RowDefIds[0]));
        command.Parameters.Add(new SqlParameter("@td", s.Doc.TableDefId));
        command.Parameters.Add(new SqlParameter("@c1", s.Doc.ColumnDefIds[0]));
        command.Parameters.Add(new SqlParameter("@c2", s.Doc.ColumnDefIds[1]));
        command.Parameters.Add(new SqlParameter("@c3", s.Doc.ColumnDefIds[2]));
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Запам'ятовує текст, таймаут і тривалість кожної команди.</summary>
    private sealed class TimingRecorder : DbCommandInterceptor
    {
        private readonly List<Seen> _seen = [];
        private readonly object _gate = new();

        public IReadOnlyList<Seen> Commands
        {
            get { lock (_gate) { return [.. _seen]; } }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _seen.Clear();
            }
        }

        public IEnumerable<string> Summary()
            => Commands.Select(c => FormattableString.Invariant(
                $"  {c.Millis,7} мс [{c.TimeoutSeconds,4} с] rows={c.Rows,6} {Head(c.Text)}"));

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            Add(command, eventData, -1);
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Add(command, eventData, result);
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }

        private void Add(DbCommand command, CommandExecutedEventData eventData, int rows)
        {
            lock (_gate)
            {
                _seen.Add(new Seen(command.CommandText, command.CommandTimeout, (long)eventData.Duration.TotalMilliseconds, rows));
            }
        }

        private static string Head(string text)
        {
            var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal)
                                                           && !l.StartsWith("SET ", StringComparison.Ordinal)) ?? string.Empty;
            return line.Length > 90 ? line[..90] : line;
        }
    }

    private sealed record Seen(string Text, int TimeoutSeconds, long Millis, int Rows);
}
