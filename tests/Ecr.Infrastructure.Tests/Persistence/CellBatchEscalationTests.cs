using System.Diagnostics;
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// PSS-05 (аудит 2026-11-20): великий пакет комірок не повинен ескалувати замки до таблиці —
/// запис <c>doc.CellValue</c> / <c>aud.CellChange</c> іде чанками ≤ 4000 рядків у ТІЙ САМІЙ транзакції.
/// </summary>
/// <remarks>
/// ⚠ Лічильник — <c>sys.dm_db_index_operational_stats.index_lock_promotion_count</c> (кумулятивний),
/// тож міряється ДЕЛЬТА навколо запису. Транзакційна семантика — окремий тест: відкіт прибирає і комірки, і аудит.
/// </remarks>
[Collection("SqlServer")]
public sealed class CellBatchEscalationTests(SqlServerFixture sql)
{
    private const int PeriodKeyValue = 202610;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "PSS-05")]
    public async Task Пакет_20000_комірок_не_ескалує_замки_і_пише_комірки_з_аудитом_разом()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, columnCount: 4, rowCount: 5000, ct: CancellationToken.None);

        var before = await PromotionsAsync();
        await using (var db = builder.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await WriteAsync(db, doc);
            await tx.CommitAsync();
        }

        var promotions = await PromotionsAsync() - before;

        Assert.Equal(20_000, await ScalarAsync($"SELECT COUNT(*) FROM doc.CellValue WHERE TableRowId IN (SELECT Id FROM doc.TableRow WHERE TableInstanceId = {doc.TableInstanceId} AND PeriodKey = {PeriodKeyValue})"));
        Assert.Equal(20_000, await ScalarAsync($"SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = {doc.DocumentId}"));
        Assert.True(promotions == 0, $"index_lock_promotion_count delta = {promotions}");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "PSS-05")]
    public async Task Відкіт_пакета_з_кількох_чанків_прибирає_і_комірки_і_аудит()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, columnCount: 4, rowCount: 2500, ct: CancellationToken.None);

        await using (var db = builder.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await WriteAsync(db, doc); // 10 000 комірок = 3 чанки
            await tx.RollbackAsync();
        }

        Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM doc.CellValue WHERE TableRowId IN (SELECT Id FROM doc.TableRow WHERE TableInstanceId = {doc.TableInstanceId} AND PeriodKey = {PeriodKeyValue})"));
        Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = {doc.DocumentId}"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "PSS-05")]
    public async Task Відкритий_пакет_6000_комірок_не_блокує_запис_100_комірок_в_іншому_проєкті()
    {
        // ⚠ Розкладка ключів PK_CellValue (період, рядок, колонка): A < S1 < B < S2. Кожен із S1/S2 уже має
        // закомічену комірку. HOLDLOCK (serializable MERGE) тримає RangeS-S на «наступному ключі» за
        // найвищою адресою — якби над A і B не було чужих ключів, обидва прийшли б на той самий «нескінченний»
        // ключ і B чекав би на RangeI-N НЕ через ескалацію, а через серіалізований діапазон (у проді вище завжди
        // є ключі інших документів). Тест міряє саме ескалацію, тож «стеля» кожного пакета — окремий S.
        var builderA = new TestDocumentBuilder(sql.ConnectionString);
        var docA = await builderA.BuildAsync(PeriodKeyValue, columnCount: 4, rowCount: 1500, ct: CancellationToken.None);
        var builderS1 = new TestDocumentBuilder(sql.ConnectionString);
        var docS1 = await builderS1.BuildAsync(PeriodKeyValue, columnCount: 4, rowCount: 1, ct: CancellationToken.None);
        var builderB = new TestDocumentBuilder(sql.ConnectionString);
        var docB = await builderB.BuildAsync(PeriodKeyValue, columnCount: 4, rowCount: 25, ct: CancellationToken.None);
        var builderS2 = new TestDocumentBuilder(sql.ConnectionString);
        var docS2 = await builderS2.BuildAsync(PeriodKeyValue, columnCount: 4, rowCount: 1, ct: CancellationToken.None);
        foreach (var (builder, sentinel) in new[] { (builderS1, docS1), (builderS2, docS2) })
        {
            await using var dbS = builder.CreateContext();
            await WriteAsync(dbS, sentinel);
        }

        await using var dbA = builderA.CreateContext();
        await using var txA = await dbA.Database.BeginTransactionAsync();
        await WriteAsync(dbA, docA); // тримаємо відкритою

        var clock = Stopwatch.StartNew();
        await using (var dbB = builderB.CreateContext())
        {
            await using var txB = await dbB.Database.BeginTransactionAsync();
            await WriteAsync(dbB, docB);
            await txB.CommitAsync();
        }

        clock.Stop();
        await txA.RollbackAsync();

        // LOCK_TIMEOUT запису — 15 с (409 ECR-DOC-4091); без ескалації другий запис не чекає зовсім.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Запис в іншому проєкті чекав {clock.Elapsed}");
    }

    private static async Task WriteAsync(Ecr.Infrastructure.Persistence.EcrDbContext db, TestDocument doc)
    {
        var cells = new List<CellRecord>();
        var audit = new List<CellChangeRecord>();
        var at = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
        foreach (var row in doc.RowIds)
        {
            foreach (var column in doc.ColumnDefIds)
            {
                var address = new CellAddress(doc.PeriodKey, row, column);
                cells.Add(new CellRecord(address, doc.TableDefId, new CellValueData { ValueNumeric = 1m }));
                audit.Add(new CellChangeRecord(
                    at, address, doc.DocumentId, $"R{row}", null, "1", 42, "UserEdit", false, "pss05"));
            }
        }

        var store = new NormalizedCellStore(db);
        await store.ApplyAsync(
            new CellChangeSet(doc.TableInstanceId, cells, [], [.. doc.RowIds], ChangedByUserId: 42, IsLateEdit: false),
            CancellationToken.None);
        await new AuditWriter(db).WriteCellChangesAsync(audit, CancellationToken.None);
    }

    private async Task<long> PromotionsAsync()
        => await ScalarAsync("""
            SELECT COALESCE(SUM(index_lock_promotion_count), 0)
            FROM sys.dm_db_index_operational_stats(DB_ID(), NULL, NULL, NULL) AS s
            WHERE s.object_id IN (OBJECT_ID(N'doc.CellValue'), OBJECT_ID(N'aud.CellChange'))
            """);

    private async Task<long> ScalarAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(text, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
