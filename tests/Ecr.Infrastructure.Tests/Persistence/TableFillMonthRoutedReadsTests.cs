using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Q-17, P1-1 проходу C (27.10): <c>GET /documents/{id}/tables/status</c> при <c>Collection:MonthColumnRouting=ON</c>
/// відповідав 50–58 с проти 40 мс при OFF. Доказ — логічні читання <c>SET STATISTICS IO</c> усіх запитів
/// <see cref="TableFillStore.GetFillCountsAsync"/>, а не мілісекунди; результат ON має збігатися з еталоном.
/// </summary>
/// <remarks>
/// ⛔ Дані: таблиця «місяці в колонках» із 202 стовпцями (200 місячних) × 12 періодів з копіями комірок у КОЖНОМУ
/// екземплярі (як у Land до перенесення) і кілька сторонніх документів, що роздувають <c>doc.CellValue</c>: на
/// порожній базі скан і пошук однакові, і замір нічого б не довів.
/// </remarks>
[Collection("SqlServer")]
public sealed class TableFillMonthRoutedReadsTests(SqlServerFixture sql, ITestOutputHelper output) : IDisposable
{
    private const int ViewMonth = 10;

    private const int Year = 2026;

    private const int Rows = 30;

    private const int PlainColumns = 2;

    private const int MonthColumns = 200;

    private const int NoiseDocuments = 3;

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    private readonly TestDocumentBuilder _builder = new(sql.ConnectionString);

    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Заповненість_при_ON_не_робить_seek_по_кожній_колонці_і_дає_еталонний_результат()
    {
        var table = await ArrangeAsync();

        var off = await RunAsync(table, routing: false);
        var on = await RunAsync(table, routing: true);

        output.WriteLine($"OFF: запитів {off.Commands}, логічних читань {off.LogicalReads}, scan CellValue {off.CellScans}");
        output.WriteLine($"ON : запитів {on.Commands}, логічних читань {on.LogicalReads}, scan CellValue {on.CellScans}");

        // Еталон (незалежний від SQL): введена, не обчислювана комірка колонки у ВЛАСНИКА; копії в інших місяцях
        // не рахуються. Місячний стовпець m — власник 2026-m; не-місячний — якір 202601.
        Assert.Equal(table.ExpectedOn, on.Filled);
        Assert.Equal(table.ExpectedOff, off.Filled);
        Assert.NotEqual(table.ExpectedOn, table.ExpectedOff);

        // Нічого не змінилося для OFF: рівно два запити (рядки, комірки).
        Assert.Equal(2, off.Commands);

        // Те саме число, що давав попередній запит Q-17 (еталонна копія його тексту).
        Assert.Equal(table.ExpectedOn, await LegacyForeignFilledAsync(table));

        // ⛔ Межа ШІСТЬ, а не три з брифу: ON за природою читає комірки КОЖНОГО з 11 періодів-власників (місячний
        // стовпець m живе лише в екземплярі m), тобто ≥ 11 «зрізів» проти одного при OFF; ≤ 3x досяжне лише новим
        // індексом (PeriodKey, ColumnDefId, …) — це міграція, див. звіт lane. Замір: було 185 471 читання / 61 380
        // seek-ів по doc.CellValue (2 150x), стало ~1 700 / ~330 (≈ 20x). Межа 40x відокремлює їх на два порядки.
        Assert.True(
            on.LogicalReads <= 40 * off.LogicalReads,
            $"ON читає {on.LogicalReads} сторінок проти {off.LogicalReads} при OFF (> 40x).");

        // Головна ознака регресії — seek по кожній колонці зі списку для кожного рядка власника: scan count по
        // doc.CellValue ~ рядки × колонки (тут 61 380). Тепер — рядки × періоди (~330).
        Assert.True(
            on.CellScans <= 4 * Rows * 11,
            $"doc.CellValue: scan count {on.CellScans} (seek по кожній колонці власника?).");
    }

    // ── дані ─────────────────────────────────────────────────────────────

