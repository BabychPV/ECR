using Testcontainers.MsSql;

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
    private MsSqlContainer? _container;

    /// <summary>Рядок підключення до тестової БД.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <inheritdoc />
    public Task InitializeAsync()
        => throw new NotImplementedException(
            "TODO: якщо змінна ECR_TEST_SQL задана — використати локальний SQL Server і НЕ " +
            "піднімати контейнер (не на кожній машині є Docker); інакше MsSqlBuilder з " +
            "образом mcr.microsoft.com/mssql/server:2022-latest. " +
            "Після старту: створити БД, застосувати міграції, виконати SQL-скрипти 01–06 " +
            "(файлові групи, партиції, RCSI), виконати seed.");

    /// <inheritdoc />
    public Task DisposeAsync()
        => throw new NotImplementedException("TODO: зупинити контейнер, якщо він піднімався.");
}
