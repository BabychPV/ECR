// tests/Ecr.Infrastructure.Tests/Persistence/PT01ProjectTemplatesMigrationTests.cs
using System.Globalization;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Persistence.Migrations;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Міграція PT01 (B74 P1 + Land-year L0, <c>docs/build/B74-L0-PT01-migration-design.md</c>): <c>doc.ProjectTemplate</c>,
/// <c>TemplateId</c> у шкалі й документі, сумісний <c>doc.fn_VersionOf</c> через <c>doc.fn_TemplateVersionOf</c>.
/// </summary>
/// <remarks>
/// ⚠ Своя база, зупинена на FE01 (остання міграція перед PT01): так виглядає майданчик до оновлення. Дані — форма живої бази:
/// проєкт зі шкалою трьох версій (Land 1.0.14 → 1.0.16), проєкт без шкали, проєкт другого шаблону (HSE).
/// Доказ F13: паритетна таблиця fn_VersionOf (до/після — 0 розбіжностей) і ідемпотентний скрипт двічі.
/// Мутації (локально): прибрати фільтр <c>t.TemplateId = @TemplateId</c> у <c>fn_TemplateVersionOf</c> — червоніє
/// паритет на шаблоні B після додавання рядка шкали A; прибрати <see cref="PT01ProjectTemplatesSql.Precheck"/> — червоніє
/// тест відмови (міграція проходить з неправдою); прибрати <see cref="PT01ProjectTemplatesSql.DownGuard"/> — червоніє тест Down.
/// </remarks>
[Collection("SqlServer")]
public sealed class PT01ProjectTemplatesMigrationTests(SqlServerFixture sql)
{
    /// <summary>Остання міграція перед PT01.</summary>
    private const string BeforeMigration = "20261112052154_FE01_FormulaError";

