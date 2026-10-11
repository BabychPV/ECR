// tests/Ecr.Infrastructure.Tests/Persistence/DatabaseGrowthTrackerTests.cs
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Діагностика росту спільної тестової бази (D-375): назвати тест, після якого виріс файл, і показати вміст бази.
/// </summary>
/// <remarks>
/// ⚠ Предмет — приладдя, а не продукт: воно не валить тести і не змінює стелі. Але без нього клас, що росте після
/// <c>TestDatabaseSizeTests</c>, лишається невідомим (стеля наприкінці колекції піднята до 192 МБ).
/// Мутації: <c>Observe</c> не оновлює <c>_last</c> → другий приріст дублює перший; не відрізає початкову точку →
/// перший тест «виростив» усе, що розгорнула схема; <c>BuildReportAsync</c> не бере <c>TOP (10)</c> → червоний
/// перелік таблиць.
/// </remarks>
[Collection("SqlServer")]
public sealed class DatabaseGrowthTrackerTests(SqlServerFixture sql)
{
    private static Dictionary<string, int> Sizes(int dataMb, int logMb)
        => new Dictionary<string, int> { ["data"] = dataMb, ["data_log"] = logMb };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Початкова_точка_не_є_ростом_а_приріст_називає_тест_файл_і_розміри()
    {
        var tracker = new DatabaseGrowthTracker("Server=unused");

        Assert.False(tracker.Observe("<розгортання схеми>", Sizes(72, 72)));
        Assert.Empty(tracker.Events);

        Assert.False(tracker.Observe("ATests.Перший", Sizes(72, 72)));

        Assert.True(tracker.Observe("BTests.Другий", Sizes(72, 136)));
        var single = Assert.Single(tracker.Events);
        Assert.Equal(new DatabaseGrowthEvent("BTests.Другий", "data_log", 72, 136), single);

        // Той самий розмір далі — не ріст: подія не дублюється.
        Assert.False(tracker.Observe("CTests.Третій", Sizes(72, 136)));
        Assert.Single(tracker.Events);

        // Ще один приріст іншого файла — окрема подія з правильним «з».
        Assert.True(tracker.Observe("DTests.Четвертий", Sizes(136, 136)));
        Assert.Equal(new DatabaseGrowthEvent("DTests.Четвертий", "data", 72, 136), tracker.Events[1]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Звіт_називає_тест_винуватець_файли_журнал_і_топ_таблиць()
    {
        var report = DatabaseGrowthTracker.FormatReport(
            "EcrTest_Infrastructure_ABCDEF",
            [("EcrTest_log", 136), ("EcrTest", 72)],
            [new DatabaseGrowthEvent("CollectionStoreFooTests.Заливка", "EcrTest_log", 72, 136)],
            [("coll.RawPoint", 41.5m, 100_001), ("aud.CellChange", 12.0m, 5000)],
            (136, 96, "ACTIVE_TRANSACTION"));

        Assert.Contains("EcrTest_Infrastructure_ABCDEF", report, StringComparison.Ordinal);
        Assert.Contains("EcrTest_log = 136 МБ", report, StringComparison.Ordinal);
        Assert.Contains("`CollectionStoreFooTests.Заливка`: EcrTest_log 72 → 136 МБ", report, StringComparison.Ordinal);
        Assert.Contains("log_reuse_wait = ACTIVE_TRANSACTION", report, StringComparison.Ordinal);
        Assert.Contains("- coll.RawPoint: 41.5 МБ, 100001 рядків", report, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Без_подій_звіт_каже_чому_їх_немає_а_не_мовчить()
    {
        var report = DatabaseGrowthTracker.FormatReport("Db", [("Db", 8)], [], [], null);

        Assert.Contains("TrackDatabaseGrowth", report, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Звіт_живої_бази_має_файли_і_не_більше_десяти_таблиць_а_без_перевищення_мовчить()
    {
        var tracker = new DatabaseGrowthTracker(sql.ConnectionString);

        Assert.Null(await tracker.BuildReportAsync(sql.DatabaseName, reportAboveMb: int.MaxValue));

        var report = await tracker.BuildReportAsync(sql.DatabaseName, reportAboveMb: int.MaxValue, always: true);

        Assert.NotNull(report);
        Assert.Contains(sql.DatabaseName, report, StringComparison.Ordinal);
        Assert.Contains("Файли: ", report, StringComparison.Ordinal);

        var tableLines = report!
            .Split('\n')
            .SkipWhile(line => !line.StartsWith("Топ-10 таблиць", StringComparison.Ordinal))
            .Skip(1)
            .Count(line => line.StartsWith("- ", StringComparison.Ordinal));
        Assert.InRange(tableLines, 1, 10);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Справжній_приріст_файла_бази_ловиться_заміром_sys_database_files()
    {
        var name = $"EcrTest_GrowthProbe_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = "master" };
        var probe = new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = name, Pooling = false };

        await using var admin = new SqlConnection(master.ConnectionString);
        await admin.OpenAsync();
        try
        {
            // ⚠ Файли — малі (3 + 2 МБ) і в каталозі, де вже лежить спільна база фікстури (на Windows це H:\EcrData,
            // у контейнері — каталог даних SQL Server): типовий каталог інстансу тут може бути повільним диском.
            var sharedFile = await ScalarAsync(
                admin,
                $"SELECT physical_name FROM sys.master_files WHERE database_id = DB_ID(N'{sql.DatabaseName}') AND file_id = 1");
            var separator = sharedFile.LastIndexOfAny(['\\', '/']);
            var dir = sharedFile[..(separator + 1)];
            await ExecuteAsync(
                admin,
                $"""
                CREATE DATABASE [{name}]
                ON PRIMARY (NAME = N'{name}', FILENAME = N'{dir}{name}.mdf', SIZE = 3MB, FILEGROWTH = 1MB)
                LOG ON (NAME = N'{name}_log', FILENAME = N'{dir}{name}_log.ldf', SIZE = 2MB, FILEGROWTH = 1MB);
                """);

            var tracker = new DatabaseGrowthTracker(probe.ConnectionString);
            tracker.Sample("<розгортання схеми>");
            Assert.Empty(tracker.Events);

            await ExecuteAsync(admin, $"ALTER DATABASE [{name}] MODIFY FILE (NAME = N'{name}', SIZE = 20MB);");
            tracker.Sample("ProbeTests.Виросла_база");

            var grown = Assert.Single(tracker.Events);
            Assert.Equal("ProbeTests.Виросла_база", grown.Test);
            Assert.Equal(name, grown.File);
            Assert.Equal(20, grown.ToMb);
            Assert.True(grown.FromMb < 20, $"початковий розмір {grown.FromMb} МБ");
        }
        finally
        {
            await ExecuteAsync(
                admin,
                $"""
                IF DB_ID(N'{name}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{name}];
                END
                """);
        }
    }

    private static async Task<string> ScalarAsync(SqlConnection connection, string sqlText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;

        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sqlText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }
}
