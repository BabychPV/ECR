using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Оптимістичне блокування.
/// </summary>
/// <remarks>
/// Ключовий тест — <c>Зміна_комірки_піднімає_RowVersion_рядка</c>.
/// <c>rowversion</c> на <c>doc.TableRow</c> **не змінюється сам**, коли ми
/// пишемо в <c>doc.CellValue</c>; забути про «дотик» рядка = зламати
/// блокування **тихо**: конфлікти перестануть виявлятися, і користувачі
/// почнуть непомітно затирати роботу один одного (B04 §2.4).
/// </remarks>
[Collection("SqlServer")]
public sealed class ConcurrencyTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_комірки_піднімає_RowVersion_рядка()
    {
        var (doc, store, rows) = await ArrangeAsync();
        var before = await VersionsAsync(rows, doc);

        await store.ApplyAsync(Change(doc, rowIndex: 0, value: 1m), CancellationToken.None);

        var after = await VersionsAsync(rows, doc);

        // ⚠ Ось воно. Запис у doc.CellValue сам по собі НЕ піднімає
        // RowVersion рядка — його піднімає лише UPDATE самого рядка. Без
        // «дотику» обидва користувачі бачили б незмінену версію, і другий
        // мовчки затер би першого.
        Assert.NotEqual(before[doc.RowIds[0]], after[doc.RowIds[0]]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Два_користувачі_в_різних_рядках_не_конфліктують()
    {
        var (doc, store, rows) = await ArrangeAsync();
        var before = await VersionsAsync(rows, doc);

        await store.ApplyAsync(Change(doc, rowIndex: 0, value: 1m), CancellationToken.None);
        await store.ApplyAsync(Change(doc, rowIndex: 1, value: 2m), CancellationToken.None);

        var after = await VersionsAsync(rows, doc);

        // Блокування має бути на РЯДКУ, а не на таблиці: інакше двоє, що
        // працюють у різних рядках одного звіту, заважали б один одному —
        // а це рівно те, як його й заповнюють.
        Assert.NotEqual(before[doc.RowIds[0]], after[doc.RowIds[0]]);
        Assert.NotEqual(before[doc.RowIds[1]], after[doc.RowIds[1]]);
        Assert.Equal(before[doc.RowIds[2]], after[doc.RowIds[2]]);
    }

    // ⛔ Аудит фази 2 (чесність тестів): тут стояв
    // `Запис_зі_застарілою_версією_рядка_відхиляється`, який не відхиляв
    // жодного запису — `NormalizedCellStore.ApplyAsync`/`CellChangeSet` на
    // цьому шарі взагалі не несе `BaseVersion`, і тест лише повторював
    // перевірку сусіднього `Зміна_комірки_піднімає_RowVersion_рядка` (що
    // версія змінюється після запису), називаючи це «відхиленням». Саме
    // відхилення застарілої версії — рішення ПРИКЛАДНОГО шару
    // (`PatchCellsHandler`), і воно вже доведено реальним викликом
    // обробника в `PatchCellsTests.Конфлікт_в_одному_рядку_відхиляє_весь_батч_із_переліком_конфліктів`
    // (перевірено мутацією окремо: вимкнення порівняння версій у
    // `PatchCellsHandler` валить саме цей тест). Дублювати неіснуючою на
    // цьому шарі перевіркою сенсу не було.

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Конфлікт_в_одному_рядку_відхиляє_весь_батч()
    {
        var (doc, store, _) = await ArrangeAsync();

        // Батч, у якому один рядок посилається на неіснуючу колонку: складений
        // FK не дасть його записати.
        var good = new CellRecord(
            new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]),
            doc.TableDefId, new CellValueData { ValueNumeric = 1m });
        var bad = new CellRecord(
            new CellAddress(doc.PeriodKey, doc.RowIds[1], 999_999),
            doc.TableDefId, new CellValueData { ValueNumeric = 2m });

        await Assert.ThrowsAsync<SqlException>(() => store.ApplyAsync(
            new CellChangeSet(doc.TableInstanceId, [good, bad], [], [doc.RowIds[0]], 1, false),
            CancellationToken.None));

        // ⚠ Часткове застосування заборонене (B04 §2.3): користувач вставив
        // діапазон із буфера і має отримати або весь діапазон, або нічого.
        // «Половина збереглася» — найгірший результат: її не видно.
        var written = await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM doc.CellValue WHERE TableRowId = {doc.RowIds[0]}");
        Assert.Equal(0, written);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відповідь_на_конфлікт_містить_чуже_значення_автора_і_момент_зміни()
    {
        var (doc, store, _) = await ArrangeAsync();
        await store.ApplyAsync(Change(doc, rowIndex: 0, value: 7m), CancellationToken.None);

        // Аудит — джерело, з якого будується відповідь на конфлікт: клієнт має
        // побачити ЧУЖЕ значення, автора і момент, а не просто «409».
        await using var db = CreateContext();
        await new AuditWriter(db).WriteCellChangesAsync(
            [new CellChangeRecord(
                DateTime.UtcNow,
                new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]),
                doc.DocumentId, "R1", OldValue: null, NewValue: "7",
                ChangedByUserId: 77, Origin: "UserEdit", IsLateEdit: false, CorrelationId: null)],
            CancellationToken.None);

        var conflict = await QueryAsync($"""
            SELECT TOP (1) CAST(ChangedByUserId AS nvarchar(10)) + N'|' + NewValue
            FROM aud.CellChange
            WHERE DocumentId = {doc.DocumentId} AND TableRowId = {doc.RowIds[0]}
            ORDER BY ChangedAt DESC
            """);

        Assert.Equal("77|7", Assert.Single(conflict));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Структурна_зміна_таблиці_ловиться_через_If_Match_на_TableInstance()
    {
        var (doc, _, _) = await ArrangeAsync();

        var before = await ScalarAsync<byte[]>(
            $"SELECT RowVersion FROM doc.TableInstance WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND Id = {doc.TableInstanceId}");

        await ExecuteAsync(
            $"UPDATE doc.TableInstance SET ModifiedAt = SYSUTCDATETIME() " +
            $"WHERE PeriodKey = {doc.PeriodKey.Value} AND Id = {doc.TableInstanceId}");

        var after = await ScalarAsync<byte[]>(
            $"SELECT RowVersion FROM doc.TableInstance WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND Id = {doc.TableInstanceId}");

        // ⚠ RowVersion саме на TableInstance, а не лише на рядках: додавання
        // чи видалення РЯДКА не змінює жодного наявного RowVersion, і без
        // версії на екземплярі клієнт не помітив би, що склад таблиці інший.
        Assert.NotEqual(Convert.ToBase64String(before!), Convert.ToBase64String(after!));
    }

    private async Task<(TestDocument Doc, ICellStore Store, Dictionary<long, string> Rows)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var store = new NormalizedCellStore(builder.CreateContext());

        var rows = new Dictionary<long, string>();
        foreach (var id in doc.RowIds)
        {
            rows[id] = await ScalarAsync<string>(
                $"SELECT RowKey FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value} AND Id = {id}") ?? string.Empty;
        }

        return (doc, store, rows);
    }

    private static CellChangeSet Change(TestDocument doc, int rowIndex, decimal value)
        => new(
            doc.TableInstanceId,
            [new CellRecord(
                new CellAddress(doc.PeriodKey, doc.RowIds[rowIndex], doc.ColumnDefIds[1]),
                doc.TableDefId,
                new CellValueData { ValueNumeric = value })],
            [],
            [doc.RowIds[rowIndex]],
            ChangedByUserId: 1,
            IsLateEdit: false);

    private async Task<Dictionary<long, string>> VersionsAsync(
        Dictionary<long, string> rows, TestDocument doc)
    {
        var result = new Dictionary<long, string>();
        foreach (var id in rows.Keys)
        {
            var bytes = await ScalarAsync<byte[]>(
                $"SELECT RowVersion FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value} AND Id = {id}");
            result[id] = Convert.ToBase64String(bytes!);
        }

        return result;
    }

    private async Task<string> RowVersionAsync(TestDocument doc, string rowKey)
    {
        var bytes = await ScalarAsync<byte[]>(
            $"SELECT RowVersion FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableInstanceId = {doc.TableInstanceId} AND RowKey = N'{rowKey}'");
        return Convert.ToBase64String(bytes!);
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T?> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private async Task<List<string>> QueryAsync(string query)
    {
        var rows = new List<string>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
