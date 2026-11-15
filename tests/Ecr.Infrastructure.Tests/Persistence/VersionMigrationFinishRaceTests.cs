using System.Data;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// B130: гонка двох одночасних переносів РІЗНИХ проєктів на кроці 6a/6b <c>FinishSql</c>
/// (<see cref="DocumentVersionMigrationStore.GrantCopyInsertSql"/>, <see cref="DocumentVersionMigrationStore.ColumnRenameInsertSql"/>).
/// Переноси різних проєктів не серіалізуються, а гранти ролей і пари перейменувань спільні для шаблону.
/// </summary>
/// <remarks>
/// <para>
/// Гонка відтворюється детерміновано, без сну: транзакція A виконує той самий SQL (текст із сховища) і НЕ комітить;
/// транзакція B виконує його ж на другому з'єднанні — тест чекає, доки B заблокується на A
/// (<c>sys.dm_exec_requests.blocking_session_id</c>), і комітить A. Без замка B під RCSI не бачить незакоміченого рядка
/// в <c>NOT EXISTS</c>, вставляє й після коміту A падає на унікальному ключі (2601/2627) — увесь перенос відкотився б.
/// З <c>UPDLOCK, HOLDLOCK</c> B чекає на читанні, бачить рядок A і не вставляє нічого.
/// </para>
/// <para>
/// Мутації: прибрати <c>WITH (UPDLOCK, HOLDLOCK)</c> з <c>GrantCopyInsertSql</c> — червоніє «6a»; з
/// <c>ColumnRenameInsertSql</c> — «6b». 20 повторів на тест.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class VersionMigrationFinishRaceTests(SqlServerFixture sql)
{
    private const int Rounds = 20;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B130")]
    public async Task B130_6a_одночасна_копія_того_самого_гранта_не_падає_на_UQ_ResourceGrant()
    {
        await RequireRcsiAsync();
        var (roleId, oldId) = await ArrangeRoleAsync();

        for (var round = 0; round < Rounds; round++)
        {
            var newId = oldId + 1 + round;
            var batch = $"""
                DECLARE @grantMap TABLE (k tinyint NOT NULL, o int NOT NULL, n int NOT NULL, PRIMARY KEY (k, o));
                INSERT @grantMap (k, o, n) VALUES ({(byte)ResourceKind.Table}, {oldId}, {newId});
                {DocumentVersionMigrationStore.GrantCopyInsertSql}
                """;

            await RaceAsync(batch, parameters: null);

            // Грант рівно один: A вставила, B (дочекавшись коміту A) — ні, і не впала на UQ_ResourceGrant.
            Assert.Equal(1, await CountAsync(
                $"SELECT COUNT(*) FROM sec.ResourceGrant WHERE RoleId = {roleId} AND ResourceKind = {(byte)ResourceKind.Table} AND ResourceId = {newId}"));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B130")]
    public async Task B130_6b_одночасна_вставка_тієї_самої_пари_не_падає_на_PK_ColumnRename()
    {
        await RequireRcsiAsync();

        // Колонки без FK (cfg.ColumnRename навмисно без ключів): унікальний діапазон Id на прогін.
        var baseId = 2_000_000_000 + Random.Shared.Next(0, 100_000_000);
        for (var round = 0; round < Rounds; round++)
        {
            var from = baseId + (2 * round);
            var to = from + 1;
            var renames = "[{\"f\":" + from + ",\"t\":" + to + "}]";

            await RaceAsync(
                DocumentVersionMigrationStore.ColumnRenameInsertSql,
                () => new SqlParameter("@renames", SqlDbType.NVarChar, -1) { Value = renames });

            // Пара рівно одна: A вставила, B (дочекавшись коміту A) — ні, і не впала на PK_ColumnRename.
            Assert.Equal(1, await CountAsync(
                $"SELECT COUNT(*) FROM cfg.ColumnRename WHERE FromColumnDefId = {from} AND ToColumnDefId = {to}"));
        }
    }

    /// <summary>
    /// A виконує пакет у транзакції без коміту; B — той самий пакет, доки не заблокується на A; коміт A; коміт B.
    /// Виняток B (порушення унікального ключа) — провал тесту.
    /// </summary>
    private async Task RaceAsync(string batch, Func<SqlParameter>? parameters)
    {
        await using var a = new SqlConnection(sql.ConnectionString);
        await using var b = new SqlConnection(sql.ConnectionString);
        await a.OpenAsync();
        await b.OpenAsync();
        var spidA = await SpidAsync(a);
        var spidB = await SpidAsync(b);

        await using var txA = (SqlTransaction)await a.BeginTransactionAsync();
        await ExecuteAsync(a, txA, batch, parameters);

        await using var txB = (SqlTransaction)await b.BeginTransactionAsync();
        var second = ExecuteAsync(b, txB, batch, parameters);

        // Бар'єр: B справді чекає на замку A (інакше гонку не відтворено — тест не стане мовчки зеленим).
        var blocked = false;
        await using (var monitor = new SqlConnection(sql.ConnectionString))
        {
            await monitor.OpenAsync();
            for (var i = 0; i < 200 && !blocked && !second.IsCompleted; i++)
            {
                await using var probe = new SqlCommand(
                    "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id = @b AND blocking_session_id = @a", monitor);
                probe.Parameters.AddWithValue("@a", spidA);
                probe.Parameters.AddWithValue("@b", spidB);
                blocked = (int)(await probe.ExecuteScalarAsync())! > 0;
                if (!blocked)
                {
                    await Task.Delay(25);
                }
            }
        }

        Assert.True(blocked, "Друге з'єднання не заблокувалося на першому — гонку не відтворено.");

        await txA.CommitAsync();
        await second;
        await txB.CommitAsync();
    }

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction transaction, string batch, Func<SqlParameter>? parameters)
    {
        await using var command = new SqlCommand(batch, connection, transaction) { CommandTimeout = 30 };
        if (parameters is not null)
        {
            command.Parameters.Add(parameters());
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<short> SpidAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("SELECT CAST(@@SPID AS smallint)", connection);
        return (short)(await command.ExecuteScalarAsync())!;
    }

    private async Task<int> CountAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(query, connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Гонка існує лише під RCSI (як у бойовій базі, <c>06-rcsi.sql</c>): без нього NOT EXISTS і так чекає.</summary>
    private async Task RequireRcsiAsync()
        => Assert.Equal(1, await CountAsync("SELECT CAST(is_read_committed_snapshot_on AS int) FROM sys.databases WHERE name = DB_NAME()"));

    private async Task<(int RoleId, int OldId)> ArrangeRoleAsync()
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);
        var role = new Role(
            EcrCode.Create($"B130{Guid.NewGuid():N}"[..16]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "B130 race (test)" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        // Старий Id ресурсу (таблиця версії-джерела) і діапазон нових — без FK на ресурс, унікальні для ролі.
        var oldId = 1_900_000_000 + (role.Id % 1_000_000 * 100);
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Table, oldId, GrantLevel.Read, isDeny: true));
        await db.SaveChangesAsync();
        return (role.Id, oldId);
    }
}
