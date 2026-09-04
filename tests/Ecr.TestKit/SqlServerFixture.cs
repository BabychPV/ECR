using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Xunit;

namespace Ecr.TestKit;

/// <summary>
/// Реальний SQL Server у контейнері для інтеграційних тестів.
/// </summary>
/// <remarks>
/// SQLite тут не підходить: перевіряти треба саме те, чого в ньому немає —
/// партиціонування, складені FK, <c>TRUNCATE … WITH (PARTITIONS)</c>, RCSI,
/// тригери. Тести, що використовують цю фікстуру, позначені
/// <c>[Trait("Category","Integration")]</c> і не входять у прогін за
/// замовчуванням (`04-environment.md` §5).
/// </remarks>
public sealed class SqlServerFixture : IAsyncLifetime
{
    /// <summary>
    /// Рядок підключення до <b>сервера</b> замість контейнера.
    /// </summary>
    /// <remarks>
    /// Docker є не на кожній машині розробника, а SQL Server Express —
    /// зазвичай є. Приклад:
    /// <c>Server=localhost\SQLEXPRESS;Integrated Security=true;TrustServerCertificate=true</c>.
    /// Ім'я бази з цього рядка ігнорується: фікстура працює зі своєю
    /// (<see cref="DatabaseName"/>) і **перестворює її з нуля**.
    /// </remarks>
    private const string LocalServerVariable = "ECR_TEST_SQL";

    /// <summary>Префікс імені тестової бази. Перевизначається <c>ECR_TEST_DB</c>.</summary>
    private const string DatabaseNamePrefix = "EcrTest";

    private MsSqlContainer? _container;

    /// <summary>Рядок підключення до тестової БД.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>Ім'я тестової бази.</summary>
    public string DatabaseName { get; private set; } = DefaultDatabaseName();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        DatabaseName = Environment.GetEnvironmentVariable("ECR_TEST_DB") ?? DefaultDatabaseName();

        var serverConnection = Environment.GetEnvironmentVariable(LocalServerVariable);
        if (string.IsNullOrWhiteSpace(serverConnection))
        {
            // Образ задається конструктором: беспараметричний MsSqlBuilder
            // оголошений застарілим у Testcontainers 4.x.
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

            await _container.StartAsync().ConfigureAwait(false);
            serverConnection = _container.GetConnectionString();
        }

