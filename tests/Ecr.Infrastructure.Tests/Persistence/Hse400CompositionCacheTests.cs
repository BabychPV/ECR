// tests/Ecr.Infrastructure.Tests/Persistence/Hse400CompositionCacheTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// PERF-11 (аудит 21.11): склад HSE400 з документів кешується між прогонами, але ніколи не віддає застарілого значення:
/// правка комірки джерела (штатний шлях запису), новий актуальний прогін, зміна набору рядків дають інший відбиток —
/// склад читається наново; без змін — кеш, жодного читання <c>doc.CellValue</c>.
/// </summary>
/// <remarks>
/// МУТАЦІЇ (локально): (1) прибрати штамп RowVersion з відбитка (<c>rowStamp ?? []</c> → <c>[]</c> у завантажувачі) — червоний
/// <c>Правка_комірки_джерела_видна_наступному_читанню</c> (кеш віддає старе 0.81); (2) прибрати актуальні прогони з
/// відбитка — червоний <c>Новий_актуальний_прогін_перечитує_розрахункові_рядки</c>; (3) віддавати кеш без перевірки
/// відбитка — обидва червоні.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PERF-11")]
public sealed class Hse400CompositionCacheTests(SqlServerFixture sql)
{
    private const int Year = 2097;
    private const int Month = 10;
    private const int PeriodKeyValue = (Year * 100) + Month;
    private const string Stream = "FG_Internal";
    private const string RowKeyValue = "Vddg";
    private const string MonthCode = "M10";
    private const string SecondRowKey = "Ethane";

    [Fact]
    public async Task Без_змін_друге_читання_береться_з_кешу_без_читання_комірок()
    {
        var s = await ArrangeAsync();
        await WriteAsync(s, 0.81m);
        var cache = new Hse400CompositionCache();

        var first = await ReadAsync(s, cache, counter: null);
        Assert.Contains(first.Rows, r => r.Value == 0.81m && r.Month == Month);
        Assert.Equal(1, cache.Misses);

        var counter = new CellQueryCounter();
        var second = await ReadAsync(s, cache, counter);

        Assert.Equal(1, cache.Hits);
        Assert.Equal(0, counter.CellValueReads);
        Assert.Equal(first.Rows, second.Rows);
        Assert.Equal(first.Years, second.Years);
    }

    [Fact]
    public async Task Правка_комірки_джерела_видна_наступному_читанню()
    {
        var s = await ArrangeAsync();
        await WriteAsync(s, 0.81m);
        var cache = new Hse400CompositionCache();

        var before = await ReadAsync(s, cache, counter: null);
        Assert.Contains(before.Rows, r => r.Value == 0.81m);

        // Штатний шлях запису (як PATCH /cells): значення змінилось, версія рядка піднялась.
        await WriteAsync(s, 0.95m);

        var counter = new CellQueryCounter();
        var after = await ReadAsync(s, cache, counter);

        Assert.Equal(2, cache.Misses);
        Assert.Equal(0, cache.Hits);
        Assert.True(counter.CellValueReads > 0, "Після правки комірки склад мусить читатися наново.");
        Assert.DoesNotContain(after.Rows, r => r.Value == 0.81m);
        Assert.Contains(after.Rows, r => r.Value == 0.95m && r.Month == Month);
    }

    [Fact]
    public async Task Кеш_і_вузьке_читання_дають_той_самий_склад_що_й_без_кешу()
    {
        var s = await ArrangeAsync();
        await WriteAsync(s, 0.77m);

        var uncached = await ReadAsync(s, cache: null, counter: null);
        var cached = await ReadAsync(s, new Hse400CompositionCache(), counter: null);

        Assert.NotEmpty(uncached.Rows);
        Assert.Equal(uncached.Rows, cached.Rows);
        Assert.Equal(uncached.Years, cached.Years);
    }

    [Fact]
    public async Task Новий_актуальний_прогін_перечитує_розрахункові_рядки()
    {
        var s = await ArrangeAsync();
        await WriteAsync(s, 0.5m);
        var cache = new Hse400CompositionCache();
        var results = Substitute.For<ICalculationResultStore>();
        results.ReadCurrentAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CalculationResultRow>>([]));

