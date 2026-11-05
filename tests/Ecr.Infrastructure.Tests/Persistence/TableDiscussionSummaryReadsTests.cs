using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
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
/// AN-155a: підсумок чату рахує вказівник прочитаного РАЗ НА ТАБЛИЦЮ (OUTER APPLY), а не для кожного повідомлення.
/// Корисність: опитування підсумку (ETag будується з результату) на 82 x 200 повідомлень коштувало 33 354 читання.
/// </summary>
/// <remarks>
/// Мутаційний доказ: повернути <c>let lastRead = ... FirstOrDefault()</c> у предикат <c>Count</c> у
/// <c>TableDiscussionStore.SummaryAsync</c> → червоніє <c>Читання_підсумку_не_залежать_від_числа_повідомлень</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class TableDiscussionSummaryReadsTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private const int Tables = 40;
    private const int PerTable = 200;

    // Виміряно ~8 читань на таблицю (≈350 разом); старий корельований підзапит давав >16 000 при вказівниках.
    private const int ReadsCeiling = 1500;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читання_підсумку_не_залежать_від_числа_повідомлень_а_значення_збігаються_з_незалежним_підрахунком()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(true);
        int me;
        int other;
        await using (var setup = builder.CreateContext())
        {
            me = await NewUserAsync(setup, "tcs_me").ConfigureAwait(true);
            other = await NewUserAsync(setup, "tcs_oth").ConfigureAwait(true);
        }

        try
        {
            await SeedAsync(document.DocumentId, me, other).ConfigureAwait(true);
            var messages = await LoadMessagesAsync(document.DocumentId).ConfigureAwait(true);
            var codes = Enumerable.Range(1, Tables).Select(i => $"T{i:D3}").ToList();

            // Вказівники: me — для i%3==0 немає, ==1 середина, ==2 останнє; other — навпаки зсунуто.
            var pointers = new Dictionary<(int User, string Code), long>();
            await using (var setup = builder.CreateContext())
            {
                for (var i = 0; i < codes.Count; i++)
                {
                    var ids = messages.Where(m => m.Code == codes[i]).Select(m => m.Id).Order().ToList();
                    AddPointer(setup, pointers, me, document.DocumentId, codes[i], i % 3, ids);
                    AddPointer(setup, pointers, other, document.DocumentId, codes[i], (i + 1) % 3, ids);
                }

                await setup.SaveChangesAsync().ConfigureAwait(true);
            }

            await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
            var store = new TableDiscussionStore(db);
            var none = new Dictionary<string, IReadOnlyCollection<int>>();
            await db.Database.OpenConnectionAsync().ConfigureAwait(true);

            foreach (var user in new[] { me, other })
            {
                await store.SummaryAsync(document.DocumentId, codes, none, user, CancellationToken.None).ConfigureAwait(true); // прогрів плану
                var best = long.MaxValue;
                IReadOnlyList<TableDiscussionSummaryRow> rows = [];
                for (var run = 0; run < 2; run++)
                {
                    var before = await SessionReadsAsync(db).ConfigureAwait(true);
                    rows = await store.SummaryAsync(document.DocumentId, codes, none, user, CancellationToken.None).ConfigureAwait(true);
                    best = Math.Min(best, await SessionReadsAsync(db).ConfigureAwait(true) - before);
                }

                output.WriteLine($"user={(user == me ? "me" : "other")}: tables={Tables} messages={Tables * PerTable} logical reads={best}");
                Assert.True(best < ReadsCeiling, $"підсумок: {best} логічних читань на {Tables * PerTable} повідомлень");

                // Незалежний підрахунок у C# з прочитаних повідомлень і вказівників.
                Assert.Equal(Tables, rows.Count);
                foreach (var code in codes)
                {
                    var mine = messages.Where(m => m.Code == code && !m.Deleted).ToList();
                    var ptr = pointers.TryGetValue((user, code), out var p) ? p : 0L;
                    var row = Assert.Single(rows, r => r.TableCode == code);
                    Assert.Equal(mine.Count, row.Total);
                    Assert.Equal(mine.Count(m => m.Id > ptr && m.Author != user), row.Unread);
                    Assert.Equal(mine.Max(m => m.Id), row.LastMessageId);
                }

                // Є і нульові, і ненульові значення: тест не вироджений.
                Assert.Contains(rows, r => r.Unread == 0);
                Assert.Contains(rows, r => r.Unread > 0);
            }
        }
        finally
        {
            await CleanupAsync(document.DocumentId).ConfigureAwait(true);
        }
    }

    private static void AddPointer(
        EcrDbContext db, Dictionary<(int, string), long> pointers, int user, long documentId, string code, int mode, List<long> ids)
    {
        if (mode == 0)
        {
            return; // немає запису про прочитане
        }

        var value = mode == 1 ? ids[ids.Count / 2] : ids[^1];
        pointers[(user, code)] = value;
        db.TableDiscussionReads.Add(new TableDiscussionRead(user, documentId, code, value));
    }

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

    private async Task<List<(long Id, string Code, int Author, bool Deleted)>> LoadMessagesAsync(long documentId)
    {
        var list = new List<(long, string, int, bool)>();
        await using var conn = new SqlConnection(sql.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(true);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, TableDefCode, AuthorUserId, CASE WHEN DeletedAt IS NULL THEN 0 ELSE 1 END FROM doc.TableDiscussionMessage WHERE DocumentId = @doc";
        cmd.Parameters.AddWithValue("@doc", documentId);
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(true);
        while (await reader.ReadAsync().ConfigureAwait(true))
        {
            list.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3) == 1));
        }

        return list;
    }

    private async Task SeedAsync(long documentId, int me, int other)
    {
        await using var conn = new SqlConnection(sql.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(true);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 120;
        // Кожне 7-ме повідомлення видалене (автором me); решта чергує авторів.
        cmd.CommandText = $"""
            ;WITH n AS (SELECT TOP ({PerTable}) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b),
                  t AS (SELECT TOP ({Tables}) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO doc.TableDiscussionMessage (DocumentId, TableDefCode, SheetCopyKey, PeriodKey, AuthorUserId, Body, CreatedAt, DeletedAt, DeletedByUserId)
            SELECT @doc, CONCAT('T', RIGHT(CONCAT('000', t.i), 3)), 0, 202601 + (n.i % 12),
                   CASE WHEN n.i % 2 = 0 THEN @me ELSE @other END,
                   N'текст', DATEADD(minute, n.i, '2026-01-01'),
                   CASE WHEN n.i % 7 = 0 THEN SYSUTCDATETIME() END, CASE WHEN n.i % 7 = 0 THEN @me END
            FROM t CROSS JOIN n;

            INSERT INTO doc.TableDiscussion (DocumentId, TableDefCode, SheetCopyKey, Revision, MessageCount, LastMessageId, LastMessageAt)
            SELECT DocumentId, TableDefCode, SheetCopyKey, COUNT(*), COUNT(CASE WHEN DeletedAt IS NULL THEN 1 END),
                   MAX(CASE WHEN DeletedAt IS NULL THEN Id END), MAX(CASE WHEN DeletedAt IS NULL THEN CreatedAt END)
            FROM doc.TableDiscussionMessage WHERE DocumentId = @doc GROUP BY DocumentId, TableDefCode, SheetCopyKey;
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
