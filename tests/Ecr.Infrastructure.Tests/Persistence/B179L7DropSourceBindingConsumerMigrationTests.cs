// tests/Ecr.Infrastructure.Tests/Persistence/B179L7DropSourceBindingConsumerMigrationTests.cs
using System.Globalization;
using System.Text.Json;
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
/// Міграція <c>B179L7DropSourceBindingConsumer</c> (B179 L7, D-574): оновлення бази зі схемою live (остання міграція
/// перед L7) і рядками в <c>cfg.DocumentSourceBindingConsumer</c> — рядки з кодами переносяться одним записом у
/// <c>aud.SecurityEvent</c>, таблиця з FK та індексом зникає, модель EF збігається з оновленою схемою; посилання бази
/// на таблицю зупиняє зняття <c>THROW 50574</c> до будь-якої зміни; Down відновлює таблицю і рядки з архіву.
/// </summary>
/// <remarks>
/// Власна база, зупинена на міграції перед L7 (як <see cref="D440DropFourEyesMigrationTests"/>); <c>aud.SecurityEvent</c>
/// і його тригер незмінності беруться з <c>11-audit-tables.sql</c> (без партиціювання — міграція лише вставляє).
/// <para>
/// Мутації (локально): прибрати INSERT архіву з Up — червоний перший тест (архів порожній); прибрати перевірку
/// <c>sys.sql_expression_dependencies</c> — подання без SCHEMABINDING зняттю не заважає, міграція проходить, другий тест
/// червоний (немає 50574, таблиці немає); прибрати відновлення рядків із Down — червоний третій (пар немає).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class B179L7DropSourceBindingConsumerMigrationTests(SqlServerFixture sql)
{
    /// <summary>Остання міграція перед L7 — схема live (W15y, a9a3c0067).</summary>
    private const string BeforeMigration = "20261122063958_PERF564FairQueueAndRunWatermark";

    private const int ThrowNumber = 50574;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-574")]
    public async Task Up_архівує_рядки_з_кодами_і_знімає_таблицю_з_FK_та_індексом_модель_збігається()
    {
        var cs = await CreateDatabaseBeforeMigrationAsync("_L7A");
        var (bindingId, landId, otherId) = await ArrangeConsumersAsync(cs);
        Assert.Equal(2, await ScalarAsync(cs, "SELECT COUNT(*) FROM cfg.DocumentSourceBindingConsumer"));

        await MigrateAsync(cs, null);

        Assert.Equal(0, await ScalarAsync(cs, "SELECT COUNT(*) FROM sys.objects WHERE object_id = OBJECT_ID(N'cfg.DocumentSourceBindingConsumer')"));
        Assert.Equal(0, await ScalarAsync(cs, "SELECT COUNT(*) FROM sys.foreign_keys WHERE name LIKE N'FK[_]DocSrcBindingConsumer[_]%'"));
        Assert.Equal(0, await ScalarAsync(cs, "SELECT COUNT(*) FROM sys.indexes WHERE name IN (N'IX_DocSrcBindingConsumer_Template', N'PK_DocumentSourceBindingConsumer')"));

        // Залежна зв'язка ціла, її FK лишились.
        Assert.Equal(1, await ScalarAsync(cs, $"SELECT COUNT(*) FROM cfg.DocumentSourceBinding WHERE Id = {Num(bindingId)}"));
        Assert.Equal(3, await ScalarAsync(cs, "SELECT COUNT(*) FROM sys.foreign_keys WHERE name LIKE N'FK[_]DocSrcBinding[_]%'"));

        // Архів: рівно один запис системи з усіма рядками і кодами.
        var details = await ArchiveAsync(cs);
        var single = Assert.Single(details);
        using var json = JsonDocument.Parse(single);
        Assert.Equal("D-574", json.RootElement.GetProperty("decision").GetString());
        var rows = json.RootElement.GetProperty("consumers").EnumerateArray()
            .Select(r => $"{r.GetProperty("bindingId").GetInt32()}:{r.GetProperty("bindingCode").GetString()}:"
                         + $"{r.GetProperty("templateId").GetInt32()}:{r.GetProperty("templateCode").GetString()}")
            .ToList();
        Assert.Equal([$"{bindingId}:L7_BINDING:{landId}:L7_LAND", $"{bindingId}:L7_BINDING:{otherId}:L7_OTHER"], rows);
        Assert.Equal(0, await ScalarAsync(
            cs, $"SELECT MAX(ChangedByUserId) FROM aud.SecurityEvent WHERE EventType = N'{B179L7DropSourceBindingConsumer.RetiredEventType}'"));

        // Модель застосунку бачить оновлену схему як свою: нових змін немає, зв'язка читається.
        await using var db = Context(cs);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal("L7_BINDING", (await db.DocumentSourceBindings.AsNoTracking().SingleAsync(b => b.Id == bindingId)).Code);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-574")]
    public async Task Подання_що_читає_таблицю_зупиняє_зняття_THROW_50574_без_змін()
    {
        var cs = await CreateDatabaseBeforeMigrationAsync("_L7B");
        await ArrangeConsumersAsync(cs);
        await ExecuteAsync(cs, "CREATE VIEW dbo.v_L7Probe AS SELECT BindingId, TemplateId FROM cfg.DocumentSourceBindingConsumer;");

        var error = await Record.ExceptionAsync(() => MigrateAsync(cs, null));

        var sqlError = FindSqlException(error);
        Assert.True(
            sqlError is { Number: ThrowNumber },
            $"очікувалась передперевірка {ThrowNumber}, а прийшло: "
            + (sqlError is null ? error?.ToString() ?? "жодної помилки" : $"SQL {sqlError.Number}: {sqlError.Message}"));

        // Нічого не знято й не записано: таблиця з рядками, архіву немає, міграція не в історії.
        Assert.Equal(2, await ScalarAsync(cs, "SELECT COUNT(*) FROM cfg.DocumentSourceBindingConsumer"));
        Assert.Empty(await ArchiveAsync(cs));
        Assert.Equal(0, await ScalarAsync(
            cs, "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE N'%[_]B179L7DropSourceBindingConsumer'"));

        // Посилання прибрано — зняття проходить.
        await ExecuteAsync(cs, "DROP VIEW dbo.v_L7Probe;");
        await MigrateAsync(cs, null);
        Assert.Equal(0, await ScalarAsync(cs, "SELECT COUNT(*) FROM sys.objects WHERE object_id = OBJECT_ID(N'cfg.DocumentSourceBindingConsumer')"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-574")]
    public async Task Down_відновлює_таблицю_й_рядки_з_архіву_порожня_таблиця_архіву_не_пише()
    {
        var cs = await CreateDatabaseBeforeMigrationAsync("_L7C");
        var (bindingId, landId, otherId) = await ArrangeConsumersAsync(cs);

        await MigrateAsync(cs, null);
        await MigrateAsync(cs, BeforeMigration);

        Assert.Equal(
            [$"{bindingId}:{landId}", $"{bindingId}:{otherId}"],
            await PairsAsync(cs));
        Assert.Equal(1, await ScalarAsync(cs, "SELECT COUNT(*) FROM sys.indexes WHERE name = N'IX_DocSrcBindingConsumer_Template'"));
        Assert.Equal(2, await ScalarAsync(cs, "SELECT COUNT(*) FROM sys.foreign_keys WHERE name LIKE N'FK[_]DocSrcBindingConsumer[_]%'"));

        // Порожня таблиця (як на свіжій базі): зняття без нового запису архіву.
        await ExecuteAsync(cs, "DELETE FROM cfg.DocumentSourceBindingConsumer;");
        await MigrateAsync(cs, null);
        Assert.Single(await ArchiveAsync(cs));
        Assert.Equal(0, await ScalarAsync(cs, "SELECT COUNT(*) FROM sys.objects WHERE object_id = OBJECT_ID(N'cfg.DocumentSourceBindingConsumer')"));
    }

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Зв'язка й два шаблони-споживачі (ключі на користувачів/довідник вимкнено — їх тест не створює).</summary>
    private static async Task<(int BindingId, int LandId, int OtherId)> ArrangeConsumersAsync(string cs)
    {
        await ExecuteAsync(cs, """
            ALTER TABLE cfg.Template NOCHECK CONSTRAINT ALL;
            ALTER TABLE cfg.DocumentSourceBinding NOCHECK CONSTRAINT ALL;
            """);
        var source = await InsertTemplateAsync(cs, "L7_HSE400");
        var land = await InsertTemplateAsync(cs, "L7_LAND");
        var other = await InsertTemplateAsync(cs, "L7_OTHER");
        var binding = await ScalarAsync(cs, $"""
            SET NOCOUNT ON;
            INSERT INTO cfg.DocumentSourceBinding (Code, SourceTemplateId, RegistryDefId, MappingKind, SourceTableCodes, Policy,
                                                   SourceState, ReadFromDocument, RecalculateDependents, UpdatedAt, UpdatedByUserId)
            VALUES (N'L7_BINDING', {Num(source)}, 1, 1, NULL, 2, 1, 1, 1, NULL, NULL);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """);
        await ExecuteAsync(cs, $"""
            INSERT INTO cfg.DocumentSourceBindingConsumer (BindingId, TemplateId)
            VALUES ({Num(binding)}, {Num(land)}), ({Num(binding)}, {Num(other)});
            """);
        return (binding, land, other);
    }

    private static Task<int> InsertTemplateAsync(string cs, string code)
        => ScalarAsync(cs, $$"""
            SET NOCOUNT ON;
            INSERT INTO cfg.Template (Code, NameL10n, CreatedAt, CreatedByUserId)
            VALUES (N'{{code}}', N'{"en":"{{code}}"}', SYSUTCDATETIME(), 1);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """);

    private static async Task<List<string>> ArchiveAsync(string cs)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT DetailsJson FROM aud.SecurityEvent WHERE EventType = N'{B179L7DropSourceBindingConsumer.RetiredEventType}' ORDER BY Id";
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static async Task<List<string>> PairsAsync(string cs)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONCAT(BindingId, ':', TemplateId) FROM cfg.DocumentSourceBindingConsumer ORDER BY BindingId, TemplateId";
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <summary>Своя база до <see cref="BeforeMigration"/> плюс справжні <c>aud.SecurityEvent</c> і тригер з 11-audit-tables.sql.</summary>
    private async Task<string> CreateDatabaseBeforeMigrationAsync(string suffix)
    {
        var builder = new SqlConnectionStringBuilder(sql.ConnectionString);
        var name = builder.InitialCatalog + suffix;

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
        await MigrateAsync(builder.ConnectionString, BeforeMigration);

        var audit = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Persistence", "Sql", "11-audit-tables.sql"));
        await ExecuteAsync(builder.ConnectionString, "IF SCHEMA_ID(N'aud') IS NULL EXEC(N'CREATE SCHEMA aud');");
        await ExecuteAsync(
            builder.ConnectionString,
            Block(audit, "IF OBJECT_ID(N'aud.SecurityEvent', N'U') IS NULL").Replace(" ON ps_AuditByMonth(ChangedAt)", string.Empty, StringComparison.Ordinal));
        await ExecuteAsync(builder.ConnectionString, Block(audit, "CREATE OR ALTER TRIGGER aud.TR_SecurityEvent_Immutable"));
        return builder.ConnectionString;
    }

    /// <summary>Пакет скрипта від <paramref name="start"/> до найближчого <c>GO</c>.</summary>
    private static string Block(string script, string start)
    {
        var from = script.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"у 11-audit-tables.sql немає «{start}»");
        var to = script.IndexOf("\nGO", from, StringComparison.Ordinal);
        return script[from..to];
    }

    private static EcrDbContext Context(string connectionString)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    private static async Task MigrateAsync(string connectionString, string? target)
    {
        await using var db = Context(connectionString);
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task ExecuteAsync(string connectionString, string commandText)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(string connectionString, string query)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        command.CommandTimeout = 300;
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static SqlException? FindSqlException(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is SqlException found)
            {
                return found;
            }
        }

        return null;
    }
}