        ConnectionString = await CreateEmptyDatabaseAsync(serverConnection).ConfigureAwait(false);
        await BuildSchemaAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }

        // Локальний сервер лишається з базою: після невдалого прогону в неї
        // корисно зазирнути. Наступний запуск усе одно перестворює її з нуля.
    }

    /// <summary>
    /// Ім'я бази — своє для КОЖНОЇ тестової збірки.
    /// </summary>
    /// <remarks>
    /// ⚠ Спільне ім'я було дефектом, і виявився він лише в повному прогоні:
    /// `Ecr.Infrastructure.Tests` і `Ecr.Api.Tests` виконуються паралельно, і
    /// фікстура однієї збірки скидала базу, з якою в цей момент працювала
    /// друга. Поодинці кожен проєкт був зелений, разом — 97 падінь із нізвідки
    /// (`Q-055`).
    ///
    /// Ім'я виводиться з каталогу збірки: `…/tests/Ecr.Api.Tests/bin/…` дає
    /// `EcrTest_Api`.
    /// </remarks>
    private static string DefaultDatabaseName()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !directory.Name.EndsWith(".Tests", StringComparison.Ordinal))
        {
            directory = directory.Parent;
        }

        var suffix = directory?.Name
            .Replace("Ecr.", string.Empty, StringComparison.Ordinal)
            .Replace(".Tests", string.Empty, StringComparison.Ordinal);

        return string.IsNullOrWhiteSpace(suffix) ? DatabaseNamePrefix : $"{DatabaseNamePrefix}_{suffix}";
    }

    /// <summary>Скидає і створює порожню базу; повертає рядок підключення до неї.</summary>
    private async Task<string> CreateEmptyDatabaseAsync(string serverConnection)
    {
        var master = new SqlConnectionStringBuilder(serverConnection)
        {
            InitialCatalog = "master",
            TrustServerCertificate = true,
        };

        await using (var connection = new SqlConnection(master.ConnectionString))
        {
            await connection.OpenAsync().ConfigureAwait(false);

            // SINGLE_USER WITH ROLLBACK IMMEDIATE: інакше DROP не пройде,
            // якщо хтось (наприклад, попередній прогін) лишив з'єднання.
            await ExecuteAsync(connection, $"""
                IF DB_ID(N'{DatabaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{DatabaseName}];
                END
                """).ConfigureAwait(false);

            // COLLATE задається ЯВНО (02a §1.0, Q-061). Без цього тестова база
            // успадковує зіставлення інстансу розробника, і поведінка
            // унікальності бізнес-кодів стає властивістю чужої машини: на
            // одному ноутбуці seed вставляється, на іншому падає.
            await ExecuteAsync(
                connection,
                $"CREATE DATABASE [{DatabaseName}] COLLATE Latin1_General_100_CI_AS_SC;")
                .ConfigureAwait(false);
        }

        var target = new SqlConnectionStringBuilder(serverConnection)
        {
            InitialCatalog = DatabaseName,
            TrustServerCertificate = true,
        };

        return target.ConnectionString;
    }

    /// <summary>
    /// Вибудовує схему в тому самому порядку, що й розгортання.
    /// </summary>
    /// <remarks>
    /// ⚠ Порядок не довільний і повторює `09-commands.md` §3:
    /// <list type="number">
    /// <item>`01`, `02` — файлові групи і схеми партиціонування мають існувати
    /// ДО того, як `07` спробує на них щось покласти;</item>
    /// <item>міграції — форма таблиць;</item>
    /// <item>`07` — прив'язка партиційованих таблиць до схем. Пропустити його
    /// означає тестувати не ту фізичну модель, яка поїде в прод (`Q-035`);</item>
    /// <item>`11` — таблиці `aud`, яких теж немає в моделі EF;</item>
    /// <item>`08` — таблиці `sys_ecr`, яких немає в моделі EF;</item>
    /// <item>`10` — тригери незмінності: `HasTrigger()` їх не створює;</item>
    /// <item>`06` — RCSI;</item>
    /// <item>seed.</item>
    /// </list>
    /// `03`, `04`, `05` не виконуються: вони посилаються на `calc.*` і `arc.*`,
    /// а тих таблиць до етапів 3–5 ще немає.
    /// </remarks>
    private async Task BuildSchemaAsync()
    {
        await RunScriptAsync("01-filegroups.sql").ConfigureAwait(false);
        await RunScriptAsync("02-partitions.sql").ConfigureAwait(false);

        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync().ConfigureAwait(false);
        }

        // ⚠ 11 ПЕРЕД 07: `07` переносить aud.* на ps_AuditByMonth, і якщо
        // таблиць ще немає, він мовчки їх пропускає (`Q-049`).
        await RunScriptAsync("11-audit-tables.sql").ConfigureAwait(false);
        await RunScriptAsync("07-partition-tables.sql").ConfigureAwait(false);
        await RunScriptAsync("08-system-tables.sql").ConfigureAwait(false);

        // ⚠ 10 обов'язково: HasTrigger() у конфігурації EF тригера НЕ створює,
        // він лише вимикає OUTPUT-клаузу. Без цього скрипта незмінність
        // опублікованої структури не тримає ніщо (Q-045).
        await RunScriptAsync("10-triggers.sql").ConfigureAwait(false);
        await RunScriptAsync("06-rcsi.sql").ConfigureAwait(false);

        await using var seedDb = CreateContext();
        await new SeedRunner(seedDb).RunAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Створює контекст на тестову базу.</summary>
    private EcrDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options;

        return new EcrDbContext(options);
    }

    /// <summary>Виконує SQL-скрипт із <c>Persistence/Sql</c>, розділяючи його по <c>GO</c>.</summary>
    private async Task RunScriptAsync(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Persistence", "Sql", fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"SQL-скрипт '{fileName}' не потрапив у вихідний каталог. " +
                "Перевірте <None Update=\"Persistence\\Sql\\*.sql\"> у Ecr.Infrastructure.csproj.",
                path);
        }

        var script = await File.ReadAllTextAsync(path).ConfigureAwait(false);

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        foreach (var batch in SqlBatches.Split(script))
        {
            await ExecuteAsync(connection, batch).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 300;   // 01-filegroups створює файли, це не миттєво
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
