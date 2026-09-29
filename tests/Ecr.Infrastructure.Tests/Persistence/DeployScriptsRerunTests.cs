// tests/Ecr.Infrastructure.Tests/Persistence/DeployScriptsRerunTests.cs
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Скрипти, які <c>tools/deploy-ecr.ps1</c> виконує на КОЖНОМУ оновленні наявної
/// бази, можна повторно виконати на базі з даними: без помилки, без зміни схеми,
/// без втрати рядків.
/// </summary>
/// <remarks>
/// ⛔ Предмет. Крок 2 розгортання (<c>deploy-ecr.ps1</c>, масив <c>$scripts</c>)
/// накочує 01…15 і <c>migration.sql</c> щоразу — і на першій установці, і на
/// кожному оновленні живої бази. Тобто кожен скрипт у проді виконується ВДРУГЕ
/// (утретє, …) поверх схеми, яку сам же створив, і поверх даних замовника.
/// Фікстура виконує їх рівно один раз на порожній базі, тож неідемпотентний
/// скрипт не червонив нічого аж до першого оновлення в замовника.
///
/// ⚠ Перелік НЕ дублюється літералом: він читається з самого
/// <c>deploy-ecr.ps1</c> (блок <c>$scripts.AddRange(…)</c>). Новий скрипт у
/// розгортанні автоматично потрапляє під перевірку; зламаний парсинг зупиняє
/// тест сторожем «знайдено ≥ 10 імен», а не мовчки перевіряє порожній список.
/// <c>14-agent-jobs.sql</c> поза масивом (лише <c>-FirstDeployment</c>) — він не
/// йде на оновленні й тут не перевіряється; <c>09-seed.sql</c> розгортання не
/// бере взагалі (сід виконує застосунок).
///
/// ⚠ Виконання — як у <c>sqlcmd -b -I</c>: кожен файл — окремий сеанс, пакети
/// по <c>GO</c> (<see cref="SqlBatches"/>), <c>QUOTED_IDENTIFIER ON</c>, перша ж
/// помилка зупиняє файл. На відміну від розгортання тест іде далі до наступного
/// файлу — щоб звіт називав УСІ неідемпотентні скрипти за один прогін.
///
/// ⚠ Власна база (<see cref="SqlServerFixture.WithOwnDatabase"/>), не спільна
/// база колекції: <c>06-rcsi.sql</c> робить <c>ROLLBACK IMMEDIATE</c>, а
/// неідемпотентний скрипт лишив би схему напівзміненою під сусідніми тестами.
///
/// Мутаційний доказ: прибрати в <c>13-cache-table.sql</c> охорону
/// <c>IF OBJECT_ID(…) IS NULL</c> навколо <c>CREATE TABLE</c> — тест червоний
/// з «There is already an object named …» і назвою файлу.
/// </remarks>
[Collection("SqlServer")]
public sealed class DeployScriptsRerunTests
{
    /// <summary>Позначка кроку міграцій у масиві <c>$scripts</c>.</summary>
    private const string MigrationToken = "<migration>";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Перелік_скриптів_оновлення_читається_з_deploy_ecr_і_не_порожній()
    {
        var scripts = DeployUpdateScripts();

        // Сторож парсингу: регулярка, що нічого не знайшла, дала б зелений тест
        // над порожнім списком.
        Assert.True(
            scripts.Count(s => s.EndsWith(".sql", StringComparison.Ordinal)) >= 10,
            $"з deploy-ecr.ps1 прочитано замало скриптів: {string.Join(", ", scripts)}");
        Assert.Contains(MigrationToken, scripts);
        Assert.DoesNotContain("09-seed.sql", scripts);
        Assert.DoesNotContain("14-agent-jobs.sql", scripts);

        foreach (var name in scripts.Where(s => s != MigrationToken))
        {
            Assert.True(File.Exists(Path.Combine(SqlDirectory(), name)), $"немає {name} у Persistence/Sql");
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повторне_виконання_скриптів_оновлення_на_базі_з_даними_без_помилок_без_зміни_схеми_й_даних()
    {
        var scripts = DeployUpdateScripts();
        Assert.True(scripts.Count >= 10, $"з deploy-ecr.ps1 прочитано замало: {string.Join(", ", scripts)}");

        var database = SqlServerFixture.WithOwnDatabase("_rerun");
        await database.InitializeAsync();
        try
        {
            var connectionString = database.ConnectionString;
            await FillAsync(connectionString);

            var schemaBefore = await SchemaSnapshotAsync(connectionString);
            var dataBefore = await DataSnapshotAsync(connectionString);

            var failures = new List<string>();
            foreach (var name in scripts)
            {
                var text = name == MigrationToken
                    ? IdempotentMigrationScript(database)
                    : await File.ReadAllTextAsync(Path.Combine(SqlDirectory(), name));

                var failure = await RunLikeSqlcmdAsync(connectionString, text);
                if (failure is not null)
                {
                    failures.Add($"{name}: {failure}");
                }
            }

            Assert.True(
                failures.Count == 0,
                "Повторне виконання скриптів оновлення впало (deploy-ecr.ps1 зупинився б на першому):"
                + Environment.NewLine + string.Join(Environment.NewLine, failures));

            var schemaAfter = await SchemaSnapshotAsync(connectionString);
            AssertSame("схема", schemaBefore, schemaAfter);

            var dataAfter = await DataSnapshotAsync(connectionString);
            AssertSame("дані (рядки й контрольні суми таблиць)", dataBefore, dataAfter);
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    /// <summary>
    /// Імена з блоку <c>$scripts.AddRange(…)</c> у <c>deploy-ecr.ps1</c>, у тому
    /// самому порядку; <c>&lt;migration&gt;</c> — крок <c>migration.sql</c>.
    /// </summary>
    private static List<string> DeployUpdateScripts()
    {
        var deploy = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "deploy-ecr.ps1"));

        var block = Regex.Match(
            deploy,
            @"\$scripts\.AddRange\(\s*\[string\[\]\]\((?<list>.*?)\)\s*\)",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));
        Assert.True(block.Success, "у deploy-ecr.ps1 не знайдено блоку $scripts.AddRange([string[]](…)) — сторож дивиться не туди");

        return [.. Regex.Matches(block.Groups["list"].Value, "'(?<name>[^']+)'", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(m => m.Groups["name"].Value)];
    }

    /// <summary>Той самий <c>migration.sql</c>, що генерує <c>deploy-ecr.ps1</c> (<c>--idempotent</c>).</summary>
    private static string IdempotentMigrationScript(SqlServerFixture database)
    {
        using var db = database.CreateContext();
        return db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
    }

    /// <summary>
    /// Виконує скрипт як <c>sqlcmd -b -I</c>: власний сеанс, пакети по <c>GO</c>,
    /// перша помилка зупиняє файл.
    /// </summary>
    /// <returns><c>null</c> — пройшов; інакше номер пакета й помилка дослівно.</returns>
    private static async Task<string?> RunLikeSqlcmdAsync(string connectionString, string script)
    {
        // Pooling=false: кожен файл — справді новий сеанс, як окремий процес sqlcmd.
        var builder = new SqlConnectionStringBuilder(connectionString) { Pooling = false };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        // `sqlcmd -I` — QUOTED_IDENTIFIER ON. У SqlClient так і за замовчуванням,
        // але тут не покладаємось на дефолт драйвера.
        await ExecuteAsync(connection, "SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON;");

        var batches = SqlBatches.Split(script);
        for (var i = 0; i < batches.Count; i++)
        {
            try
            {
                await ExecuteAsync(connection, batches[i]);
            }
            catch (SqlException error)
            {
                var head = batches[i].Length > 200 ? batches[i][..200] + "…" : batches[i];
                return $"пакет {i + 1}/{batches.Count}: Msg {error.Number}, {error.Message}{Environment.NewLine}    {head.ReplaceLineEndings(" ")}";
            }
        }

        return null;
    }

    /// <summary>Штатний ланцюг «шаблон → проєкт → період → документ» і значення в комірках.</summary>
    private static async Task FillAsync(string connectionString)
    {
        var doc = await new TestDocumentBuilder(connectionString).BuildAsync(rowCount: 3);

        var values = new StringBuilder();
        for (var row = 0; row < doc.RowIds.Count; row++)
        {
            values.Append(CultureInfo.InvariantCulture, $"""
                INSERT INTO doc.CellValue
                    (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
                VALUES ({doc.PeriodKey.Value}, {doc.RowIds[row]}, {doc.ColumnDefIds[1]},
                        {doc.TableDefId}, {(row + 1) * 10.5m}, 0, 0);

                """);
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, values.ToString());

        // Сторож наповнення: база без даних зробила б перевірку «дані не втрачені» порожньою.
        await using var check = connection.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM doc.CellValue WHERE TableDefId = {doc.TableDefId}";
        Assert.Equal(doc.RowIds.Count, (int)(await check.ExecuteScalarAsync())!);
    }

    /// <summary>Знімок схеми: імена й типи, без дат і ідентифікаторів.</summary>
    /// <remarks>
    /// ⚠ <c>{C}</c> — <c>COLLATE DATABASE_DEFAULT</c> на кожному рядковому стовпці
    /// каталогу: частина з них (<c>type_desc</c>, <c>sys.databases.name</c>…) у
    /// зіставленні інстансу, а база — <c>Latin1_General_100_CI_AS_SC</c>, і без
    /// явного зіставлення конкатенація падає на «collation conflict».
    /// </remarks>
    private static Task<List<string>> SchemaSnapshotAsync(string connectionString)
        => QueryLinesAsync(connectionString, """
            SELECT N'obj|' + s.name{C} + N'.' + o.name{C} + N'|' + o.type{C}
            FROM sys.objects AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0
            UNION ALL
            SELECT N'col|' + s.name{C} + N'.' + o.name{C} + N'.' + c.name{C} + N'|' + ty.name{C}
                 + N'|' + CAST(c.max_length AS nvarchar(10)) + N'|' + CAST(c.precision AS nvarchar(10))
                 + N'|' + CAST(c.scale AS nvarchar(10)) + N'|' + CAST(c.is_nullable AS nvarchar(1))
                 + N'|' + CAST(c.is_identity AS nvarchar(1))
            FROM sys.columns AS c
            JOIN sys.objects AS o ON o.object_id = c.object_id
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
            WHERE o.is_ms_shipped = 0
            UNION ALL
            SELECT N'idx|' + s.name{C} + N'.' + o.name{C} + N'.' + ISNULL(i.name{C}, N'<heap>') + N'|' + i.type_desc{C}
                 + N'|' + ds.name{C} + N'|' + CAST(i.is_unique AS nvarchar(1)) + N'|' + ISNULL(i.filter_definition{C}, N'')
                 + N'|' + CAST(i.is_disabled AS nvarchar(1))
            FROM sys.indexes AS i
            JOIN sys.objects AS o ON o.object_id = i.object_id
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
            WHERE o.is_ms_shipped = 0
            UNION ALL
            SELECT N'ps|' + ps.name{C} + N'|' + pf.name{C}
            FROM sys.partition_schemes AS ps JOIN sys.partition_functions AS pf ON pf.function_id = ps.function_id
            UNION ALL
            SELECT N'pf|' + pf.name{C} + N'|' + CAST(pf.fanout AS nvarchar(10))
            FROM sys.partition_functions AS pf
            UNION ALL
            SELECT N'fg|' + fg.name{C} + N'|' + fg.type{C}
            FROM sys.filegroups AS fg
            UNION ALL
            SELECT N'file|' + f.name{C} + N'|' + f.type_desc{C} + N'|' + ISNULL(FILEGROUP_NAME(f.data_space_id){C}, N'')
            FROM sys.database_files AS f
            UNION ALL
            SELECT N'trg|' + OBJECT_SCHEMA_NAME(t.parent_id){C} + N'.' + OBJECT_NAME(t.parent_id){C} + N'.' + t.name{C}
                 + N'|' + CAST(t.is_disabled AS nvarchar(1))
            FROM sys.triggers AS t WHERE t.parent_class = 1
            UNION ALL
            SELECT N'mod|' + OBJECT_SCHEMA_NAME(m.object_id){C} + N'.' + OBJECT_NAME(m.object_id){C}
                 + N'|' + CONVERT(nvarchar(64), HASHBYTES('SHA2_256', m.definition), 2){C}
            FROM sys.sql_modules AS m
            WHERE OBJECTPROPERTY(m.object_id, 'IsMSShipped') = 0
            UNION ALL
            SELECT N'db|rcsi=' + CAST(is_read_committed_snapshot_on AS nvarchar(1))
                 + N'|snapshot=' + CAST(snapshot_isolation_state AS nvarchar(1))
            FROM sys.databases WHERE database_id = DB_ID();
            """.Replace("{C}", " COLLATE DATABASE_DEFAULT", StringComparison.Ordinal));

    /// <summary>
    /// Кількість рядків і контрольна сума кожної користувацької таблиці —
    /// точним <c>COUNT_BIG</c>, а не наближеними <c>sys.partitions.rows</c>.
    /// </summary>
    private static Task<List<string>> DataSnapshotAsync(string connectionString)
        => QueryLinesAsync(connectionString, """
            SET NOCOUNT ON;
            DECLARE @sql nvarchar(max) = (
                SELECT STRING_AGG(CAST(
                    N'SELECT N''' + s.name + N'.' + t.name + N'|'' + CAST(COUNT_BIG(*) AS nvarchar(20)) + N''|'' '
                    + N'+ CAST(ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(*)), 0) AS nvarchar(20)) FROM '
                    + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) AS nvarchar(max)), N' UNION ALL ')
                FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id
                WHERE t.is_ms_shipped = 0);
            EXEC sys.sp_executesql @sql;
            """);

    private static async Task<List<string>> QueryLinesAsync(string connectionString, string query)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        command.CommandTimeout = 600;

        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(0));
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static void AssertSame(string what, List<string> before, List<string> after)
    {
        var lost = before.Except(after, StringComparer.Ordinal).ToList();
        var added = after.Except(before, StringComparer.Ordinal).ToList();

        Assert.True(
            before.Count > 0 && lost.Count == 0 && added.Count == 0,
            $"Повторне виконання змінило {what} (знімок до: {before.Count} рядків)."
            + Environment.NewLine + "було, зникло:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", lost)
            + Environment.NewLine + "стало, з'явилось:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", added));
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        // sqlcmd за замовчуванням не має таймауту запиту; 01-filegroups створює файли.
        command.CommandTimeout = 600;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Каталог, звідки <c>deploy-ecr.ps1</c> бере скрипти в дереві репозиторію.</summary>
    private static string SqlDirectory()
        => Path.Combine(RepoRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ecr.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Ecr.sln не знайдено вгору від каталогу збірки.");
    }
}
