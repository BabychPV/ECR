// tests/Ecr.Adapters.Tests/SourceCredentialAdapterTests.cs
using System.Data.Odbc;
using Ecr.Adapters.PiAf;
using Ecr.Adapters.Sql;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Microsoft.Data.SqlClient;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests;

/// <summary>
/// Підстановка облікових даних в адаптерах (P-1126-SRCCRED, порція 3): AC-3, AC-5, AC-6b, AC-8, AC-13, AC-2b.
/// </summary>
/// <remarks>
/// Мутації (кожна збирається): прибрати <c>builder["UID"] = …</c> — червоніють AC-3 PiSqlClient; прибрати
/// <c>IntegratedSecurity = false</c> — червоніє AC-3 Sql; прибрати перевірку <c>CredentialsRejectedAt</c> в
/// <c>OpenAsync</c> — червоніє AC-6b; розшифровувати ДО <c>RequireAllowedAddress</c> — червоніє AC-13.
/// </remarks>
public sealed class SourceCredentialAdapterTests
{
    private const string PiEndpoint = "Driver={PI SQL Client};AF Server=af01;AF Database=ECR";

    private readonly string _sentinel = "ZZpwd-" + Guid.NewGuid().ToString("N");

    // ── AC-3 / AC-8: одна точка підстановки ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AC3_PiSqlClient_UserPassword_ставить_UID_PWD_і_прибирає_аліаси_Windows()
    {
        var built = PiSqlClientDataSource.BuildConnectionString(
            PiEndpoint + ";Trusted_Connection=Yes;Integrated Security=SSPI;User Id=old;Persist Security Info=true",
            new ResolvedCredentials("reader", new SecretString(_sentinel)), envSecret: "env-ignored");

        var parsed = new System.Data.Common.DbConnectionStringBuilder(useOdbcRules: true) { ConnectionString = built };
        Assert.Equal("reader", Unbrace(parsed["UID"]));
        Assert.Equal(_sentinel, Unbrace(parsed["PWD"]));
        Assert.False(parsed.ContainsKey("Trusted_Connection"));
        Assert.False(parsed.ContainsKey("Integrated Security"));
        Assert.False(parsed.ContainsKey("User Id"));
        Assert.False(parsed.ContainsKey("Persist Security Info"));
        Assert.DoesNotContain("env-ignored", built, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("user;Server=evil", "p;AF Server=evil")]
    [InlineData("user}", "a}b{c")]
    [InlineData("a=b", "  spaced = pass  ")]
    public void AC8_ін_єкція_в_логін_чи_пароль_не_додає_ключів(string user, string password)
    {
        // Розбір правилами ODBC (як драйвер-менеджер): дужки знімаються, `}}` → `}`.
        var odbc = new System.Data.Common.DbConnectionStringBuilder(useOdbcRules: true)
        {
            ConnectionString = PiSqlClientDataSource.BuildConnectionString(
                PiEndpoint, new ResolvedCredentials(user, new SecretString(password)), null),
        };

        Assert.Equal(user, Unbrace(odbc["UID"]));
        Assert.Equal(password, Unbrace(odbc["PWD"]));
        Assert.Equal("af01", odbc["AF Server"]);
        Assert.Equal(5, odbc.Count);

        var sql = new SqlConnectionStringBuilder(SqlDataSource.BuildConnectionString(
            new SqlConnectionStringBuilder("Server=flert;Database=Vol"), new ResolvedCredentials(user, new SecretString(password)), null));

        Assert.Equal(user, sql.UserID);
        Assert.Equal(password, sql.Password);
        Assert.Equal("flert", sql.DataSource);
    }

    [Fact]
    public void AC3_Sql_UserPassword_вимикає_Integrated_Security_і_Persist_Security_Info()
    {
        var built = new SqlConnectionStringBuilder(SqlDataSource.BuildConnectionString(
            new SqlConnectionStringBuilder("Server=flert;Database=Vol;Integrated Security=true;Persist Security Info=true;UID=old"),
            new ResolvedCredentials("reader", new SecretString(_sentinel)), envSecret: "env-ignored"));

        Assert.False(built.IntegratedSecurity);
        Assert.False(built.PersistSecurityInfo);
        Assert.Equal("reader", built.UserID);
        Assert.Equal(_sentinel, built.Password);
    }

    [Fact]
    public void AC5_Windows_рядок_байтово_той_самий_що_до_змін_разом_із_секретом_середовища()
    {
        // Знімок поведінки до P-1126-SRCCRED: builder(endpoint) [+ PWD/Password = секрет середовища].
        var odbcBefore = new OdbcConnectionStringBuilder(PiEndpoint + ";Integrated Security=SSPI");
        Assert.Equal(odbcBefore.ConnectionString, PiSqlClientDataSource.BuildConnectionString(PiEndpoint + ";Integrated Security=SSPI", null, null));
        odbcBefore["PWD"] = "env";
        Assert.Equal(odbcBefore.ConnectionString, PiSqlClientDataSource.BuildConnectionString(PiEndpoint + ";Integrated Security=SSPI", null, "env"));

        var sqlBefore = new SqlConnectionStringBuilder("Server=flert;Database=Vol;Integrated Security=true");
        Assert.Equal(sqlBefore.ConnectionString, SqlDataSource.BuildConnectionString(new SqlConnectionStringBuilder(sqlBefore.ConnectionString), null, null));
        Assert.DoesNotContain("UID", PiSqlClientDataSource.BuildConnectionString(PiEndpoint, null, null), StringComparison.OrdinalIgnoreCase);
    }

    // ── AC-6b / AC-13 / AC-R: коли розшифровується ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC6b_відхилений_пароль_PiSqlClient_не_розшифровується_і_не_йде_до_драйвера()
    {
        var resolver = new CountingResolver(_sentinel);
        var source = UserPasswordSource(ExternalTransport.PiSqlClient, PiEndpoint, rejected: true);
        var adapter = new PiSqlClientDataSource(StoreWith(source), Substitute.For<ISecretProvider>(), credentials: resolver);

        var error = await Assert.ThrowsAsync<CredentialsRejectedException>(() => adapter.DiscoverAsync(1, CancellationToken.None));

        Assert.Equal(CredentialsRejectedException.MessageKey, error.Details!["messageKey"]);
        Assert.Equal(0, resolver.Resolves);
    }

    [Fact]
    public async Task AC6b_відхилений_пароль_Sql_не_розшифровується_і_не_йде_до_сервера()
    {
        var resolver = new CountingResolver(_sentinel);
        var source = UserPasswordSource(ExternalTransport.Sql, "Server=127.0.0.1,1;Connect Timeout=1", rejected: true);
        var adapter = new SqlDataSource(StoreWith(source), Substitute.For<ISecretProvider>(), credentials: resolver);

        await Assert.ThrowsAsync<CredentialsRejectedException>(() => adapter.CheckConnectionAsync(1, CancellationToken.None));
        Assert.Equal(0, resolver.Resolves);
    }

    [Fact]
    public async Task AC13_відхилена_політикою_адреса_пароля_не_розшифровує()
    {
        var resolver = new CountingResolver(_sentinel);
        var pi = new PiSqlClientDataSource(
            StoreWith(UserPasswordSource(ExternalTransport.PiSqlClient, "Driver={PI SQL Client};Server=169.254.169.254", rejected: false)),
            Substitute.For<ISecretProvider>(), credentials: resolver);
        var sql = new SqlDataSource(
            StoreWith(UserPasswordSource(ExternalTransport.Sql, "Server=169.254.169.254;Connect Timeout=1", rejected: false)),
            Substitute.For<ISecretProvider>(), credentials: resolver);

        var piError = await Assert.ThrowsAsync<BusinessRuleException>(() => pi.DiscoverAsync(1, CancellationToken.None));
        var sqlError = await Assert.ThrowsAsync<SourceNotConfiguredException>(() => sql.CheckConnectionAsync(1, CancellationToken.None));

        Assert.Equal("err.ECR-INT-0503.endpointForbidden", piError.Details!["messageKey"]);
        Assert.Equal("err.ECR-INT-0503.endpointForbidden", sqlError.Details!["messageKey"]);
        Assert.Equal(0, resolver.Resolves);
    }

    // ── AC-2b: безпечний запис винятку ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AC2b_SafeLogException_прибирає_пароль_логін_і_пари_облікових_даних()
    {
        var raw = new InvalidOperationException(
            $"Login failed: Driver={{PI SQL Client}};UID=reader;PWD={_sentinel};AF Server=af01 user reader password {_sentinel} Password = other;x");

        var safe = SafeLogException.Of(raw, "28000", _sentinel, "reader");

        Assert.DoesNotContain(_sentinel, safe.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("reader", safe.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("other", safe.ToString(), StringComparison.Ordinal);
        Assert.Equal("InvalidOperationException", safe.Type);
        Assert.Equal("28000", safe.Code);
        Assert.Contains("AF Server=af01", safe.Message, StringComparison.Ordinal);
    }

    /// <summary>Значення так, як його читає драйвер-менеджер ODBC: без дужок, <c>}}</c> → <c>}</c>.</summary>
    private static string? Unbrace(object? raw)
        => raw is string { Length: >= 2 } text && text[0] == '{' && text[^1] == '}'
            ? text[1..^1].Replace("}}", "}", StringComparison.Ordinal)
            : raw as string;

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static DataSource UserPasswordSource(ExternalTransport transport, string endpoint, bool rejected)
    {
        var source = new DataSource(
            EcrCode.Create("SRC"), new LocalizedText(new Dictionary<string, string> { ["en"] = "S" }),
            transport, endpoint, "DataSource.SRC");
        source.ReplaceCredentials(SourceAuthMode.UserPassword, "reader", [1, 2, 3]);

        if (rejected)
        {
            source.MarkCredentialsRejected(new DateTime(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc));
        }

        return source;
    }

    private static ICollectionStore StoreWith(DataSource source)
    {
        var store = Substitute.For<ICollectionStore>();
        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(source);
        return store;
    }

    /// <summary>Рахує розшифрування.</summary>
    private sealed class CountingResolver(string password) : IDataSourceCredentialResolver
    {
        public int Resolves { get; private set; }

        public byte[] Protect(SecretString password) => throw new NotSupportedException();

        public ResolvedCredentials? Resolve(DataSource source)
        {
            Resolves++;
            return new ResolvedCredentials(source.UserName!, new SecretString(password));
        }
    }
}
