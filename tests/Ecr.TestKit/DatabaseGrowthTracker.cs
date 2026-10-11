using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Sdk;

namespace Ecr.TestKit;

/// <summary>
/// Подія росту файла спільної тестової бази: який тест щойно закінчився й на скільки виріс файл.
/// </summary>
/// <param name="Test">«Клас.метод» тесту, після якого файл виявився більшим.</param>
/// <param name="File">Логічне ім'я файла (<c>sys.database_files.name</c>).</param>
/// <param name="FromMb">Розмір до тесту, МБ.</param>
/// <param name="ToMb">Розмір після тесту, МБ.</param>
public readonly record struct DatabaseGrowthEvent(string Test, string File, int FromMb, int ToMb);

/// <summary>
/// Діагностика росту спільної тестової бази: ХТО з тестів виростив файл і що лежить у базі.
/// </summary>
/// <remarks>
/// ⛔ Навіщо. Стеля наприкінці колекції піднята до <see cref="SqlServerFixture.MaxFileMbAtCollectionEnd"/> (D-375),
/// бо якийсь клас ПІСЛЯ сторожа <c>TestDatabaseSizeTests</c> дає другий приріст файла (CI run 38079429594: 136 МБ).
/// Сам сторож бачить лише підсумок, а порядок класів xUnit не задається, тож назвати винного з коду неможливо — його
/// треба ПОБАЧИТИ. Тест (клас у колекції <c>SqlServer</c>) виконується послідовно, тож приріст файла між двома
/// замірами «після тесту» належить саме тому тесту, що відпрацював у цьому проміжку.
///
/// ⚠ Заміри роблять <see cref="TrackDatabaseGrowthAttribute"/> (рівня збірки, лише там, де його підключено) і
/// <see cref="SqlServerFixture"/> (початкова точка після розгортання схеми; звіт — у <c>DisposeAsync</c> ДО зупинки
/// контейнера). Один запит до <c>sys.database_files</c> на тест — мілісекунди.
///
/// ⚠ Що дивитись у CI: крок «Тести .NET» → Step Summary прогону (<c>GITHUB_STEP_SUMMARY</c>), блок «Ріст тестової
/// бази». Там — файли, тести-винуватці з розмірами «з → до», топ-10 таблиць і заповненість журналу. Коли стеля
/// перетята, той самий текст — у повідомленні збою прибирання колекції.
/// </remarks>
public sealed class DatabaseGrowthTracker(string connectionString)
{
    /// <summary>Скільки подій тримаємо: розмір файла росте рідко, тож їх одиниці; решту відрізаємо.</summary>
    private const int MaxEvents = 40;

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DatabaseGrowthEvent> _events = [];

    /// <summary>Події росту в порядку виникнення.</summary>
    public IReadOnlyList<DatabaseGrowthEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>
    /// Порівнює розміри з попереднім замірам і записує ріст, який припадає на <paramref name="test"/>.
    /// Перший виклик лише ставить початкову точку (ріст під час розгортання схеми — не тест).
    /// </summary>
    /// <param name="test">Ім'я тесту, що щойно завершився.</param>
    /// <param name="sizesMb">Файл → розмір, МБ.</param>
    /// <returns><c>true</c>, коли хоча б один файл виріс.</returns>
    public bool Observe(string test, IReadOnlyDictionary<string, int> sizesMb)
    {
        ArgumentNullException.ThrowIfNull(test);
        ArgumentNullException.ThrowIfNull(sizesMb);

        var grew = false;
        lock (_gate)
        {
            foreach (var (file, mb) in sizesMb)
            {
                if (_last.TryGetValue(file, out var before) && mb > before)
                {
                    grew = true;
                    if (_events.Count < MaxEvents)
                    {
                        _events.Add(new DatabaseGrowthEvent(test, file, before, mb));
                    }
                }

                _last[file] = mb;
            }
        }

        return grew;
    }

    /// <summary>Заміряє файли бази й передає <see cref="Observe"/>. Помилка SQL — тихо: діагностика не валить тест.</summary>
    /// <param name="test">Ім'я тесту, що щойно завершився.</param>
    public void Sample(string test)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name, CAST(size / 128 AS int) FROM sys.database_files";

