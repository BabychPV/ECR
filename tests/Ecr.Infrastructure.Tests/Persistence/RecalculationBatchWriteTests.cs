// tests/Ecr.Infrastructure.Tests/Persistence/RecalculationBatchWriteTests.cs
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// O2 (ФВ-9.8, замір I2): обчислені комірки повного перерахунку пишуться
/// ОДНИМ пакетом на документо-період — на <b>реальному</b> SQL Server.
/// </summary>
/// <remarks>
/// ⛔ Що було. <c>RecalculationService</c> писав кожен екземпляр таблиці
/// окремо: <c>ReadCellsAsync</c> → <c>MERGE doc.CellValue</c> →
/// <c>INSERT aud.CellChange</c> на КОЖНУ таблицю. На документі з 90 таблицями
/// це 90 <c>MERGE</c> на період, 1 080 на документ-рік, ~11 мс ЦП SQL кожен —
/// 66 % ЦП SQL річного перерахунку (I2, <c>docs/build/perf/I2-…</c>).
///
/// ⚠ Документ тут лежить у ДВОХ сусідніх партиціях <c>pf_ByPeriodKey</c>
/// (202612 і 202701 — межа року, кожне значення — своя партиція,
/// <c>02-partitions.sql</c>): пакет, що губить рядок на межі партиції чи
/// періоду, дає неправильну кількість або значення з одного боку межі.
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationBatchWriteTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private const int Early = 202612;
    private const int Late = 202701;
    private const int RowCount = 3;

    /// <summary>Таблиць на аркуш: два аркуші, п'ять таблиць на період.</summary>
    private static readonly int[] TablesPerSheet = [3, 2];

    private static readonly AsyncLocal<StrongBox<bool>?> Measuring = new();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пакетний_запис_дає_ті_самі_комірки_й_аудит_що_поштучний_по_обидва_боки_межі_партиції()
    {
        var perInstance = await ArrangeAsync();
        var batched = await ArrangeAsync();

        foreach (var period in new[] { Early, Late })
        {
            await RecalculateAsync(perInstance, period, legacyWrites: true);
            await RecalculateAsync(batched, period, legacyWrites: false);
        }

        var expected = await DumpAsync(perInstance);
        var actual = await DumpAsync(batched);

        // Порівняння для замітки «до/після» між комітами: той самий знімок,
        // нормалізований від ідентифікаторів, лягає у файл.
        if (Environment.GetEnvironmentVariable("ECR_O2_DUMP") is { Length: > 0 } dumpPath)
        {
            await File.WriteAllTextAsync(dumpPath, actual, new UTF8Encoding(false));
        }

        Assert.Equal(expected, actual);

        // ⛔ Рівність двох шляхів не доводить, що жоден нічого не загубив.
        // Тому обидва боки межі перевіряються ще й за очікуваним станом.
        foreach (var period in new[] { Early, Late })
        {
            await AssertPeriodAsync(batched, period);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Документо_період_пишеться_одним_MERGE_і_одним_INSERT_аудиту()
    {
        var doc = await ArrangeAsync();

        var (written, detail, merges, audits) = await MeasureAsync(doc, Early);
        output.WriteLine(detail);

        Assert.Equal(doc.Tables.Count * 2, written);

        // До O2: MERGE і INSERT аудиту — по одному на КОЖЕН екземпляр (тут 5).
        Assert.True(merges == 1, $"MERGE doc.CellValue на документо-період: {merges}, очікувався 1.\n{detail}");
        Assert.True(audits == 1, $"INSERT aud.CellChange на документо-період: {audits}, очікувався 1.\n{detail}");

        await AssertPeriodAsync(doc, Early);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повторний_перерахунок_нічого_не_пише_й_не_змінює()
    {
        var doc = await ArrangeAsync();
        await RecalculateAsync(doc, Early, legacyWrites: false);
        var first = await DumpAsync(doc);

        var (written, detail, merges, audits) = await MeasureAsync(doc, Early);

        Assert.Equal(0, written);
        Assert.True(merges == 0 && audits == 0, detail);
        Assert.Equal(first, await DumpAsync(doc));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Поданий_аркуш_пакет_не_переписує_а_сусідній_пише()
    {
        var doc = await ArrangeAsync();
        var submittedSheet = doc.Tables[0].SheetDefId;

        await using (var db = Context())
        {
            var state = new ApprovalState(doc.DocumentId, submittedSheet, Early);
            state.Submit(1, new DateTime(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc));
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync();
        }

        var before = await CellsAsync(doc, Early);
        var (written, detail, merges, _) = await MeasureAsync(doc, Early);
        var after = await CellsAsync(doc, Early);

        var open = doc.Tables.Where(t => t.SheetDefId != submittedSheet).ToList();
        Assert.Equal(open.Count * 2, written);
        Assert.True(merges == 1, detail);

        foreach (var table in doc.Tables)
        {
            var total = table.ColumnDefIds[3];
            for (var r = 0; r < RowCount; r++)
            {
                var key = (table.Rows[Early][r], total);
                if (table.SheetDefId == submittedSheet)
                {
                    // ⛔ Число поданого аркуша — те саме, що до прогону: і
                    // застаріле, і відсутнє лишаються як були.
                    Assert.Equal(before.GetValueOrDefault(key), after.GetValueOrDefault(key));
                }
                else
                {
                    Assert.Equal(Expected(Early, table.Index, r), after[key]);
                }
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Попередні_значення_документа_читаються_одним_запитом_і_понад_межу_параметрів()
    {
        // Пакет документо-періоду читає «старі» значення для аудиту одним
        // зверненням; у документі за період рядків буває тисячі, а SQL Server
        // приймає не більше 2100 параметрів на запит.
        await using var db = Context();
        var store = new NormalizedCellStore(db);
        var addresses = Enumerable.Range(0, 5000)
            .Select(i => new CellAddress(new PeriodKey(Early), 9_000_000_000L + i, 1 + (i % 7)))
            .ToList();

        var found = await store.ReadCellsAsync(addresses, CancellationToken.None);

        Assert.Empty(found);
    }

    // ── перевірки ───────────────────────────────────────────────────────────

    private static decimal Base(int period, int table, int row) => (period % 100) * 1000 + table * 10 + row;

    private static decimal Expected(int period, int table, int row) => (2 * Base(period, table, row)) + 1.5m;

    /// <summary>Обидва боки межі: кількість і значення обчислених і ручних комірок.</summary>
    private async Task AssertPeriodAsync(Fixture doc, int period)
    {
        var cells = await CellsAsync(doc, period);
        var calculated = 0;

        foreach (var table in doc.Tables)
        {
            for (var r = 0; r < RowCount; r++)
            {
                var row = table.Rows[period][r];
                Assert.Equal(Base(period, table.Index, r), cells[(row, table.ColumnDefIds[0])]);
                Assert.Equal(Base(period, table.Index, r) + 0.5m, cells[(row, table.ColumnDefIds[1])]);
                Assert.Equal(1m, cells[(row, table.ColumnDefIds[2])]);
                Assert.Equal(Expected(period, table.Index, r), cells[(row, table.ColumnDefIds[3])]);
                calculated++;
            }
        }

        Assert.Equal(doc.Tables.Count * RowCount * 4, cells.Count);
        Assert.Equal(doc.Tables.Count * RowCount, calculated);
    }

    private async Task<Dictionary<(long Row, int Column), decimal?>> CellsAsync(Fixture doc, int period)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cv.TableRowId, cv.ColumnDefId, cv.ValueNumeric
            FROM doc.CellValue cv
            JOIN doc.TableRow tr ON tr.PeriodKey = cv.PeriodKey AND tr.Id = cv.TableRowId
            JOIN doc.TableInstance ti ON ti.PeriodKey = tr.PeriodKey AND ti.Id = tr.TableInstanceId
            WHERE ti.DocumentId = @d AND cv.PeriodKey = @p;
            """;
        command.Parameters.AddWithValue("@d", doc.DocumentId);
        command.Parameters.AddWithValue("@p", period);

        var result = new Dictionary<(long, int), decimal?>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[(reader.GetInt64(0), reader.GetInt32(1))] = reader.IsDBNull(2) ? null : reader.GetDecimal(2);
        }

        return result;
    }

    /// <summary>
    /// Стан документа без ідентифікаторів: комірки обох періодів, «дотик» рядків
    /// і журнал аудиту в порядку вставки.
    /// </summary>
    private async Task<string> DumpAsync(Fixture doc)
    {
        var tables = doc.Tables.ToDictionary(t => t.TableDefId, t => $"T{t.Index}");
        var columns = doc.Tables
            .SelectMany(t => t.ColumnDefIds.Select((id, i) => (id, name: $"T{t.Index}C{i + 1}")))
            .ToDictionary(p => p.id, p => p.name);
        var rows = doc.Tables
            .SelectMany(t => t.Rows.SelectMany(pair => pair.Value.Select((id, i) => (id, name: $"T{t.Index}P{pair.Key}R{i + 1}"))))
            .ToDictionary(p => p.id, p => p.name);
        var rowKeys = doc.Tables
            .SelectMany(t => t.RowKeys.Select((key, i) => (key, name: $"T{t.Index}R{i + 1}")))
            .ToDictionary(p => p.key, p => p.name, StringComparer.Ordinal);

        var text = new StringBuilder();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT cv.PeriodKey, cv.TableRowId, cv.ColumnDefId, cv.TableDefId, cv.ValueString,
                       cv.ValueNumeric, cv.ValueDate, cv.ValueBool, cv.ValueRegistryEntryId, cv.ValueUnitId,
                       cv.IsCalculated, cv.IsEmpty, tr.ModifiedAt
                FROM doc.CellValue cv
                JOIN doc.TableRow tr ON tr.PeriodKey = cv.PeriodKey AND tr.Id = cv.TableRowId
                JOIN doc.TableInstance ti ON ti.PeriodKey = tr.PeriodKey AND ti.Id = tr.TableInstanceId
                WHERE ti.DocumentId = @d;
                """;
            command.Parameters.AddWithValue("@d", doc.DocumentId);

            var lines = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                lines.Add(string.Join('|',
                    reader.GetInt32(0).ToString(CultureInfo.InvariantCulture),
                    rows[reader.GetInt64(1)],
                    columns[reader.GetInt32(2)],
                    tables[reader.GetInt32(3)],
                    Field(reader, 4), Field(reader, 5), Field(reader, 6), Field(reader, 7),
                    Field(reader, 8), Field(reader, 9), Field(reader, 10), Field(reader, 11), Field(reader, 12)));
            }

            lines.Sort(StringComparer.Ordinal);
            text.AppendLine("[doc.CellValue]");
            lines.ForEach(line => text.AppendLine(line));
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT ChangedAt, PeriodKey, TableRowId, RowKey, ColumnDefId, OldValue, NewValue,
                       ChangedByUserId, Origin, IsLateEdit, CorrelationId
                FROM aud.CellChange WHERE DocumentId = @d ORDER BY Id;
                """;
            command.Parameters.AddWithValue("@d", doc.DocumentId);

            text.AppendLine("[aud.CellChange]");
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                text.AppendLine(string.Join('|',
                    Field(reader, 0), Field(reader, 1), rows[reader.GetInt64(2)],
                    rowKeys[reader.GetString(3)], columns[reader.GetInt32(4)],
                    Field(reader, 5), Field(reader, 6), Field(reader, 7), Field(reader, 8),
                    Field(reader, 9), Field(reader, 10)));
            }
        }

        return text.ToString();

        static string Field(SqlDataReader reader, int ordinal)
            => reader.IsDBNull(ordinal)
                ? "∅"
                : Convert.ToString(reader.GetValue(ordinal) is DateTime at ? at.ToString("O", CultureInfo.InvariantCulture) : reader.GetValue(ordinal), CultureInfo.InvariantCulture)!;
    }

    // ── прогін ──────────────────────────────────────────────────────────────

    private async Task<int> RecalculateAsync(Fixture doc, int period, bool legacyWrites)
    {
        await using var db = Context();
        return await Build(db, doc, legacyWrites)
            .RecalculateAllAsync(doc.DocumentId, new PeriodKey(period), CancellationToken.None);
    }

    private async Task<(int Written, string Detail, int Merges, int Audits)> MeasureAsync(Fixture doc, int period)
    {
        await using var db = Context();
        var service = Build(db, doc, legacyWrites: false);

        using var counter = new SqlClientCommandCounter(
            new CommandTally(), _ => Measuring.Value is { Value: true });
        counter.Tally.Reset();
        Measuring.Value = new StrongBox<bool>(true);
        int written;
        try
        {
            written = await service.RecalculateAllAsync(doc.DocumentId, new PeriodKey(period), CancellationToken.None);
        }
        finally
        {
            Measuring.Value = null;
        }

        var seen = counter.Tally.Snapshot();
        return (written, seen.Format(), seen["MERGE doc.CellValue"], seen["INSERT aud.CellChange"]);
    }

    private static RecalculationService Build(EcrDbContext db, Fixture doc, bool legacyWrites)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new FixedClock(new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc));

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(doc.DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(call =>
               {
                   var key = new PeriodKey(call.ArgAt<int>(1));
                   var start = new DateOnly(key.Year, key.Sequence, 1);
                   return (PeriodBounds?)new PeriodBounds(start, start.AddMonths(1).AddDays(-1));
               });
        periods.FindPeriodStateAsync(doc.DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns((PeriodState?)PeriodState.Open);

        var versions = Substitute.For<ITemplateVersionStore>();
        versions.ListFormulaDependenciesAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns([]);

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());

        ICellStore cells = new NormalizedCellStore(db);
        if (legacyWrites)
        {
            cells = new PerInstanceCellStore(cells);
        }

        return new RecalculationService(
            cells, new RowStore(db, bulk, clock), periods,
            new MetadataCache(new MemoryCache(new MemoryCacheOptions()), db), versions,
            new RealFormulaEngine(), units, Substitute.For<IRegistryStore>(), headers,
            new AuditWriter(db), clock, new UnitOfWork(db), new SheetEditGate(db));
    }

    // ── дані ────────────────────────────────────────────────────────────────

    private sealed record FixtureTable(
        int Index,
        int SheetDefId,
        int TableDefId,
        IReadOnlyList<int> ColumnDefIds,
        IReadOnlyList<string> RowKeys,
        IReadOnlyDictionary<int, IReadOnlyList<long>> Rows);

    private sealed record Fixture(long DocumentId, int TemplateVersionId, IReadOnlyList<FixtureTable> Tables);

    /// <summary>
    /// Документ на два аркуші й п'ять таблиць у періодах 202612 і 202701;
    /// кожна таблиця — три ручні колонки й формула <c>[C1] + [C2] + [C3]</c> у
    /// четвертій. Рядок 1 обчисленої комірки не має (вставка), рядок 2 має
    /// застаріле число (оновлення), рядок 3 — уже правильне (не пишеться).
    /// </summary>
    private async Task<Fixture> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        await using var db = Context();

        var template = new Template(EcrCode.Create($"TPLO2{tag}"), Name("O2"), 1, now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();
        var version = new TemplateVersion(template.Id, $"1.0.0.{tag}", 1, now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var shapes = new List<(int Index, SheetDef Sheet, TableDef Table, List<ColumnDef> Columns, List<string> RowKeys)>();
        var index = 0;
        for (var s = 0; s < TablesPerSheet.Length; s++)
        {
            var sheet = new SheetDef(version.Id, EcrCode.Create($"SH{s}_{tag}"), Name($"Sheet {s}"), s + 1);
            db.SheetDefs.Add(sheet);
            await db.SaveChangesAsync();

            for (var t = 0; t < TablesPerSheet[s]; t++, index++)
            {
                var table = new TableDef(sheet.Id, EcrCode.Create($"TB{index}_{tag}"), Name($"Table {index}"), t + 1,
                    TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
                db.TableDefs.Add(table);
                await db.SaveChangesAsync();

                var columns = new List<ColumnDef>();
                for (var c = 1; c <= 4; c++)
                {
                    var column = new ColumnDef(table.Id, EcrCode.Create($"C{c}_{index}_{tag}"), Name($"Col {c}"), c, CellDataType.Decimal);
                    columns.Add(column);
                    db.ColumnDefs.Add(column);
                }

                var rowKeys = new List<string>();
                for (var r = 1; r <= RowCount; r++)
                {
                    rowKeys.Add($"R{r}_{index}_{tag}");
                    db.RowDefs.Add(new RowDef(table.Id, RowKey.Create(rowKeys[^1]), r, Name($"Row {r}"), RowKind.Item));
                }

                await db.SaveChangesAsync();

                var formula = new FormulaDef(
                    table.Id, FormulaScope.Column,
                    $"[{columns[0].Code}] + [{columns[1].Code}] + [{columns[2].Code}]", ExpressionDialect.Template);
                formula.AssignColumn(columns[3].Id);
                formula.SetEvaluationOrder(index + 1);
                db.FormulaDefs.Add(formula);
                await db.SaveChangesAsync();

                shapes.Add((index, sheet, table, columns, rowKeys));
            }
        }

        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync();
        var project = new Project(
            EcrCode.Create($"PRJO2{tag}"), Name("O2"), new DateOnly(2026, 1, 1), new DateOnly(2027, 12, 31),
            version.Id, PeriodKind.Monthly, policyId, "Asia/Almaty");
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        foreach (var period in new[] { Early, Late })
        {
            var key = new PeriodKey(period);
            db.Periods.Add(new Period(project.Id, key, (byte)key.Sequence,
                new DateOnly(key.Year, key.Sequence, 1),
                new DateOnly(key.Year, key.Sequence, DateTime.DaysInMonth(key.Year, key.Sequence))));
        }

        var document = new Document(project.Id, $"DOCO2{tag}", 1, now);
        db.Documents.Add(document);
        await db.SaveChangesAsync();

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var rowsByTable = shapes.ToDictionary(s => s.Index, _ => new Dictionary<int, IReadOnlyList<long>>());
        var inserts = new StringBuilder();

        foreach (var period in new[] { Early, Late })
        {
            var key = new PeriodKey(period);
            foreach (var shape in shapes)
            {
                var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
                var firstRowId = await loader.ReserveIdsAsync("doc.TableRowSeq", RowCount, CancellationToken.None);
                db.TableInstances.Add(new TableInstance(key, instanceId, document.Id, shape.Table.Id, now));

                var rowIds = new List<long>();
                for (var r = 0; r < RowCount; r++)
                {
                    var rowId = firstRowId + r;
                    rowIds.Add(rowId);
                    db.TableRows.Add(new TableRow(key, rowId, instanceId, RowKey.Create(shape.RowKeys[r]), r + 1, now));

                    var value = Base(period, shape.Index, r);
                    Cell(inserts, period, rowId, shape.Columns[0].Id, shape.Table.Id, value, calculated: false);
                    Cell(inserts, period, rowId, shape.Columns[1].Id, shape.Table.Id, value + 0.5m, calculated: false);
                    Cell(inserts, period, rowId, shape.Columns[2].Id, shape.Table.Id, 1m, calculated: false);
                    if (r == 1)
                    {
                        Cell(inserts, period, rowId, shape.Columns[3].Id, shape.Table.Id, -1m, calculated: true);
                    }
                    else if (r == 2)
                    {
                        Cell(inserts, period, rowId, shape.Columns[3].Id, shape.Table.Id, Expected(period, shape.Index, r), calculated: true);
                    }
                }

                rowsByTable[shape.Index][period] = rowIds;
            }
        }

        await db.SaveChangesAsync();

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = inserts.ToString();
            await command.ExecuteNonQueryAsync();
        }

        return new Fixture(
            document.Id,
            version.Id,
            [.. shapes.Select(s => new FixtureTable(
                s.Index, s.Sheet.Id, s.Table.Id, [.. s.Columns.Select(c => c.Id)], s.RowKeys,
                rowsByTable[s.Index]))]);

        static void Cell(StringBuilder sb, int period, long rowId, int columnId, int tableId, decimal value, bool calculated)
            => sb.AppendLine(CultureInfo.InvariantCulture,
                $"INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES ({period}, {rowId}, {columnId}, {tableId}, {value}, {(calculated ? 1 : 0)}, 0);");
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    /// <summary>
    /// Поштучний шлях запису — еталон «до O2»: пакет розкладається на
    /// <see cref="ICellStore.ApplyAsync"/> на кожен екземпляр, як писав
    /// <c>RecalculationService</c> доти.
    /// </summary>
    private sealed class PerInstanceCellStore(ICellStore inner) : ICellStore
    {
        public Task<IReadOnlyList<CellRecord>> ReadSliceAsync(long tableInstanceId, CancellationToken ct)
            => inner.ReadSliceAsync(tableInstanceId, ct);

        public Task<IReadOnlyDictionary<long, IReadOnlyList<CellRecord>>> ReadSlicesAsync(
            IReadOnlyList<long> tableInstanceIds, CancellationToken ct)
            => inner.ReadSlicesAsync(tableInstanceIds, ct);

        public Task<IReadOnlyDictionary<CellAddress, CellValueData>> ReadCellsAsync(
            IReadOnlyCollection<CellAddress> addresses, CancellationToken ct)
            => inner.ReadCellsAsync(addresses, ct);

        public Task<IReadOnlyDictionary<long, string>> ApplyAsync(CellChangeSet changes, CancellationToken ct)
            => inner.ApplyAsync(changes, ct);

        public async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<long, string>>> ApplyBatchAsync(
            IReadOnlyCollection<CellChangeSet> changes, CancellationToken ct)
        {
            var result = new Dictionary<long, IReadOnlyDictionary<long, string>>();
            foreach (var set in changes)
            {
                result[set.TableInstanceId] = await inner.ApplyAsync(set, ct).ConfigureAwait(false);
            }

            return result;
        }
    }
}