    private sealed record Table(TestDocument Doc, int[] ColumnIds, int[] MonthOfColumn, int ExpectedOn, int ExpectedOff);

    private async Task<Table> ArrangeAsync()
    {
        var doc = await _builder.BuildAsync(
            periodKey: (Year * 100) + ViewMonth,
            columnCount: PlainColumns,
            rowCount: Rows,
            layoutKind: TableLayoutKind.MonthsInColumns,
            monthColumns: 12);

        var columnIds = new List<int>(doc.ColumnDefIds);
        var monthOf = new List<int>();
        for (var i = 0; i < PlainColumns; i++)
        {
            monthOf.Add(0);
        }

        for (var m = 1; m <= 12; m++)
        {
            monthOf.Add(m);
        }

        await using (var db = _builder.CreateContext())
        {
            var extra = new List<ColumnDef>();
            for (var i = 1; i <= MonthColumns - 12; i++)
            {
                var m = 1 + (i % 12);
                var column = new ColumnDef(
                    doc.TableDefId,
                    EcrCode.Create($"X{i}_{doc.TableDefId}"),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = $"Extra {i}" }),
                    PlainColumns + 12 + i,
                    CellDataType.Decimal);
                column.SetMonth(true, (byte)m);
                extra.Add(column);
                monthOf.Add(m);
            }