            var sizes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    sizes[reader.GetString(0)] = reader.GetInt32(1);
                }
            }

            Observe(test, sizes);
        }
        catch (SqlException)
        {
            // База недоступна (її скинув тест) — міряти нічого.
        }
        catch (InvalidOperationException)
        {
            // З'єднання вже закрите пулом під час зупинки колекції.
        }
    }

    /// <summary>
    /// Звіт про вміст і ріст бази; <c>null</c>, коли жоден файл не перевищує <paramref name="reportAboveMb"/>.
    /// </summary>
    /// <param name="databaseName">Ім'я бази — для заголовка.</param>
    /// <param name="reportAboveMb">Поріг: файл більший за нього робить звіт потрібним.</param>
    /// <param name="always">Будувати звіт і без перевищення (діагностичний прогін, тест).</param>
    public async Task<string?> BuildReportAsync(string databaseName, int reportAboveMb, bool always = false)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        var files = await ReadRowsAsync(
            connection,
            "SELECT name, CAST(size / 128 AS int) FROM sys.database_files ORDER BY size DESC",
            reader => (reader.GetString(0), reader.GetInt32(1))).ConfigureAwait(false);

        if (!always && !files.Any(f => f.Item2 > reportAboveMb))
        {
            return null;
        }

        var tables = await ReadRowsAsync(
            connection,
            """
            SELECT TOP (10)
                   s.name + N'.' + o.name,
                   CAST(SUM(ps.reserved_page_count) / 128.0 AS decimal(18, 1)),
                   SUM(CASE WHEN ps.index_id IN (0, 1) THEN ps.row_count ELSE 0 END)
            FROM sys.dm_db_partition_stats AS ps
            JOIN sys.objects AS o ON o.object_id = ps.object_id
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            WHERE o.type = 'U'
            GROUP BY s.name, o.name
            ORDER BY SUM(ps.reserved_page_count) DESC
            """,
            reader => (reader.GetString(0), reader.GetDecimal(1), reader.GetInt64(2))).ConfigureAwait(false);

        var log = await ReadRowsAsync(
            connection,
            """
            SELECT CAST(u.total_log_size_in_bytes / 1048576 AS int),
                   CAST(u.used_log_space_in_bytes / 1048576 AS int),
                   d.log_reuse_wait_desc
            FROM sys.dm_db_log_space_usage AS u
            CROSS JOIN sys.databases AS d
            WHERE d.database_id = DB_ID()
            """,
            reader => (reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2))).ConfigureAwait(false);

        (int TotalMb, int UsedMb, string ReuseWait)? logUsage = null;
        if (log.Count > 0)
        {
            logUsage = log[0];
        }

        return FormatReport(databaseName, files, Events, tables, logUsage);
    }

    /// <summary>Текст звіту (markdown, читається і в консолі). Чиста функція — перевіряється без бази.</summary>
    /// <param name="databaseName">Ім'я бази.</param>
    /// <param name="files">Файли: ім'я і розмір, МБ.</param>
    /// <param name="events">Ріст за тестами.</param>
    /// <param name="tables">Топ таблиць: ім'я, зарезервовано МБ, рядків.</param>
    /// <param name="log">Журнал: розмір, використано (МБ) і чого чекає звільнення; <c>null</c> — невідомо.</param>
    public static string FormatReport(
        string databaseName,
        IReadOnlyList<(string Name, int Mb)> files,
        IReadOnlyList<DatabaseGrowthEvent> events,
        IReadOnlyList<(string Name, decimal Mb, long Rows)> tables,
        (int TotalMb, int UsedMb, string ReuseWait)? log)
    {
        var text = new StringBuilder();
        text.Append("### Ріст тестової бази: ").AppendLine(databaseName).AppendLine();

        text.AppendLine("Файли: " + (files.Count == 0
            ? "—"
            : string.Join(", ", files.Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Name} = {f.Mb} МБ")))));

        if (log is { } l)
        {
            text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Журнал: використано {l.UsedMb} із {l.TotalMb} МБ; log_reuse_wait = {l.ReuseWait}."));
        }

        text.AppendLine().AppendLine("Ріст файлів за тестами (тест виконується послідовно в колекції):");
        if (events.Count == 0)
        {
            text.AppendLine("- подій росту після розгортання схеми не зафіксовано (немає `[assembly: TrackDatabaseGrowth]`"
                + " у цій збірці або ріст стався до першого тесту).");
        }

        foreach (var e in events)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- `{e.Test}`: {e.File} {e.FromMb} → {e.ToMb} МБ"));
        }

        text.AppendLine().AppendLine("Топ-10 таблиць за зарезервованим місцем:");
        foreach (var t in tables)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- {t.Name}: {t.Mb:0.0} МБ, {t.Rows} рядків"));
        }

        return text.ToString();
    }

    private static async Task<List<T>> ReadRowsAsync<T>(SqlConnection connection, string sql, Func<SqlDataReader, T> map)
    {
        var rows = new List<T>();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(map(reader));
            }
        }
        catch (SqlException)
        {
            // Немає права на DMV чи об'єкта — звіт лишається частковим, а не падає: це діагностика.
        }

        return rows;
    }

    /// <summary>Виводить звіт туди, де його побачать у CI: Step Summary прогону (якщо є) і stderr.</summary>
    /// <param name="report">Текст звіту.</param>
    public static void Publish(string report)
    {
        ArgumentNullException.ThrowIfNull(report);

        Console.Error.WriteLine(report);

        var summary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrWhiteSpace(summary))
        {
            return;
        }

        try
        {
            File.AppendAllText(summary, report + Environment.NewLine);
        }
        catch (IOException)
        {
            // Діагностика не привід падати.
        }
        catch (UnauthorizedAccessException)
        {
            // Те саме.
        }
    }
}

/// <summary>
/// Записує ріст файлів спільної бази після КОЖНОГО тесту колекції <c>SqlServer</c> (рівень збірки).
/// </summary>
/// <remarks>
/// Підключення: <c>[assembly: TrackDatabaseGrowth]</c> у збірці, що має спільну базу. Без атрибута звіт у
/// <see cref="SqlServerFixture"/> лишається, але без розділу «ріст за тестами».
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class TrackDatabaseGrowthAttribute : BeforeAfterTestAttribute
{
    private const string SharedCollection = "SqlServer";

    /// <inheritdoc />
    public override void After(MethodInfo methodUnderTest)
    {
        ArgumentNullException.ThrowIfNull(methodUnderTest);

        var type = methodUnderTest.ReflectedType ?? methodUnderTest.DeclaringType;
        if (type is null || !InSharedCollection(type))
        {
            return;
        }

        SqlServerFixture.Shared?.Growth?.Sample($"{type.Name}.{methodUnderTest.Name}");
    }

    /// <summary>
    /// Клас (чи його базовий) оголошений <c>[Collection("SqlServer")]</c>. У xUnit 2 ім'я колекції не відкрито
    /// властивістю атрибута, тож читаємо аргумент конструктора.
    /// </summary>
    private static bool InSharedCollection(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var data in current.GetCustomAttributesData())
            {
                if (data.AttributeType == typeof(CollectionAttribute)
                    && data.ConstructorArguments.Count > 0
                    && data.ConstructorArguments[0].Value as string == SharedCollection)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
