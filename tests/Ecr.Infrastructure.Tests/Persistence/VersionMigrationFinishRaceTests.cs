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
/// B130: одночасні переноси РІЗНИХ проєктів на кроці 6 <c>FinishSql</c>: гранти ролей (6a,
/// <see cref="DocumentVersionMigrationStore.GrantCopyInsertSql"/>) і пари перейменувань (6b,
/// <see cref="DocumentVersionMigrationStore.ColumnRenameInsertSql"/>) спільні для шаблону, а переноси різних проєктів
/// серіалізує лише applock шаблону цільової версії (<see cref="DocumentVersionMigrationStore.TemplateFinishLockSql"/>).
/// </summary>
/// <remarks>
/// <para>
/// Гонка хінтів відтворюється детерміновано, без сну. Транзакція A — «інший перенос, що вже перевірив NOT EXISTS і
/// збирається вставити»: <c>SELECT … WITH (UPDLOCK, HOLDLOCK)</c> по ВІДСУТНЬОМУ ключу, без вставки. Транзакція B
/// виконує SQL кроку зі сховища на другому з'єднанні; тест чекає, доки B заблокується на A
/// (<c>sys.dm_exec_requests.blocking_session_id</c>), далі A вставляє рядок і комітить. Лише з обома хінтами B чекає
/// вже на читанні, бачить рядок A і не вставляє: без <c>HOLDLOCK</c> чи без <c>UPDLOCK</c> B вирішує «немає» до
/// коміту A і падає на унікальному ключі (2601/2627) або в дедлоку 1205 з A.
/// </para>
/// <para>
/// Мутації: лише <c>HOLDLOCK</c>, лише <c>UPDLOCK</c> чи без хінтів у 6a/6b — червоніють «6a»/«6b»; без <c>DISTINCT</c>
/// у 6b — «повтор пари»; без applock на початку <c>FinishSql</c> чи з порожнім
/// <see cref="DocumentVersionMigrationStore.TemplateFinishLockSql"/> — «applock». 20 повторів на тест гонки.
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
    public async Task B130_6a_грант_іншого_переносу_між_NOT_EXISTS_і_INSERT_не_дає_дубля_чи_дедлоку()
    {
        await RequireRcsiAsync();
        var (roleId, oldId) = await ArrangeRoleAsync();
        var kind = (byte)ResourceKind.Table;

        for (var round = 0; round < Rounds; round++)
        {
            var newId = oldId + 1 + round;
            var key = $"RoleId = {roleId} AND ResourceKind = {kind} AND ResourceId = {newId}";
            var batch = $"""
                DECLARE @grantMap TABLE (k tinyint NOT NULL, o int NOT NULL, n int NOT NULL, PRIMARY KEY (k, o));
                INSERT @grantMap (k, o, n) VALUES ({kind}, {oldId}, {newId});
                {DocumentVersionMigrationStore.GrantCopyInsertSql}
                """;

            await RaceAsync(
                probe: $"SELECT 1 FROM sec.ResourceGrant WITH (UPDLOCK, HOLDLOCK) WHERE {key};",
                insert: $"INSERT sec.ResourceGrant (RoleId, ResourceKind, ResourceId, [Level], IsDeny) VALUES ({roleId}, {kind}, {newId}, 1, 1);",
                batch,
                parameters: null);

            // Грант рівно один: A вставила, B (дочекавшись коміту A) — ні, і не впала на UQ_ResourceGrant.
            Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM sec.ResourceGrant WHERE {key}"));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B130")]
    public async Task B130_6b_пара_іншого_переносу_між_NOT_EXISTS_і_INSERT_не_дає_дубля_чи_дедлоку()
    {
        await RequireRcsiAsync();

        // Колонки без FK (cfg.ColumnRename навмисно без ключів): унікальний діапазон Id на прогін.
        var baseId = 2_000_000_000 + Random.Shared.Next(0, 100_000_000);
        for (var round = 0; round < Rounds; round++)
        {
            var from = baseId + (2 * round);
            var to = from + 1;
            var key = $"FromColumnDefId = {from} AND ToColumnDefId = {to}";
            var renames = "[{\"f\":" + from + ",\"t\":" + to + "}]";

            await RaceAsync(
                probe: $"SELECT 1 FROM cfg.ColumnRename WITH (UPDLOCK, HOLDLOCK) WHERE {key};",
                insert: $"INSERT cfg.ColumnRename (FromColumnDefId, ToColumnDefId) VALUES ({from}, {to});",
                DocumentVersionMigrationStore.ColumnRenameInsertSql,
                () => new SqlParameter("@renames", SqlDbType.NVarChar, -1) { Value = renames });

            // Пара рівно одна: A вставила, B (дочекавшись коміту A) — ні, і не впала на PK_ColumnRename.
            Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM cfg.ColumnRename WHERE {key}"));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B130")]
    public async Task B130_6b_повтор_пари_в_мапі_вставляє_один_рядок()
    {
        var from = 2_000_000_000 + Random.Shared.Next(100_000_000, 140_000_000);
        var renames = "[{\"f\":" + from + ",\"t\":" + (from + 1) + "},{\"f\":" + from + ",\"t\":" + (from + 1) + "}]";

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(DocumentVersionMigrationStore.ColumnRenameInsertSql, connection);
        command.Parameters.Add(new SqlParameter("@renames", SqlDbType.NVarChar, -1) { Value = renames });
        await command.ExecuteNonQueryAsync();

        Assert.Equal(1, await CountAsync(
            $"SELECT COUNT(*) FROM cfg.ColumnRename WHERE FromColumnDefId = {from} AND ToColumnDefId = {from + 1}"));
    }

    /// <summary>
    /// Applock шаблону цільової версії: другий перенос того самого шаблону чекає коміту першого, іншого шаблону — ні;
    /// і саме ним <c>FinishSql</c> починається (до 6a/6b).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B130")]
    public async Task B130_applock_шаблону_серіалізує_крок_6_переносів_одного_шаблону()
    {
        Assert.False(string.IsNullOrWhiteSpace(DocumentVersionMigrationStore.TemplateFinishLockSql));
        Assert.StartsWith(
            DocumentVersionMigrationStore.TemplateFinishLockSql.Trim(),
            DocumentVersionMigrationStore.FinishSql.TrimStart(),
            StringComparison.Ordinal);

        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var same = await builder.BuildAsync(209912, columnCount: 1, rowCount: 1);
        var other = await builder.BuildAsync(209912, columnCount: 1, rowCount: 1);

        await using var a = new SqlConnection(sql.ConnectionString);
        await using var b = new SqlConnection(sql.ConnectionString);
        await using var c = new SqlConnection(sql.ConnectionString);
        await a.OpenAsync();
        await b.OpenAsync();
        await c.OpenAsync();
        var spidA = await SpidAsync(a);
        var spidB = await SpidAsync(b);

        await using var txA = (SqlTransaction)await a.BeginTransactionAsync();
        await ExecuteAsync(a, txA, DocumentVersionMigrationStore.TemplateFinishLockSql, Target(same.TemplateVersionId));

        // Інший шаблон — не чекає.
        await using (var txC = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await ExecuteAsync(c, txC, DocumentVersionMigrationStore.TemplateFinishLockSql, Target(other.TemplateVersionId));
            await txC.CommitAsync();
        }

        await using var txB = (SqlTransaction)await b.BeginTransactionAsync();
        var second = ExecuteAsync(b, txB, DocumentVersionMigrationStore.TemplateFinishLockSql, Target(same.TemplateVersionId));
        Assert.True(await BlockedAsync(spidA, spidB, second), "Другий перенос того самого шаблону не чекав applock першого.");

        await txA.CommitAsync();
        await second;
        await txB.CommitAsync();

        static Func<SqlParameter> Target(int versionId)
            => () => new SqlParameter("@targetVersionId", SqlDbType.Int) { Value = versionId };
    }

    /// <summary>
    /// A: читання з <c>UPDLOCK, HOLDLOCK</c> по відсутньому ключу (без вставки); B: SQL кроку — доки не заблокується на A;
    /// A вставляє рядок і комітить; B має завершитися без винятку.
    /// </summary>
    private async Task RaceAsync(string probe, string insert, string batch, Func<SqlParameter>? parameters)
    {
        await using var a = new SqlConnection(sql.ConnectionString);
        await using var b = new SqlConnection(sql.ConnectionString);
        await a.OpenAsync();
        await b.OpenAsync();
        var spidA = await SpidAsync(a);
        var spidB = await SpidAsync(b);

        await using var txA = (SqlTransaction)await a.BeginTransactionAsync();
        await ExecuteAsync(a, txA, probe, parameters: null);

        await using var txB = (SqlTransaction)await b.BeginTransactionAsync();
        var second = ExecuteAsync(b, txB, batch, parameters);
        Assert.True(await BlockedAsync(spidA, spidB, second), "Друге з'єднання не заблокувалося на першому — гонку не відтворено.");

        await ExecuteAsync(a, txA, insert, parameters: null);
        await txA.CommitAsync();
        await second;
        await txB.CommitAsync();
    }

    /// <summary>Чекає, доки сесія B заблокується на A (до ~5 с); <c>false</c> — B не заблокувалась (або вже завершилась).</summary>
    private async Task<bool> BlockedAsync(short spidA, short spidB, Task second)
    {
        await using var monitor = new SqlConnection(sql.ConnectionString);
        await monitor.OpenAsync();
        for (var i = 0; i < 200 && !second.IsCompleted; i++)
        {
            await using var probe = new SqlCommand(
                "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id = @b AND blocking_session_id = @a", monitor);
            probe.Parameters.AddWithValue("@a", spidA);
            probe.Parameters.AddWithValue("@b", spidB);
            if ((int)(await probe.ExecuteScalarAsync())! > 0)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
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

    /// <summary>Гонка під RCSI — як у бойовій базі (<c>06-rcsi.sql</c>).</summary>
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