        await ReadAsync(s, cache, counter: null, results);
        await ReadAsync(s, cache, counter: null, results);
        Assert.Equal(1, cache.Hits);
        await results.Received(1).ReadCurrentAsync(s.DocumentId, PeriodKeyValue, Arg.Any<CancellationToken>());

        // Документ отримав актуальний прогін (так з'являються нові F_<X>): інша множина прогонів — інший відбиток.
        await using (var db = s.Builder.CreateContext())
        {
            var run = new CalculationRun(s.ProjectId, PeriodKeyValue, null, DateTime.UtcNow, s.DocumentId);
            db.CalculationRuns.Add(run);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE calc.CalculationRun SET Status = 'Current' WHERE Id = {run.Id}");
        }

        await ReadAsync(s, cache, counter: null, results);

        Assert.Equal(2, cache.Misses);
        Assert.Equal(1, cache.Hits);
        await results.Received(2).ReadCurrentAsync(s.DocumentId, PeriodKeyValue, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Interleaved_коміти_транзакцій_запису_не_ховають_правку_від_відбитка()
    {
        // H1: версію рядка видає UPDATE на початку транзакції, а комітяться вони в іншому порядку (RCSI).
        var s = await ArrangeAsync();
        await WriteRowAsync(s, s.RowId, 1m, touch: true);
        await WriteRowAsync(s, s.SecondRowId, 2m, touch: true);
        var cache = new Hse400CompositionCache();

        await using var conn1 = new Microsoft.Data.SqlClient.SqlConnection(sql.ConnectionString);
        await conn1.OpenAsync();
        await using var tx1 = (Microsoft.Data.SqlClient.SqlTransaction)await conn1.BeginTransactionAsync();
        await using (var update = conn1.CreateCommand())
        {
            update.Transaction = tx1;
            // T1: бере МЕНШУ версію (раніше), але не комітить.
            update.CommandText = $"UPDATE doc.CellValue SET ValueNumeric = 9 WHERE PeriodKey = {PeriodKeyValue} AND TableRowId = {s.RowId} AND ColumnDefId = {s.MonthColumnId};"
                + $"UPDATE doc.TableRow SET ModifiedAt = SYSUTCDATETIME() WHERE PeriodKey = {PeriodKeyValue} AND Id = {s.RowId};";
            await update.ExecuteNonQueryAsync();
        }

        // T2: бере БІЛЬШУ версію й комітить першою.
        await WriteRowAsync(s, s.SecondRowId, 3m, touch: true);

        var during = await ReadAsync(s, cache, counter: null);
        Assert.DoesNotContain(during.Rows, r => r.Value == 9m);
        Assert.Contains(during.Rows, r => r.Value == 3m);

        await tx1.CommitAsync();

        var after = await ReadAsync(s, cache, counter: null);
        Assert.Contains(after.Rows, r => r.Value == 9m);
        Assert.Equal(2, cache.Misses);
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public async Task Запис_без_піднімання_версії_рядка_видно_коли_на_таблиці_є_формула()
    {
        // M2: перерахунок формул документа пише комірки з TouchedRowIds: [] — версія рядка не міняється.
        var s = await ArrangeAsync();
        await WriteAsync(s, 0.1m);
        await using (var db = sql.CreateContext())
        {
            var formula = new FormulaDef(s.TableDefId, FormulaScope.Column, "1", ExpressionDialect.Template);
            formula.AssignColumn(s.MonthColumnId);
            db.FormulaDefs.Add(formula);
            await db.SaveChangesAsync();
        }

        var cache = new Hse400CompositionCache();
        var before = await ReadAsync(s, cache, counter: null);
        Assert.Contains(before.Rows, r => r.Value == 0.1m);

        await WriteRowAsync(s, s.RowId, 0.2m, touch: false);

        var after = await ReadAsync(s, cache, counter: null);
        Assert.Contains(after.Rows, r => r.Value == 0.2m);
        Assert.DoesNotContain(after.Rows, r => r.Value == 0.1m);
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public async Task М_яке_й_фізичне_видалення_рядка_змінює_склад()
    {
        var s = await ArrangeAsync();
        await WriteRowAsync(s, s.RowId, 1.5m, touch: true);
        await WriteRowAsync(s, s.SecondRowId, 2.5m, touch: true);
        var cache = new Hse400CompositionCache();

        var all = await ReadAsync(s, cache, counter: null);
        Assert.Contains(all.Rows, r => r.Value == 1.5m);
        Assert.Contains(all.Rows, r => r.Value == 2.5m);

        await ExecAsync($"UPDATE doc.TableRow SET IsDeleted = 1 WHERE PeriodKey = {PeriodKeyValue} AND Id = {s.RowId}");
        var soft = await ReadAsync(s, cache, counter: null);
        Assert.DoesNotContain(soft.Rows, r => r.Value == 1.5m);
        Assert.Contains(soft.Rows, r => r.Value == 2.5m);

        await ExecAsync($"DELETE FROM doc.CellValue WHERE PeriodKey = {PeriodKeyValue} AND TableRowId = {s.SecondRowId}; "
            + $"DELETE FROM doc.TableRow WHERE PeriodKey = {PeriodKeyValue} AND Id = {s.SecondRowId}");
        var hard = await ReadAsync(s, cache, counter: null);
        Assert.DoesNotContain(hard.Rows, r => r.Value == 2.5m);
        Assert.Equal(3, cache.Misses);
    }

    [Fact]
    public async Task Затвердження_місяця_змінює_склад_за_ApprovedOnly()
    {
        var s = await ArrangeAsync();
        await WriteAsync(s, 0.6m);
        var cache = new Hse400CompositionCache();

        var draft = await ReadAsync(s, cache, counter: null, approvedOnly: true);
        Assert.DoesNotContain(draft.Rows, r => r.Value == 0.6m);

        await using (var db = sql.CreateContext())
        {
            var state = new Ecr.Domain.Entities.Workflow.ApprovalState(s.DocumentId, s.SheetDefId, PeriodKeyValue);
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync();
            db.Entry(state).Property(x => x.Status).CurrentValue = DocumentStatus.Approved;
            db.Entry(state).Property(x => x.ApprovedAt).CurrentValue = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var approved = await ReadAsync(s, cache, counter: null, approvedOnly: true);
        Assert.Contains(approved.Rows, r => r.Value == 0.6m);
        Assert.Equal(0, cache.Hits);

        await ExecAsync($"UPDATE wf.ApprovalState SET Status = 0 WHERE DocumentId = {s.DocumentId}");
        var reopened = await ReadAsync(s, cache, counter: null, approvedOnly: true);
        Assert.DoesNotContain(reopened.Rows, r => r.Value == 0.6m);
    }

    [Fact]
    public async Task Перейменований_код_таблиці_змінює_відбиток()
    {
        var s = await ArrangeAsync();
        await WriteAsync(s, 0.4m);
        var cache = new Hse400CompositionCache();

        var before = await ReadAsync(s, cache, counter: null);
        Assert.Contains(before.Rows, r => r.Value == 0.4m);

        // Інший канонічний потік: склад мусить перерахуватись, а не лишитись під старим кодом.
        await ExecAsync($"UPDATE cfg.TableDef SET Code = 'FG_Makat' WHERE Id = {s.TableDefId}");

        var after = await ReadAsync(s, cache, counter: null);
        Assert.Equal(2, cache.Misses);
        Assert.Contains(after.Rows, r => r.Value == 0.4m && r.Stream == "FG_Makat");
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scene(
        TestDocumentBuilder Builder,
        long DocumentId,
        int ProjectId,
        string TemplateCode,
        long InstanceId,
        long RowId,
        int MonthColumnId,
        int TableDefId,
        long SecondRowId,
        int SheetDefId);

    private async Task<Scene> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var source = await builder.BuildAsync(PeriodKeyValue, columnCount: 1, rowCount: 1);
        var now = DateTime.UtcNow;

        await using var db = builder.CreateContext();
        var table = new TableDef(
            source.SheetDefId, EcrCode.Create(Stream), Name(Stream), 50, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var month = new ColumnDef(table.Id, EcrCode.Create(MonthCode), Name(MonthCode), 1, CellDataType.Decimal);
        month.SetMonth(true, (byte)Month);
        db.ColumnDefs.Add(month);
        db.RowDefs.Add(new RowDef(table.Id, RowKey.Create(RowKeyValue), 1, Name(RowKeyValue), RowKind.Item));
        db.RowDefs.Add(new RowDef(table.Id, RowKey.Create(SecondRowKey), 2, Name(SecondRowKey), RowKind.Item));
        await db.SaveChangesAsync();

        var ids = new BulkCellLoader(sql.ConnectionString, 1000);
        var instance = await ids.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var row = await ids.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);
        var key = new PeriodKey(PeriodKeyValue);
        db.TableInstances.Add(new TableInstance(key, instance, source.DocumentId, table.Id, now));
        var row2 = await ids.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);
        db.TableRows.Add(new TableRow(key, row, instance, RowKey.Create(RowKeyValue), 1, now));
        db.TableRows.Add(new TableRow(key, row2, instance, RowKey.Create(SecondRowKey), 2, now));
        await db.SaveChangesAsync();

        var code = await db.Templates.Where(t => t.Id == source.TemplateId).Select(t => t.Code).SingleAsync();
        return new Scene(builder, source.DocumentId, source.ProjectId, code, instance, row, month.Id, table.Id, row2, source.SheetDefId);
    }

    /// <summary>Штатний запис комірки джерела (<c>NormalizedCellStore</c>, рядок «торкається» — версія піднімається).</summary>
    private static Task WriteAsync(Scene s, decimal value) => WriteRowAsync(s, s.RowId, value, touch: true);

    private static async Task WriteRowAsync(Scene s, long rowId, decimal value, bool touch)
    {
        await using var db = s.Builder.CreateContext();
        var store = new NormalizedCellStore(db, new ArchiveAwareCellReader(db));
        await store.ApplyAsync(
            new CellChangeSet(
                s.InstanceId,
                [new CellRecord(new CellAddress(new PeriodKey(PeriodKeyValue), rowId, s.MonthColumnId), s.TableDefId,
                    new CellValueData { ValueNumeric = value })],
                [],
                touch ? [rowId] : [],
                ChangedByUserId: 1,
                IsLateEdit: false),
            CancellationToken.None);
    }

    private async Task ExecAsync(string sqlText)
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(sql.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sqlText;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(IReadOnlyList<Ecr.Application.Registries.Hse400CompositionRow> Rows, IReadOnlyCollection<int> Years)> ReadAsync(
        Scene s, Hse400CompositionCache? cache, CellQueryCounter? counter, ICalculationResultStore? results = null,
        bool approvedOnly = false)
    {
        await using var db = counter is null ? sql.CreateContext() : sql.CreateContext(counter);
        var loader = new Hse400CompositionSnapshotLoader(
            Substitute.For<IRegistrySnapshotLoader>(),
            db,
            results ?? EmptyResults(),
            enabled: true,
            registryCode: "PERF11_UNUSED",
            templateCode: s.TemplateCode,
            cache: cache);
        return await loader.ReadDocumentsAsync(approvedOnly, CancellationToken.None);
    }

    private static ICalculationResultStore EmptyResults()
    {
        var results = Substitute.For<ICalculationResultStore>();
        results.ReadCurrentAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CalculationResultRow>>([]));
        return results;
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Лічить команди EF, що читають <c>doc.CellValue</c>.</summary>
    private sealed class CellQueryCounter : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public int CellValueReads { get; private set; }

        public override System.Threading.Tasks.ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>>
            ReaderExecutingAsync(
                System.Data.Common.DbCommand command,
                Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
                Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[doc].[CellValue]", StringComparison.Ordinal))
            {
                CellValueReads++;
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