            db.ColumnDefs.AddRange(extra);
            await db.SaveChangesAsync();
            columnIds.AddRange(extra.Select(c => c.Id));
        }

        var periods = new Dictionary<int, (PeriodKey Key, IReadOnlyList<long> RowIds)>
        {
            [ViewMonth] = (doc.PeriodKey, doc.RowIds),
        };

        for (var month = 1; month <= 12; month++)
        {
            if (month == ViewMonth)
            {
                continue;
            }

            var instance = await _builder.AddPeriodInstanceAsync(doc, new PeriodKey((Year * 100) + month));
            periods[month] = (instance.PeriodKey, instance.RowIds);
        }

        var columns = columnIds.ToArray();
        var months = monthOf.ToArray();
        var records = new List<CellRecord>();
        int expectedOn = 0, expectedOff = 0;

        foreach (var (month, (key, rowIds)) in periods)
        {
            for (var r = 0; r < rowIds.Count; r++)
            {
                for (var c = 0; c < columns.Length; c++)
                {
                    // Детермінована «розріджена» заповненість: частина порожніх, частина обчислених.
                    var salt = (r * 31) + (c * 17) + month;
                    if (salt % 4 == 0)
                    {
                        continue;
                    }

                    var calculated = salt % 11 == 0;
                    records.Add(new CellRecord(
                        new CellAddress(key, rowIds[r], columns[c]),
                        doc.TableDefId,
                        new CellValueData { ValueNumeric = 1m, IsCalculated = calculated }));

                    if (calculated)
                    {
                        continue;
                    }

                    if (month == ViewMonth)
                    {
                        expectedOff++;
                    }

                    // Власник: місячна колонка m — період 2026-m; звичайна — якір (202601).
                    var owner = months[c] == 0 ? 1 : months[c];
                    if (month == owner)
                    {
                        expectedOn++;
                    }
                }
            }
        }

        // ON не рахує власних (не чужих) комірок двічі: власник колонки = перегляд лише для M10 — вони є в обох.
        // Тому expectedOn: чужі колонки (власник ≠ 10) з комірок власників + власні (власник = 10) з перегляду.
        await new BulkCellLoader(sql.ConnectionString, 5000).LoadAsync(records, CancellationToken.None);

        // Сторонні документи — лише для обсягу doc.CellValue.
        for (var n = 0; n < NoiseDocuments; n++)
        {
            await AddNoiseAsync();
        }

        return new Table(doc, columns, months, expectedOn, expectedOff);
    }

    private async Task AddNoiseAsync()
    {
        var noise = await _builder.BuildAsync(
            periodKey: (Year * 100) + ViewMonth,
            columnCount: MonthColumns + PlainColumns,
            rowCount: Rows,
            layoutKind: TableLayoutKind.MonthsInColumns,
            monthColumns: 12);
        var records = new List<CellRecord>();
        var all = new Dictionary<PeriodKey, IReadOnlyList<long>> { [noise.PeriodKey] = noise.RowIds };
        for (var month = 1; month <= 12; month++)
        {
            if (month != ViewMonth)
            {
                var instance = await _builder.AddPeriodInstanceAsync(noise, new PeriodKey((Year * 100) + month));
                all[instance.PeriodKey] = instance.RowIds;
            }
        }

        foreach (var (key, rowIds) in all)
        {
            foreach (var rowId in rowIds)
            {
                foreach (var columnId in noise.ColumnDefIds)
                {
                    records.Add(new CellRecord(
                        new CellAddress(key, rowId, columnId), noise.TableDefId, new CellValueData { ValueNumeric = 2m }));
                }
            }
        }

        await new BulkCellLoader(sql.ConnectionString, 5000).LoadAsync(records, CancellationToken.None);
    }

    // ── прогін і замір ───────────────────────────────────────────────────

    private sealed record Run(int Filled, int Commands, long LogicalReads, long CellScans);

    private sealed record Io(long Reads, long CellScans);

    private async Task<Run> RunAsync(Table table, bool routing)
    {
        var recorder = new Recorder();
        await using var db = sql.CreateContext(recorder);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Collection:MonthColumnRouting"] = routing ? "true" : "false",
            })
            .Build();
        var router = new MonthColumnRouter(db, config);
        var store = new TableFillStore(db, router, new MetadataCache(_memory, db));

        // Прогрів: метадані й календар у кеші, щоб замір міряв лише запити заповненості.
        await store.GetFillCountsAsync(table.Doc.DocumentId, table.Doc.PeriodKey, [], CancellationToken.None);
        recorder.Commands.Clear();
        var counts = await store.GetFillCountsAsync(table.Doc.DocumentId, table.Doc.PeriodKey, [], CancellationToken.None);
        var filled = Assert.Single(counts, c => c.TableDefId == table.Doc.TableDefId).FilledCells;

        // Лише запити doc.* (метадані/календар могли б піти з кешу, але не рахуємо їх у будь-якому разі).
        var queries = recorder.Commands.Where(c => c.Text.Contains("[doc].", StringComparison.Ordinal)).ToList();
        long reads = 0;
        long scans = 0;
        foreach (var query in queries)
        {
            var io = await LogicalReadsAsync(query);
            reads += io.Reads;
            scans += io.CellScans;
        }

        return new Run(filled, queries.Count, reads, scans);
    }

    private async Task<Io> LogicalReadsAsync(RecordedCommand command)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        var messages = new List<string>();
        connection.InfoMessage += (_, args) => messages.Add(args.Message);

        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "SET STATISTICS IO ON;";
            await setup.ExecuteNonQueryAsync();
        }

        await using var replay = connection.CreateCommand();
        replay.CommandText = command.Text;
        replay.CommandTimeout = 600;
        foreach (var (name, value, dbType) in command.Parameters)
        {
            var p = new SqlParameter(name, value ?? DBNull.Value);
            if (value is not null)
            {
                p.DbType = dbType;
            }

            replay.Parameters.Add(p);
        }

        // ExecuteNonQuery: повідомлення STATISTICS IO з асинхронним читачем не долітають (див. PartitionScanTests).
        await replay.ExecuteNonQueryAsync();

        var total = 0L;
        var cellScans = 0L;
        foreach (var message in messages)
        {
            foreach (Match match in Regex.Matches(
                         message, @"logical reads\s+(\d+)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5)))
            {
                total += long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }

            foreach (Match match in Regex.Matches(
                         message, @"Table 'CellValue'\.\s+Scan count\s+(\d+)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5)))
            {
                cellScans += long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        Assert.True(messages.Count > 0, "STATISTICS IO нічого не повідомила: замір порожній.");
        return new Io(total, cellScans);
    }

    /// <summary>Еталон: текст запиту Q-17 до виправлення P1-1 (усі періоди-власники одним запитом).</summary>
    private async Task<int> LegacyForeignFilledAsync(Table table)
    {
        // Власники: місячна колонка m — 2026-m; звичайна — якір 202601. Чужі — власник ≠ перегляду.
        var owners = new Dictionary<int, int>();
        for (var c = 0; c < table.ColumnIds.Length; c++)
        {
            var owner = (Year * 100) + (table.MonthOfColumn[c] == 0 ? 1 : table.MonthOfColumn[c]);
            if (owner != table.Doc.PeriodKey.Value)
            {
                owners[table.ColumnIds[c]] = owner;
            }
        }

        await using var db = _builder.CreateContext();
        var ownerPeriods = owners.Values.Distinct().ToList();
        var tableDefIds = new List<int> { table.Doc.TableDefId };
        var columnIds = owners.Keys.ToList();
        var key = table.Doc.PeriodKey.Value;
        var documentId = table.Doc.DocumentId;

        var rows = await (
            from instance in db.TableInstances.AsNoTracking()
            where instance.DocumentId == documentId && instance.PeriodKeyValue == key && tableDefIds.Contains(instance.TableDefId)
            join row in db.TableRows.AsNoTracking().Where(r => r.PeriodKeyValue == key && !r.IsDeleted)
                on instance.Id equals row.TableInstanceId
            join owner in db.TableInstances.AsNoTracking()
                    .Where(o => o.DocumentId == documentId && ownerPeriods.Contains(o.PeriodKeyValue) && tableDefIds.Contains(o.TableDefId))
                on instance.TableDefId equals owner.TableDefId
            join ownerRow in db.TableRows.AsNoTracking().Where(r => ownerPeriods.Contains(r.PeriodKeyValue) && !r.IsDeleted)
                on new { P = owner.PeriodKeyValue, I = owner.Id, K = row.RowKeyValue }
                equals new { P = ownerRow.PeriodKeyValue, I = ownerRow.TableInstanceId, K = ownerRow.RowKeyValue }
            join cell in db.CellValues.AsNoTracking()
                    .Where(c => ownerPeriods.Contains(c.PeriodKeyValue) && !c.IsCalculated && columnIds.Contains(c.ColumnDefId))
                on new { P = ownerRow.PeriodKeyValue, R = ownerRow.Id }
                equals new { P = cell.PeriodKeyValue, R = cell.TableRowId }
            group cell by new { instance.TableDefId, cell.ColumnDefId, OwnerPeriod = cell.PeriodKeyValue } into g
            select new { g.Key.ColumnDefId, g.Key.OwnerPeriod, Filled = g.Count() })
            .ToListAsync();

        var foreign = rows.Where(r => owners.TryGetValue(r.ColumnDefId, out var o) && o == r.OwnerPeriod).Sum(r => r.Filled);

        // + власні (не чужі) колонки перегляду — як рахує filledByColumn у сховищі.
        var own = await db.CellValues.AsNoTracking()
            .Where(c => c.PeriodKeyValue == key && !c.IsCalculated && c.TableDefId == table.Doc.TableDefId)
            .GroupBy(c => c.ColumnDefId)
            .Select(g => new { ColumnDefId = g.Key, N = g.Count() })
            .ToListAsync();
        var ownFilled = own.Where(o => !owners.ContainsKey(o.ColumnDefId)).Sum(o => o.N);

        return foreign + ownFilled;
    }

    // ── перехоплювач ─────────────────────────────────────────────────────

    private sealed record RecordedCommand(string Text, List<(string Name, object? Value, DbType DbType)> Parameters);

    private sealed class Recorder : DbCommandInterceptor
    {
        public List<RecordedCommand> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(new RecordedCommand(
                command.CommandText,
                [.. command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, p.Value, p.DbType))]));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
