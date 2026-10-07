// tests/Ecr.Adapters.Tests/SourceConnectFailureTextTests.cs
using System.Data.Odbc;
using Ecr.Adapters.PiAf;
using Ecr.Adapters.Sql;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests;

/// <summary>
/// Відмова транспорту джерела не віддає текст драйвера в повідомлення винятку
/// (SEC, TIER2): воно йде в <c>itg.CollectionRun.ErrorMessage</c>, <c>/jobs</c> і клієнту.
/// </summary>
/// <remarks>
/// ⛔ До виправлення адаптери клали <c>{ex.Message}</c> в текст <c>EcrException</c>:
/// «Login failed for user …», імена серверів, фрагменти рядка з'єднання. Тепер у
/// повідомленні лише номер помилки/SQLSTATE і кореляція, повний виняток — у журналі
/// сервера. Мутація: повернути <c>: {ex.Message}</c> — тести червоні.
/// </remarks>
public sealed class SourceConnectFailureTextTests
{
    private static readonly string[] Secrets = ["Secret123", "db01", "pi01.internal"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Sql_джерело_зі_зіпсованим_рядком_не_віддає_текст_розбору()
    {
        const string endpoint = "Server=db01;User Id=svc;Password=Secret123;Connect Timeout=abc";
        var parseText = Assert.Throws<FormatException>(() => new SqlConnectionStringBuilder(endpoint)).Message;
        var log = new CapturingLogger<SqlDataSource>();
        var adapter = new SqlDataSource(
            StoreWith(ExternalTransport.Sql, endpoint),
            Substitute.For<ISecretProvider>(),
            Settings((SqlDataSource.ValueQueryKey, "SELECT 1")),
            log);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => adapter.ReadAsync(
                new CollectionRequest(1, 7, "STREAM-1", Midnight, Midnight.AddHours(1), 10), CancellationToken.None));

        Assert.Equal("ECR-INT-0503", error.ErrorCode);
        Assert.Equal("err.ECR-INT-0503.connectionStringBroken", error.Details!["messageKey"]);
        AssertSafe(error.Message, parseText);
        AssertLoggedWithCorrelation(log, error.Message);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task PiSqlClient_джерело_зі_зіпсованим_рядком_не_віддає_текст_розбору()
    {
        const string endpoint = "Driver={unterminated;Server=pi01.internal;Pwd=Secret123";
        var parseText = Assert.Throws<ArgumentException>(() => new OdbcConnectionStringBuilder(endpoint)).Message;
        var log = new CapturingLogger<PiSqlClientDataSource>();
        var adapter = new PiSqlClientDataSource(
            StoreWith(ExternalTransport.PiSqlClient, endpoint),
            Substitute.For<ISecretProvider>(),
            Settings(("PiSqlClient:AIR:ElementListQuery", "SELECT ?")),
            log);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => adapter.DiscoverElementsAsync(1, "Plant", CancellationToken.None));

        Assert.Equal("ECR-INT-0503", error.ErrorCode);
        Assert.Equal("err.ECR-INT-0503.connectionStringBroken", error.Details!["messageKey"]);
        AssertSafe(error.Message, parseText);
        AssertLoggedWithCorrelation(log, error.Message);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Sql_джерело_з_відмовою_входу_віддає_лише_номер_помилки()
    {
        // Справжній сервер відмовляє справжньому логіну: текст «Login failed for user …».
        var builder = new SqlConnectionStringBuilder(SqlServerFixtureConnection())
        {
            IntegratedSecurity = false,
            UserID = "ecr_no_such_login_db01",
            Password = "Secret123",
            ConnectTimeout = 5,
        };
        var log = new CapturingLogger<SqlDataSource>();
        var adapter = new SqlDataSource(
            StoreWith(ExternalTransport.Sql, builder.ConnectionString),
            Substitute.For<ISecretProvider>(),
            Settings((SqlDataSource.ValueQueryKey, "SELECT 1")),
            log);

        var error = await Assert.ThrowsAsync<SourceAuthenticationException>(
            () => adapter.ReadAsync(
                new CollectionRequest(1, 7, "STREAM-1", Midnight, Midnight.AddHours(1), 10), CancellationToken.None));

        var driverText = Assert.IsType<SqlException>(Assert.Single(log.Entries).Exception).Message;
        Assert.Contains("18456", error.Message, StringComparison.Ordinal);
        AssertSafe(error.Message, driverText);
        Assert.DoesNotContain("ecr_no_such_login", error.Message, StringComparison.Ordinal);
        AssertLoggedWithCorrelation(log, error.Message);
    }

    private static readonly DateTime Midnight = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string SqlServerFixtureConnection()
        => Environment.GetEnvironmentVariable("ECR_TEST_SQL") is { Length: > 0 } fromEnv
            ? fromEnv
            : "Server=localhost;Integrated Security=true;TrustServerCertificate=True";

    private static void AssertSafe(string message, string driverText)
    {
        Assert.DoesNotContain(driverText, message, StringComparison.Ordinal);
        foreach (var secret in Secrets)
        {
            Assert.DoesNotContain(secret, message, StringComparison.Ordinal);
        }

        Assert.Contains("кореляція", message, StringComparison.Ordinal);
    }

    private static void AssertLoggedWithCorrelation<T>(CapturingLogger<T> log, string message)
    {
        var entry = Assert.Single(log.Entries);
        Assert.NotNull(entry.Exception);
        var correlation = entry.Text[(entry.Text.LastIndexOf("кореляція ", StringComparison.Ordinal) + "кореляція ".Length)..].TrimEnd('.');
        Assert.Contains(correlation, message, StringComparison.Ordinal);
    }

    private static ICollectionStore StoreWith(ExternalTransport transport, string endpoint)
    {
        var store = Substitute.For<ICollectionStore>();
        var source = new DataSource(
            EcrCode.Create("AIR"),
            new LocalizedText(new Dictionary<string, string> { ["uk"] = "AIR" }),
            transport,
            endpoint,
            "Source.AIR");
        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(source);
        return store;
    }

    private static ISecretProvider Settings(params (string Key, string Value)[] values)
    {
        var settings = Substitute.For<ISecretProvider>();
        settings.Find(Arg.Any<string>()).Returns((string?)null);
        foreach (var (key, value) in values)
        {
            settings.Find(key).Returns(value);
        }

        return settings;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Text, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
