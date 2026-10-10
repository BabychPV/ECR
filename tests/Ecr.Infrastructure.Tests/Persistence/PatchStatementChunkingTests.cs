// tests/Ecr.Infrastructure.Tests/Persistence/PatchStatementChunkingTests.cs
using System.Data;
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// P1-02: батч до 50 000 комірок не їде одним <c>MERGE doc.CellValue</c> і одним <c>INSERT aud.CellChange</c> -
/// кожен оператор лишається під порогом ескалації блокувань (5 000 на таблицю чи індекс), а атомарність,
/// порядок і результат ті самі.
/// </summary>
/// <remarks>
/// ⛔ ДОВІД ДЕФЕКТУ — контрольна фаза: з розміром порції <see cref="int.MaxValue"/> (колишня поведінка) той самий
/// батч у 12 000 комірок ескалує блокування до таблиці (X на <c>doc.CellValue</c> / <c>aud.CellChange</c> у
/// <c>sys.dm_tran_locks</c>); зі штатним розміром - ні. Тест міряє справжній SQL Server, а не припущення.
/// <para>
/// Мутації: поставити <c>DefaultUpsertChunkSize</c>/<c>DefaultInsertChunkSize</c> у <c>int.MaxValue</c> - червоніють
/// обидва тести ескалації; комітити кожну порцію окремо (власна транзакція на порцію) - червоніють тести
/// атомарності (рядки лишаються після відкату).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class PatchStatementChunkingTests
{
    private const int Period = 202610;

    /// <summary>Понад два штатні розміри порції (2 000), під 50 000 і значно над порогом ескалації (5 000).</summary>
    private const int Big = 12_000;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P1-02")]
    public async Task Великий_MERGE_комірок_не_ескалує_блокування_до_таблиці_а_колишній_один_оператор_ескалує()
    {
        var database = SqlServerFixture.WithOwnDatabase("_p102a");
        await database.InitializeAsync();
        try
        {
            await ExecAsync(database.ConnectionString, "ALTER TABLE doc.CellValue NOCHECK CONSTRAINT ALL;");

            // Контроль: КОЛИШНЯ поведінка - один оператор на весь батч.
            var legacy = await EscalatedAsync(
                database, "doc.CellValue", (db, ct) => ApplyAsync(db, Cells(0, Big), int.MaxValue, ct));
            Assert.True(
                legacy.Escalated,
                "Контроль не відтворив дефект: один MERGE на 12 000 комірок не ескалував блокування - вимір не має "
                + $"предмета. Блокування: {legacy.Report}");

            // Штатна поведінка: оператори по DefaultUpsertChunkSize.
            var chunked = await EscalatedAsync(
                database, "doc.CellValue",
                (db, ct) => ApplyAsync(db, Cells(100_000, Big), NormalizedCellStore.DefaultUpsertChunkSize, ct));
            Assert.False(chunked.Escalated, $"Порційний MERGE усе одно ескалував блокування: {chunked.Report}");
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P1-02")]
    public async Task Великий_INSERT_аудиту_не_ескалує_блокування_до_таблиці_а_колишній_один_оператор_ескалує()
    {
        var database = SqlServerFixture.WithOwnDatabase("_p102b");
        await database.InitializeAsync();
        try
        {
            var legacy = await EscalatedAsync(
                database, "aud.CellChange",
                (db, ct) => new AuditWriter(db) { InsertChunkSize = int.MaxValue }.WriteCellChangesAsync(Changes(1, Big), ct));
            Assert.True(
                legacy.Escalated,
                "Контроль не відтворив дефект: один INSERT на 12 000 записів аудиту не ескалував блокування. "
                + $"Блокування: {legacy.Report}");

            var chunked = await EscalatedAsync(
                database, "aud.CellChange",
                (db, ct) => new AuditWriter(db).WriteCellChangesAsync(Changes(2, Big), ct));
            Assert.False(chunked.Escalated, $"Порційний INSERT усе одно ескалував блокування: {chunked.Report}");
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P1-02")]
    public async Task Порційний_запис_дає_той_самий_результат_що_один_оператор_і_відкочується_цілком()
    {
        var database = SqlServerFixture.WithOwnDatabase("_p102c");
        await database.InitializeAsync();
        try
        {
            var cs = database.ConnectionString;
            await ExecAsync(cs, "ALTER TABLE doc.CellValue NOCHECK CONSTRAINT ALL;");

            const int count = 5_300; // 2 000 + 2 000 + 1 300: три порції, остання неповна.
            var cells = Cells(0, count);

            // Власна транзакція сховища (ambient немає): усе записано, лічильник змінених рядків - сума порцій.
            await using (var db = Context(cs))
            {
                var store = new NormalizedCellStore(db);
                await ApplyAsync(db, cells, store.UpsertChunkSize, CancellationToken.None, store);
                Assert.Equal(count, store.LastUpsertRowsAffected);
            }

            Assert.Equal(count, await ScalarAsync<int>(cs, $"SELECT COUNT(*) FROM doc.CellValue WHERE PeriodKey = {Period}"));
            Assert.Equal(
                5299m,
                await ScalarAsync<decimal>(cs, $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {Period} AND TableRowId = {5299 / 10 + 1} AND ColumnDefId = {5299 % 10 + 1}"));
            Assert.Equal(
                count,
                await ScalarAsync<int>(cs, $"SELECT COUNT(*) FROM doc.CellValue WHERE PeriodKey = {Period} AND ValueNumeric IS NOT NULL"));

            // Повтор без змін - 0 змінених рядків (умова WR-06 діє в КОЖНІЙ порції); зміна перших 3 000 - рівно 3 000
            // (порція 1 цілком, порція 2 частково): результат той самий, що дав би один MERGE.
            await using (var db = Context(cs))
            {
                var store = new NormalizedCellStore(db);
                await ApplyAsync(db, cells, store.UpsertChunkSize, CancellationToken.None, store);
                Assert.Equal(0, store.LastUpsertRowsAffected);
            }

            await using (var db = Context(cs))
            {
                var store = new NormalizedCellStore(db);
                await ApplyAsync(db, Cells(0, count, bump: i => i < 3_000), store.UpsertChunkSize, CancellationToken.None, store);
                Assert.Equal(3_000, store.LastUpsertRowsAffected);
            }

            Assert.Equal(
                10_000m,
                await ScalarAsync<decimal>(cs, $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {Period} AND TableRowId = 1 AND ColumnDefId = 1"));

            // Дубль адреси в батчі, що ріжеться, - відмова ДО запису (в одному MERGE він теж падав).
            await using (var db = Context(cs))
            {
                var withDuplicate = Cells(50_000, 4_500);
                withDuplicate = [.. withDuplicate, withDuplicate[10]];
                await Assert.ThrowsAsync<ArgumentException>(() =>
                    ApplyAsync(db, withDuplicate, NormalizedCellStore.DefaultUpsertChunkSize, CancellationToken.None));
            }

            Assert.Equal(0, await ScalarAsync<int>(cs, $"SELECT COUNT(*) FROM doc.CellValue WHERE PeriodKey = {Period} AND TableRowId > 5000"));

            // ⛔ Атомарність: остання (третя) порція падає на задовгому значенні (nvarchar(1000), помилка сервера 2628) - дві попередні, що вже
            // виконано в транзакції, не лишаються ні для власної транзакції сховища, ні для ambient (відкат викликача).
            var poisoned = Cells(200_000, 4_500);
            poisoned[^1] = poisoned[^1] with { Value = new CellValueData { ValueString = new string('x', 1_500) } };

            await using (var db = Context(cs))
            {
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    ApplyAsync(db, poisoned, NormalizedCellStore.DefaultUpsertChunkSize, CancellationToken.None));
            }

            await using (var db = Context(cs))
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    ApplyAsync(db, poisoned, NormalizedCellStore.DefaultUpsertChunkSize, CancellationToken.None));
                await tx.RollbackAsync();
            }

            Assert.Equal(0, await ScalarAsync<int>(cs, $"SELECT COUNT(*) FROM doc.CellValue WHERE PeriodKey = {Period} AND TableRowId > 20000"));
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P1-02")]
    public async Task Порційний_аудит_пише_всі_рядки_в_порядку_батча_і_відкочується_цілком()
    {
        var database = SqlServerFixture.WithOwnDatabase("_p102d");
        await database.InitializeAsync();
        try
        {
            var cs = database.ConnectionString;

            // 4 500 записів = 2 000 + 2 000 + 500. Id зростають у порядку батча.
            await using (var db = Context(cs))
            {
                await new AuditWriter(db).WriteCellChangesAsync(Changes(7, 4_500), CancellationToken.None);
            }

            var values = await ColumnAsync(cs, "SELECT NewValue FROM aud.CellChange WHERE DocumentId = 7 ORDER BY Id");
            Assert.Equal(4_500, values.Count);
            Assert.Equal([.. Enumerable.Range(0, 4_500).Select(i => "v" + i.ToString(CultureInfo.InvariantCulture))], values);

            // ⛔ Атомарність без ambient-транзакції: остання порція падає (старе значення довше за nvarchar(1000)) -
            // дві попередні не лишаються в журналі.
            var poisoned = Changes(8, 4_500).ToList();
            poisoned[^1] = poisoned[^1] with { OldValue = new string('x', 1_500) };

            await using (var db = Context(cs))
            {
                var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                    new AuditWriter(db).WriteCellChangesAsync(poisoned, CancellationToken.None));
                Assert.NotNull(error);
            }

            Assert.Equal(0, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = 8"));

            // З ambient-транзакцією викликача - те саме після її відкату (журнал разом з даними, WR-08).
            await using (var db = Context(cs))
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await Assert.ThrowsAnyAsync<Exception>(() =>
                    new AuditWriter(db).WriteCellChangesAsync(poisoned, CancellationToken.None));
                await tx.RollbackAsync();
            }

            Assert.Equal(0, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = 8"));
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    /// <summary>
    /// Виконує <paramref name="write"/> у відкритій транзакції, дивиться блокування сесії на таблиці й відкочує.
    /// </summary>
    private static async Task<(bool Escalated, string Report)> EscalatedAsync(
        SqlServerFixture database, string table, Func<EcrDbContext, CancellationToken, Task> write)
    {
        await using var db = Context(database.ConnectionString);
        await using var tx = await db.Database.BeginTransactionAsync();

        await write(db, CancellationToken.None);

        // X-блокування рівня ОБ'ЄКТА (або всього heap/B-tree) - і є ескалація: рядкові й сторінкові X-блокування
        // мають тип KEY/PAGE/RID, а OBJECT під DML лише IX.
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = tx.GetDbTransaction();
        command.CommandText = $"""
            SELECT l.resource_type, l.request_mode, COUNT(*) AS n
              FROM sys.dm_tran_locks AS l
             WHERE l.request_session_id = @@SPID AND l.resource_database_id = DB_ID() AND l.request_status = N'GRANT'
               AND ((l.resource_type = N'OBJECT' AND l.resource_associated_entity_id = OBJECT_ID(N'{table}'))
                 OR l.resource_associated_entity_id IN (SELECT hobt_id FROM sys.partitions WHERE object_id = OBJECT_ID(N'{table}')))
             GROUP BY l.resource_type, l.request_mode;
            """;

        var report = new List<string>();
        var escalated = false;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var type = reader.GetString(0);
                var mode = reader.GetString(1);
                report.Add($"{type}/{mode}={reader.GetInt32(2).ToString(CultureInfo.InvariantCulture)}");
                escalated |= (type is "OBJECT" or "HOBT") && mode is "X" or "SIX" or "S";
            }
        }

        await tx.RollbackAsync();

        return (escalated, string.Join("; ", report));
    }

    private static async Task ApplyAsync(
        EcrDbContext db,
        IReadOnlyList<CellRecord> cells,
        int chunkSize,
        CancellationToken ct,
        NormalizedCellStore? store = null)
    {
        store ??= new NormalizedCellStore(db) { UpsertChunkSize = chunkSize };
        await store.ApplyAsync(
            new CellChangeSet(
                TableInstanceId: 1, Upserts: cells, Deletes: [], TouchedRowIds: [], ChangedByUserId: 1, IsLateEdit: false),
            ct);
    }

    /// <summary>Комірки з унікальними адресами: рядок <c>i / 10 + 1 + start/10</c>, колонка <c>i % 10 + 1</c>.</summary>
    /// <param name="start">Зсув номера (рядок = <c>(start + i) / 10 + 1</c>).</param>
    /// <param name="count">Скільки комірок.</param>
    /// <param name="bump">Для яких номерів значення змінюється (10 000 + i).</param>
    private static List<CellRecord> Cells(int start, int count, Func<int, bool>? bump = null)
        => [.. Enumerable.Range(0, count).Select(i =>
        {
            var n = start + i;
            var value = bump is not null && bump(i) ? 10_000m + i : n;

            return new CellRecord(
                new CellAddress(new PeriodKey(Period), (n / 10) + 1, (n % 10) + 1),
                TableDefId: 1,
                new CellValueData { ValueNumeric = value });
        })];

    private static List<CellChangeRecord> Changes(long documentId, int count)
        => [.. Enumerable.Range(0, count).Select(i => new CellChangeRecord(
            new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc).AddSeconds(i),
            new CellAddress(new PeriodKey(Period), (i / 10) + 1, (i % 10) + 1),
            documentId,
            "R" + (i / 10).ToString(CultureInfo.InvariantCulture),
            OldValue: "0",
            NewValue: "v" + i.ToString(CultureInfo.InvariantCulture),
            ChangedByUserId: 1,
            Origin: "UserEdit",
            IsLateEdit: false,
            CorrelationId: null))];

    private static EcrDbContext Context(string connectionString)
        => new(EfWarningGuard.Apply(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(connectionString)).Options);

    private static async Task ExecAsync(string cs, string text)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string cs, string text)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static async Task<List<string>> ColumnAsync(string cs, string text)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }
}
