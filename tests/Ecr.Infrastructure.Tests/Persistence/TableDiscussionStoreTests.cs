using System.Diagnostics;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Сховище чатів таблиць (TC): підсумок усіх таблиць документа — ОДНА команда до бази, і її вартість у логічних читаннях
/// на синтетичному документі розміру Land (150 таблиць × 200 повідомлень = 30 000 рядків; тестова база обмежена, тому
/// розмір фіксований у тесті, а масштабування — екстраполяція за читаннями).
/// </summary>
/// <remarks>
/// ⚠ Масштаб чесно: реальний Land має сотні таблиць, але розмова є лише в частини; вартість підсумку лінійна за
/// ЧИСЛОМ ТАБЛИЦЬ з чатом (по одному пошуку на таблицю), а не за числом повідомлень — його мірять пороги нижче.
/// Мутаційний доказ: прибрати <c>full.Contains(s.TableDefCode)</c> / додати другий запит у <c>SummaryAsync</c> →
/// червоніє <c>Підсумок_документа_одна_команда</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class TableDiscussionStoreTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private const int Tables = 150;
    private const int PerTable = 200;
    private static readonly int[] FirstQuarter = [202601, 202602, 202603];

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Підсумок_документа_одна_команда_і_дешеві_логічні_читання_на_розмірі_Land()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(true);

        int me;
        int other;
        await using (var setup = builder.CreateContext())
        {
            me = await NewUserAsync(setup, "tcp_me").ConfigureAwait(true);
            other = await NewUserAsync(setup, "tcp_other").ConfigureAwait(true);
        }

        try
        {
            await SeedAsync(document.DocumentId, me, other).ConfigureAwait(true);

            var codes = Enumerable.Range(1, Tables).Select(i => $"T{i:D3}").ToList();
            var counter = new DbCommandCounter();
            await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
                .UseSqlServer(sql.ConnectionString).AddInterceptors(counter).Options);
            var store = new TableDiscussionStore(db);

            // Прогрів плану, потім замір: команда одна; читання — різниця накопичених читань сеансу (те саме з'єднання).
            await store.SummaryAsync(document.DocumentId, codes, new Dictionary<string, IReadOnlyCollection<int>>(), me, CancellationToken.None).ConfigureAwait(true);

            await db.Database.OpenConnectionAsync().ConfigureAwait(true);

            var readsBefore = await SessionReadsAsync(db).ConfigureAwait(true);
            counter.Tally.Reset();
            var clock = Stopwatch.StartNew();
            var rows = await store.SummaryAsync(
                document.DocumentId, codes, new Dictionary<string, IReadOnlyCollection<int>>(), me, CancellationToken.None).ConfigureAwait(true);
            clock.Stop();
            var seen = counter.Tally.Snapshot();
            var fullReads = await SessionReadsAsync(db).ConfigureAwait(true) - readsBefore;

            Assert.Equal(1, seen.Total);
            Assert.Equal(Tables, rows.Count);
            Assert.All(rows, r => Assert.Equal(PerTable, r.Total));
            Assert.All(rows, r => Assert.Equal(PerTable / 2, r.Unread)); // половина повідомлень — чужі, прочитано 0
            output.WriteLine($"summary (full): commands={seen.Total} logical reads={fullReads} ms={clock.ElapsedMilliseconds} tables={Tables} messages={Tables * PerTable}");

            // Роль, звужена періодами: 20 таблиць × 3 періоди рахуються з повідомлень тією самою командою (UNION ALL).
            var restricted = Enumerable.Range(1, 20).ToDictionary(
                i => $"T{i:D3}", _ => (IReadOnlyCollection<int>)FirstQuarter);
            var fullRest = codes.Skip(20).ToList();
            await store.SummaryAsync(document.DocumentId, fullRest, restricted, me, CancellationToken.None).ConfigureAwait(true);
            readsBefore = await SessionReadsAsync(db).ConfigureAwait(true);
            counter.Tally.Reset();
            clock.Restart();
            var mixed = await store.SummaryAsync(document.DocumentId, fullRest, restricted, me, CancellationToken.None).ConfigureAwait(true);
            clock.Stop();
            var mixedCommands = counter.Tally.Snapshot().Total;
            var mixedReads = await SessionReadsAsync(db).ConfigureAwait(true) - readsBefore;

            Assert.Equal(1, mixedCommands);
            Assert.Equal(Tables, mixed.Count);
            Assert.All(mixed.Where(r => string.CompareOrdinal(r.TableCode, "T020") <= 0), r => Assert.True(r.Total is > 0 and < PerTable));
            output.WriteLine($"summary (20 period-restricted tables): logical reads={mixedReads} ms={clock.ElapsedMilliseconds}");

            // Сторінка одного чату: keyset по індексу — незалежно від розміру документа.
            readsBefore = await SessionReadsAsync(db).ConfigureAwait(true);
            clock.Restart();
            var page = await store.ListAsync(document.DocumentId, "T001", null, null, null, null, 51, CancellationToken.None).ConfigureAwait(true);
            clock.Stop();
            var pageReads = await SessionReadsAsync(db).ConfigureAwait(true) - readsBefore;
            Assert.Equal(51, page.Count);
            Assert.True(page[0].Id > page[^1].Id);
            output.WriteLine($"page of 51: logical reads={pageReads} ms={clock.ElapsedMilliseconds}");

            // Пороги: виміряно 894 / 871 / 338 читань (150 таблиць × 200 повідомлень, 43 / 96 / 25 мс), поріг — запас ×3; ріст — лінійний за числом таблиць, не повідомлень.
            Assert.True(fullReads < 3000, $"підсумок: {fullReads} логічних читань");
            Assert.True(mixedReads < 3000, $"підсумок зі звуженням: {mixedReads} логічних читань");
            Assert.True(pageReads < 1000, $"сторінка: {pageReads} логічних читань");
        }
        finally
        {
            await CleanupAsync(document.DocumentId).ConfigureAwait(true);
        }
    }

    /// <summary>Накопичені логічні читання сеансу (з'єднання тримає контекст відкритим) — різниця до/після дає вартість виклику.</summary>
    private static async Task<long> SessionReadsAsync(EcrDbContext db)
        => await db.Database.SqlQueryRaw<long>("SELECT CAST(logical_reads AS bigint) AS Value FROM sys.dm_exec_sessions WHERE session_id = @@SPID")
            .SingleAsync().ConfigureAwait(true);

    private static async Task<int> NewUserAsync(EcrDbContext db, string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new Ecr.Infrastructure.Security.PasswordHasher().Hash("Pw-TableChat-2026!"));
        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(true);
        return user.Id;
    }

    private async Task SeedAsync(long documentId, int me, int other)
    {
        await using var conn = new SqlConnection(sql.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(true);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;
        cmd.CommandText = $"""
            ;WITH n AS (SELECT TOP ({PerTable}) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b),
                  t AS (SELECT TOP ({Tables}) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO doc.TableDiscussionMessage (DocumentId, TableDefCode, PeriodKey, AuthorUserId, Body, CreatedAt)
            SELECT @doc, CONCAT('T', RIGHT(CONCAT('000', t.i), 3)), 202601 + (n.i % 12),
                   CASE WHEN n.i % 2 = 0 THEN @me ELSE @other END,
                   REPLICATE(N'повідомлення ', 10), DATEADD(minute, n.i, '2026-01-01')
            FROM t CROSS JOIN n;

            INSERT INTO doc.TableDiscussion (DocumentId, TableDefCode, Revision, MessageCount, LastMessageId, LastMessageAt)
            SELECT DocumentId, TableDefCode, COUNT(*), COUNT(*), MAX(Id), MAX(CreatedAt)
            FROM doc.TableDiscussionMessage WHERE DocumentId = @doc GROUP BY DocumentId, TableDefCode;
            """;
        cmd.Parameters.AddWithValue("@doc", documentId);
        cmd.Parameters.AddWithValue("@me", me);
        cmd.Parameters.AddWithValue("@other", other);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(true);
    }

    private async Task CleanupAsync(long documentId)
    {
        await using var conn = new SqlConnection(sql.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(true);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;
        cmd.CommandText = """
            DELETE FROM doc.TableDiscussionMention WHERE DocumentId = @doc;
            DELETE FROM doc.TableDiscussionRead WHERE DocumentId = @doc;
            DELETE FROM doc.TableDiscussionMessage WHERE DocumentId = @doc;
            DELETE FROM doc.TableDiscussion WHERE DocumentId = @doc;
            """;
        cmd.Parameters.AddWithValue("@doc", documentId);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(true);
    }
}