    private static readonly int[] PeriodKeys = [202512, 202601, 202603, 202604, 202609, 202610, 202612, 203001];

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "B74")]
    public async Task Бекфіл_1_до_1_шаблон_проєкту_шкали_і_документів_із_чинної_версії()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;
        var ids = await SeedLiveShapeAsync(cs);

        await MigrateAsync(cs);

        Assert.Equal(
            $"{ids.P1},{ids.TemplateA},{ids.A3};{ids.P2},{ids.TemplateA},{ids.A2};{ids.P3},{ids.TemplateB},{ids.B1}",
            await ScalarAsync<string>(cs, """
                SELECT STUFF((SELECT N';' + CONCAT(ProjectId, N',', TemplateId, N',', CurrentTemplateVersionId)
                              FROM doc.ProjectTemplate ORDER BY ProjectId FOR XML PATH(N'')), 1, 1, N'')
                """));

        Assert.Equal(0, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM doc.ProjectTemplateVersion WHERE TemplateId IS NULL"));
        Assert.Equal(3, await ScalarAsync<int>(cs,
            $"SELECT COUNT(*) FROM doc.ProjectTemplateVersion WHERE ProjectId = {ids.P1} AND TemplateId = {ids.TemplateA}"));
        Assert.Equal(1, await ScalarAsync<int>(cs,
            $"SELECT COUNT(*) FROM doc.ProjectTemplateVersion WHERE ProjectId = {ids.P3} AND TemplateId = {ids.TemplateB}"));

        Assert.Equal(0, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM doc.Document WHERE TemplateId IS NULL"));
        Assert.Equal(2, await ScalarAsync<int>(cs,
            $"SELECT COUNT(*) FROM doc.Document WHERE ProjectId = {ids.P1} AND TemplateId = {ids.TemplateA}"));
        Assert.Equal(1, await ScalarAsync<int>(cs,
            $"SELECT COUNT(*) FROM doc.Document WHERE ProjectId = {ids.P3} AND TemplateId = {ids.TemplateB}"));

        // Форма: TemplateId шкали NOT NULL і в PK другим стовпцем; Document.TemplateId — NULL (Q2, NOT NULL у P6).
        Assert.Equal("ProjectId,TemplateId,FromPeriodKey", await PrimaryKeyColumnsAsync(cs, "doc.ProjectTemplateVersion"));
        Assert.Equal(0, await ScalarAsync<int>(cs,
            "SELECT CAST(is_nullable AS int) FROM sys.columns WHERE object_id = OBJECT_ID(N'doc.ProjectTemplateVersion') AND name = N'TemplateId'"));
        Assert.Equal(1, await ScalarAsync<int>(cs,
            "SELECT CAST(is_nullable AS int) FROM sys.columns WHERE object_id = OBJECT_ID(N'doc.Document') AND name = N'TemplateId'"));
        Assert.Equal(1, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'cfg.MonthInputAlias')"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "B74")]
    public async Task fn_VersionOf_дає_ту_саму_версію_до_і_після_міграції_паритетна_таблиця_0_розбіжностей()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;
        var ids = await SeedLiveShapeAsync(cs);
        int[] projects = [ids.P1, ids.P2, ids.P3, ids.P1 + ids.P2 + ids.P3];   // останній - неіснуючий проєкт (NULL)

        var before = await VersionOfTableAsync(cs, projects);
        await MigrateAsync(cs);
        var after = await VersionOfTableAsync(cs, projects);

        Assert.Equal(before.Count, after.Count);
        Assert.Empty(before.Where(b => after[b.Key] != b.Value).Select(b => $"{b.Key}: {b.Value} → {after[b.Key]}"));

        // Очікувані значення, а не лише «так само»: шкала A1@202601, A2@202604, A3@202610.
        Assert.Equal(ids.A1, before[(ids.P1, 202512)]);
        Assert.Equal(ids.A2, before[(ids.P1, 202609)]);
        Assert.Equal(ids.A3, before[(ids.P1, 203001)]);
        Assert.Equal(ids.A2, before[(ids.P2, 202601)]);
        Assert.Equal(ids.B1, before[(ids.P3, 202612)]);

        // Нова функція за явним шаблоном - те саме; чужий шаблон проєкту - NULL.
        foreach (var key in PeriodKeys)
        {
            Assert.Equal(after[(ids.P1, key)], await ScalarAsync<int?>(cs, $"SELECT doc.fn_TemplateVersionOf({ids.P1}, {ids.TemplateA}, {key})"));
            Assert.Null(await ScalarAsync<int?>(cs, $"SELECT doc.fn_TemplateVersionOf({ids.P1}, {ids.TemplateB}, {key})"));
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "B74")]
    public async Task Рядок_шкали_чужого_шаблону_зупиняє_міграцію_а_після_неї_не_вставляється()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;
        var ids = await SeedLiveShapeAsync(cs);

        // До міграції: проєкт шаблону A зі шкалою, де стоїть версія шаблону B.
        await ExecuteAsync(cs, $"INSERT doc.ProjectTemplateVersion (ProjectId, FromPeriodKey, TemplateVersionId) VALUES ({ids.P2}, 202607, {ids.B1});");

        var refused = await Assert.ThrowsAnyAsync<SqlException>(() => MigrateAsync(cs));
        Assert.Equal(PT01ProjectTemplatesSql.ForeignTemplateTimelineError, refused.Number);
        Assert.Equal(0, await ScalarAsync<int>(cs, $"SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'{await Pt01IdAsync(cs)}'"));
        Assert.Equal(0, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'doc.ProjectTemplate')"));

        // Прибрали неправду - міграція проходить; далі складений FK не пускає версію чужого шаблону.
        await ExecuteAsync(cs, $"DELETE FROM doc.ProjectTemplateVersion WHERE ProjectId = {ids.P2};");
        await MigrateAsync(cs);

        var fk = await Assert.ThrowsAnyAsync<SqlException>(() => ExecuteAsync(cs,
            $"INSERT doc.ProjectTemplateVersion (ProjectId, TemplateId, FromPeriodKey, TemplateVersionId) VALUES ({ids.P2}, {ids.TemplateA}, 202607, {ids.B1});"));
        Assert.Equal(547, fk.Number);
        var pt = await Assert.ThrowsAnyAsync<SqlException>(() => ExecuteAsync(cs,
            $"INSERT doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId) VALUES ({ids.P2}, {ids.TemplateB}, {ids.A1});"));
        Assert.Equal(547, pt.Number);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "B74")]
    public async Task Down_відмовляє_проєкту_з_двома_шаблонами_а_з_одним_відкочує_і_Up_повторюється()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;
        var ids = await SeedLiveShapeAsync(cs);
        int[] projects = [ids.P1, ids.P2, ids.P3];
        var before = await VersionOfTableAsync(cs, projects);

        await MigrateAsync(cs);
        await ExecuteAsync(cs,
            $"INSERT doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId) VALUES ({ids.P1}, {ids.TemplateB}, {ids.B1});");

        var refused = await Assert.ThrowsAnyAsync<SqlException>(() => MigrateAsync(cs, BeforeMigration));
        Assert.Equal(PT01ProjectTemplatesSql.MultiTemplateDownError, refused.Number);
        Assert.Equal(1, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'doc.ProjectTemplate')"));

        await ExecuteAsync(cs, $"DELETE FROM doc.ProjectTemplate WHERE ProjectId = {ids.P1} AND TemplateId = {ids.TemplateB};");
        await MigrateAsync(cs, BeforeMigration);

        Assert.Equal(0, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'doc.ProjectTemplate')"));
        Assert.Equal(0, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.objects WHERE object_id = OBJECT_ID(N'doc.fn_TemplateVersionOf')"));
        Assert.Equal("ProjectId,FromPeriodKey", await PrimaryKeyColumnsAsync(cs, "doc.ProjectTemplateVersion"));
        Assert.Equal(before, await VersionOfTableAsync(cs, projects));   // відновлене тіло RPTV01

        await MigrateAsync(cs);
        Assert.Equal(before, await VersionOfTableAsync(cs, projects));
        Assert.Equal(3, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM doc.ProjectTemplate"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "B74")]
    public async Task Ідемпотентний_скрипт_двічі_другий_прогін_нічого_не_змінює()
    {
        await using var database = await OwnDatabaseBeforeMigrationAsync();
        var cs = database.ConnectionString;
        var ids = await SeedLiveShapeAsync(cs);
        int[] projects = [ids.P1, ids.P2, ids.P3];
        var before = await VersionOfTableAsync(cs, projects);

        await RunIdempotentScriptAsync(cs);
        var first = await FingerprintAsync(cs);

        await RunIdempotentScriptAsync(cs);
        Assert.Equal(first, await FingerprintAsync(cs));
        Assert.Equal(before, await VersionOfTableAsync(cs, projects));
        Assert.Equal(1, await ScalarAsync<int>(cs, $"SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId = N'{await Pt01IdAsync(cs)}'"));

        // Повтор бекфілу посередині збою (той самий текст ще раз напряму): 0 змін.
        await ExecuteAsync(cs, PT01ProjectTemplatesSql.Backfill);
        Assert.Equal(first, await FingerprintAsync(cs));
    }

    /// <summary>Ідентифікатори засіву у формі живої бази.</summary>
    private sealed record Ids(int TemplateA, int TemplateB, int A1, int A2, int A3, int B1, int P1, int P2, int P3);

    /// <summary>
    /// Засів на FE01: шаблон A (версії A1..A3), шаблон B (B1); P1 - A3 чинна і шкала A1@202601, A2@202604, A3@202610
    /// (2 документи); P2 - A2 без шкали; P3 - B1 зі шкалою B1@202601 (1 документ). FK вимкнено лише для довідкових
    /// стовпців (користувачі, політика) - самі зв'язки шаблон/версія/проєкт узгоджені.
    /// </summary>
    private static async Task<Ids> SeedLiveShapeAsync(string cs)
    {
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        await ExecuteAsync(cs, $$"""
            ALTER TABLE doc.Project NOCHECK CONSTRAINT ALL;
            ALTER TABLE doc.Document NOCHECK CONSTRAINT ALL;
            DECLARE @ta int, @tb int, @a1 int, @a2 int, @a3 int, @b1 int, @p1 int, @p2 int, @p3 int;
            INSERT cfg.Template (Code, NameL10n, CreatedAt, CreatedByUserId) VALUES (N'PTA{{tag}}', N'{"en":"A"}', '2026-01-01', 1);
            SET @ta = SCOPE_IDENTITY();
            INSERT cfg.Template (Code, NameL10n, CreatedAt, CreatedByUserId) VALUES (N'PTB{{tag}}', N'{"en":"B"}', '2026-01-01', 1);
            SET @tb = SCOPE_IDENTITY();
            INSERT cfg.TemplateVersion (TemplateId, Version, Status, PublishedAt, PublishedByUserId, CreatedAt, CreatedByUserId)
            VALUES (@ta, N'1.0.14', 1, '2026-01-01', 1, '2026-01-01', 1); SET @a1 = SCOPE_IDENTITY();
            INSERT cfg.TemplateVersion (TemplateId, Version, Status, PublishedAt, PublishedByUserId, CreatedAt, CreatedByUserId)
            VALUES (@ta, N'1.0.15', 1, '2026-01-01', 1, '2026-01-01', 1); SET @a2 = SCOPE_IDENTITY();
            INSERT cfg.TemplateVersion (TemplateId, Version, Status, PublishedAt, PublishedByUserId, CreatedAt, CreatedByUserId)
            VALUES (@ta, N'1.0.16', 1, '2026-01-01', 1, '2026-01-01', 1); SET @a3 = SCOPE_IDENTITY();
            INSERT cfg.TemplateVersion (TemplateId, Version, Status, PublishedAt, PublishedByUserId, CreatedAt, CreatedByUserId)
            VALUES (@tb, N'1.0.0', 1, '2026-01-01', 1, '2026-01-01', 1); SET @b1 = SCOPE_IDENTITY();

            INSERT doc.Project (Code, NameL10n, PeriodStart, PeriodEnd, PeriodKind, PeriodPolicyId, Status, TemplateVersionId, TimeZoneId)
            VALUES (N'P1{{tag}}', N'{"en":"P1"}', '2026-01-01', '2026-12-31', 0, 1, 1, @a3, N'Asia/Atyrau'); SET @p1 = SCOPE_IDENTITY();
            INSERT doc.Project (Code, NameL10n, PeriodStart, PeriodEnd, PeriodKind, PeriodPolicyId, Status, TemplateVersionId, TimeZoneId)
            VALUES (N'P2{{tag}}', N'{"en":"P2"}', '2026-01-01', '2026-12-31', 0, 1, 1, @a2, N'Asia/Atyrau'); SET @p2 = SCOPE_IDENTITY();
            INSERT doc.Project (Code, NameL10n, PeriodStart, PeriodEnd, PeriodKind, PeriodPolicyId, Status, TemplateVersionId, TimeZoneId)
            VALUES (N'P3{{tag}}', N'{"en":"P3"}', '2026-01-01', '2026-12-31', 2, 1, 1, @b1, N'Asia/Atyrau'); SET @p3 = SCOPE_IDENTITY();

            INSERT doc.ProjectTemplateVersion (ProjectId, FromPeriodKey, TemplateVersionId)
            VALUES (@p1, 202601, @a1), (@p1, 202604, @a2), (@p1, 202610, @a3), (@p3, 202601, @b1);

            INSERT doc.Document (ProjectId, BusinessKey, CreatedAt, CreatedByUserId, ModifiedAt, ModifiedByUserId)
            VALUES (@p1, N'PT01-1', '2026-01-01', 1, '2026-01-01', 1), (@p1, N'PT01-2', '2026-01-01', 1, '2026-01-01', 1),
                   (@p3, N'PT01-3', '2026-01-01', 1, '2026-01-01', 1);
            """);

        async Task<int> Id(string query) => await ScalarAsync<int>(cs, query);
        var ta = await Id($"SELECT Id FROM cfg.Template WHERE Code = N'PTA{tag}'");
        var tb = await Id($"SELECT Id FROM cfg.Template WHERE Code = N'PTB{tag}'");
        return new Ids(
            ta, tb,
            await Id($"SELECT Id FROM cfg.TemplateVersion WHERE TemplateId = {ta} AND Version = N'1.0.14'"),
            await Id($"SELECT Id FROM cfg.TemplateVersion WHERE TemplateId = {ta} AND Version = N'1.0.15'"),
            await Id($"SELECT Id FROM cfg.TemplateVersion WHERE TemplateId = {ta} AND Version = N'1.0.16'"),
            await Id($"SELECT Id FROM cfg.TemplateVersion WHERE TemplateId = {tb}"),
            await Id($"SELECT Id FROM doc.Project WHERE Code = N'P1{tag}'"),
            await Id($"SELECT Id FROM doc.Project WHERE Code = N'P2{tag}'"),
            await Id($"SELECT Id FROM doc.Project WHERE Code = N'P3{tag}'"));
    }

    /// <summary>Паритетна таблиця: <c>doc.fn_VersionOf</c> на кожну пару (проєкт, період).</summary>
    private static async Task<Dictionary<(int Project, int Period), int?>> VersionOfTableAsync(string cs, int[] projects)
    {
        var result = new Dictionary<(int, int), int?>();
        foreach (var project in projects)
        {
            foreach (var key in PeriodKeys)
            {
                result[(project, key)] = await ScalarAsync<int?>(cs, $"SELECT doc.fn_VersionOf({project}, {key})");
            }
        }

        return result;
    }

    /// <summary>Відбиток стану, який міграція змінює (для «другий прогін - 0 змін»).</summary>
    private static Task<string> FingerprintAsync(string cs) => ScalarAsync<string>(cs, """
        SELECT CONCAT(
            (SELECT COUNT(*) FROM doc.ProjectTemplate), N'|',
            (SELECT CHECKSUM_AGG(CHECKSUM(ProjectId, TemplateId, CurrentTemplateVersionId)) FROM doc.ProjectTemplate), N'|',
            (SELECT CHECKSUM_AGG(CHECKSUM(ProjectId, TemplateId, FromPeriodKey, TemplateVersionId)) FROM doc.ProjectTemplateVersion), N'|',
            (SELECT CHECKSUM_AGG(CHECKSUM(Id, TemplateId)) FROM doc.Document), N'|',
            (SELECT CHECKSUM_AGG(CHECKSUM(RowVersion)) FROM doc.Document), N'|',
            (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory))
        """);

    private static async Task<string> PrimaryKeyColumnsAsync(string cs, string table)
        => await ScalarAsync<string>(cs, $"""
            SELECT STUFF((SELECT N',' + c.name
                FROM sys.indexes AS i
                JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID(N'{table}') AND i.is_primary_key = 1
                ORDER BY ic.key_ordinal FOR XML PATH(N'')), 1, 1, N'')
            """);

    /// <summary>Ідентифікатор PT01 з переліку міграцій збірки (червоно, доки міграцію не згенеровано).</summary>
    private static async Task<string> Pt01IdAsync(string cs)
    {
        await using var db = CreateContext(cs);
        return db.Database.GetMigrations().Single(m => m.EndsWith("_PT01_ProjectTemplates", StringComparison.Ordinal));
    }

    /// <summary>Своя база (з видаленням у кінці), зупинена на FE01.</summary>
    private async Task<OwnDatabase> OwnDatabaseBeforeMigrationAsync()
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + "_PT01" + Guid.NewGuid().ToString("N")[..6];

        await ExecuteAsync(
            new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = "master" }.ConnectionString,
            $"""
            IF DB_ID(N'{name}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{name}];
            END
            CREATE DATABASE [{name}]{TestDatabaseFiles.Clause(name)} COLLATE Latin1_General_100_CI_AS_SC;
            """);

        builder.InitialCatalog = name;
        var database = new OwnDatabase(builder.ConnectionString, name, sql.ConnectionString);
        await MigrateAsync(database.ConnectionString, BeforeMigration);

        // Червоно, доки PT01 не згенеровано: тест має сенс лише як «FE01 → PT01».
        Assert.NotNull(await Pt01IdAsync(database.ConnectionString));
        return database;
    }

    /// <summary>Своя база тесту; <c>DisposeAsync</c> її знищує.</summary>
    private sealed class OwnDatabase(string connectionString, string name, string serverConnectionString) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await ExecuteAsync(
                new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = "master" }.ConnectionString,
                $"""
                IF DB_ID(N'{name}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{name}];
                END
                """);
        }
    }

    /// <summary>Той самий <c>migration.sql</c> (<c>--idempotent</c>), що в deploy, пакетами по <c>GO</c>.</summary>
    private static async Task RunIdempotentScriptAsync(string connectionString)
    {
        string script;
        await using (var db = CreateContext(connectionString))
        {
            script = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var batch in SqlBatches.Split(script))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = 300;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task MigrateAsync(string connectionString, string? target = null)
    {
        await using var db = CreateContext(connectionString);
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static EcrDbContext CreateContext(string connectionString)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    private static async Task ExecuteAsync(string connectionString, string text)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string query)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        command.CommandTimeout = 300;
        var result = await command.ExecuteScalarAsync();

        if (result is null or DBNull)
        {
            return default!;
        }

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(result, target, CultureInfo.InvariantCulture);
    }
}
