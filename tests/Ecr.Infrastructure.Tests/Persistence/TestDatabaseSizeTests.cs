using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Тестова база не має права народжуватися продуктивного розміру.
/// </summary>
/// <remarks>
/// ⛔ Сторож з'явився заднім числом, і рахунок відомий точно: **152 ГБ**.
/// <c>01-filegroups.sql</c> зменшував файли лише на Express
/// (<c>EngineEdition = 4</c>), а замовник ухвалив ставити локально
/// **Developer Edition** (<c>H-19</c>) — у неї <c>EngineEdition = 3</c>, та
/// сама, що в Enterprise. Перевірка мовчала, і кожна тестова база з кількома
/// сотнями рядків отримувала 4096 + 4096 + 4096 + 2048 МБ. Сімнадцять баз
/// заповнили диск, і робота стала з помилкою «MODIFY FILE encountered
/// operating system error 112».
///
/// ⚠ Найоманливіше було ім'я інстансу: він називається <c>SQLEXPRESS</c>,
/// тобто сервер сам казав «Express» — а виданням не був. Саме тому сторож
/// питає РОЗМІР ФАЙЛІВ, а не видання і не наявність позначки: перевіряти те,
/// що ми щойно налаштували, означало б повторити ту саму помилку — питати
/// ознаку замість наслідку.
///
/// ⚠ Симптом був не «диск повний», а хвиля падінь SQL-тестів у всіх збірках
/// одразу. Шукати причину починають у власній правці.
/// </remarks>
[Collection("SqlServer")]
public sealed class TestDatabaseSizeTests(SqlServerFixture sql)
{
    /// <summary>
    /// Стеля одного файла тестової бази, МБ.
    /// </summary>
    /// <remarks>
    /// Скрипт створює файли по 64 МБ і дає їм рости по 64 МБ. Стеля вдвічі
    /// вища за початковий розмір: один приріст під час прогону — нормальна
    /// робота, а не привід до червоного. Продуктивний файл (2048 МБ) від цієї
    /// межі відрізняється на порядок, тож сплутати їх неможливо.
    /// </remarks>
    private const int MaxFileMb = 128;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Жоден_файл_тестової_бази_не_має_продуктивного_розміру()
    {
        var oversized = await QueryAsync($"""
            SELECT name + N' = ' + CAST(size / 128 AS nvarchar(10)) + N' МБ'
            FROM sys.database_files
            WHERE size / 128 > {MaxFileMb}
            ORDER BY size DESC
            """);

        // ⛔ Повідомлення несе ІМЕНА і РОЗМІРИ: «якийсь файл завеликий» не
        // сказало б, чи це файлова група з `01-filegroups.sql`, чи журнал, що
        // розрісся від довгої транзакції, — а лікуються вони по-різному.
        Assert.Empty(oversized);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Уся_тестова_база_вкладається_в_гігабайт()
    {
        // ⚠ Окремо від перевірки файлів, і не заради повноти: одна межа на
        // всю базу ловить те, чого не ловить межа на файл, — чотирнадцять
        // файлів, кожен трохи нижчий за стелю.
        var totalMb = await ScalarAsync<int>(
            "SELECT CAST(SUM(CAST(size AS bigint)) / 128 AS int) FROM sys.database_files");

        Assert.InRange(totalMb, 1, 1024);
    }

    private async Task<T?> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);

        return value is null or DBNull ? default : (T)value;
    }

    private async Task<List<string>> QueryAsync(string query)
    {
        var rows = new List<string>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
