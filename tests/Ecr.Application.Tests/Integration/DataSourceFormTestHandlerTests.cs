// tests/Ecr.Application.Tests/Integration/DataSourceFormTestHandlerTests.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Перевірка з'єднання з урахуванням облікових даних (P-1126-SRCCRED, порція 3): AC-6a..c, AC-11a..e (+Q-15), AC-12,
/// стан облікових даних у переліку.
/// </summary>
/// <remarks>
/// Мутації (кожна збирається): прибрати перевірку <c>CredentialsRejectedAt</c> у перевірці за id — червоніє AC-6b;
/// прибрати <c>SameTarget</c> у <c>Credentials</c> форми — червоніє AC-11b; прибрати <c>RecordFailure</c> — червоніє Q-15;
/// прибрати <c>ResetPools</c> — червоніє AC-12.
/// </remarks>
public sealed class DataSourceFormTestHandlerTests
{
    private const string SqlEndpoint = "Server=flert;Database=Vol";
    private const string Reason = "Перевірка нового логіна.";

    private readonly string _sentinel = "ZZpwd-" + Guid.NewGuid().ToString("N");
    private readonly IDataSourceStore _store = Substitute.For<IDataSourceStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly List<SecurityEventRecord> _events = [];
    private readonly FakeProbe _probe = new();
    private readonly FakeResolver _resolver = new();
    private readonly SourceTargetLockout _lockout = new();
    private readonly TestClock _clock = new(new DateTime(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc));

    public DataSourceFormTestHandlerTests()
    {
        _user.UserId.Returns(9);
        Allow("Integration.Manage");
        _store.CountUsageAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new DataSourceUsage(0, 0));
        _audit.WriteSecurityEventAsync(Arg.Do<SecurityEventRecord>(_events.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
    }

    // ── AC-6 ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AC6a_пароль_не_читається_і_відхилений_мають_свої_категорії_раніше_за_auth()
    {
        Assert.Equal("credentialsUnreadable", TestDataSourceConnectionHandler.Classify(new CredentialsUnreadableException("S")));
        Assert.Equal("credentialsRejected", TestDataSourceConnectionHandler.Classify(new CredentialsRejectedException("S")));
        Assert.Equal("auth", TestDataSourceConnectionHandler.Classify(new SourceAuthenticationException("ECR-INT-0502", "x")));
        Assert.Contains("credentialsUnreadable", TestDataSourceConnectionHandler.FailureCategories);
        Assert.Contains("credentialsRejected", TestDataSourceConnectionHandler.FailureCategories);
    }

    [Fact]
    public async Task AC6b_AC6c_відхилений_пароль_не_доходить_до_адаптера_а_заміна_пароля_відкриває_знову()
    {
        var source = Stored(rejected: true);
        var adapter = new CountingAdapter();
        var byId = new TestDataSourceConnectionHandler(
            _store, [adapter], new SourceProbeGate(), _access, _audit, _user, _clock,
            Substitute.For<ILogger<TestDataSourceConnectionHandler>>());

        var first = await byId.HandleAsync(5, Reason, default);
        Assert.Equal("credentialsRejected", first.Error);
        Assert.Equal(0, adapter.Calls);

        source.ReplaceCredentials(SourceAuthMode.UserPassword, "reader", [9, 9]);
        Assert.Null(source.CredentialsRejectedAt);

        var second = await byId.HandleAsync(5, Reason, default);
        Assert.True(second.Ok);
        Assert.Equal(1, adapter.Calls);
    }

    [Fact]
    public void Перелік_показує_стан_облікових_даних_без_розшифрування()
    {
        Assert.Equal(SourceCredentialsState.Rejected, ListDataSourcesHandler.StateOf(Stored(rejected: true)));
        Assert.Equal(SourceCredentialsState.Ok, ListDataSourcesHandler.StateOf(Stored(rejected: false)));
        Assert.Equal(SourceCredentialsState.Ok, ListDataSourcesHandler.StateOf(Windows()));
        Assert.Equal(0, _resolver.Resolves);
    }

    // ── AC-11 ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ExternalTransport.Sql, "Server=flert;Pwd=x", "err.ECR-REQ-0422.dataSourceEndpointCarriesSecret")]
    [InlineData(ExternalTransport.Sql, "Server=169.254.169.254", "err.ECR-REQ-0422.dataSourceEndpointSqlLinkLocal")]
    [InlineData(ExternalTransport.PiWebApi, "https://pi.corp.example/piwebapi", TestDataSourceFormHandler.TransportKey)]
    public async Task AC11a_та_сама_валідація_що_при_збереженні(ExternalTransport transport, string endpoint, string key)
    {
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Form(null, transport, endpoint, SourceAuthMode.Windows, null, null), default));

        Assert.Equal(key, refused.Details!["messageKey"]);
        Assert.Equal(0, _probe.Calls);
    }

    [Fact]
    public async Task AC11b_пароль_з_тіла_а_збережений_лише_за_тією_самою_ціллю_і_логіном()
    {
        Stored(rejected: false);

        var body = await Handler().HandleAsync(Form(null, ExternalTransport.Sql, SqlEndpoint, SourceAuthMode.UserPassword, "reader", _sentinel), default);
        Assert.True(body.Ok);
        Assert.Equal(_sentinel, _probe.LastCredentials!.Password.Reveal());
        Assert.Equal(0, _resolver.Resolves);

        var same = await Handler().HandleAsync(Form(5, ExternalTransport.Sql, "database=Vol; server=flert", SourceAuthMode.UserPassword, "reader", null), default);
        Assert.True(same.Ok);
        Assert.Equal(1, _resolver.Resolves);

        var moved = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            Form(5, ExternalTransport.Sql, "Server=evil;Database=Vol", SourceAuthMode.UserPassword, "reader", null), default));
        Assert.Equal(DataSourceCredentialRules.PasswordReentryRequiredKey, moved.Details!["messageKey"]);

        var otherUser = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            Form(5, ExternalTransport.Sql, SqlEndpoint, SourceAuthMode.UserPassword, "admin", null), default));
        Assert.Equal(DataSourceCredentialRules.PasswordReentryRequiredKey, otherUser.Details!["messageKey"]);
        Assert.Equal(1, _resolver.Resolves);
    }

    [Fact]
    public async Task AC11c_без_Manage_403_і_без_пароля_у_відмові()
    {
        Allow("Integration.View");

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => Handler().HandleAsync(
            Form(null, ExternalTransport.Sql, SqlEndpoint, SourceAuthMode.UserPassword, "reader", _sentinel), default));

        Assert.DoesNotContain(_sentinel, denied.Message + JsonSerializer.Serialize(denied.Details), StringComparison.Ordinal);
        Assert.Equal(0, _probe.Calls);
    }

    [Fact]
    public async Task AC11e_журнал_unsaved_без_адреси_і_пароля_відповідь_лише_категорія()
    {
        _probe.Throw = new SourceAuthenticationException("ECR-INT-0502", "Login failed for user reader " + _sentinel);

        var result = await Handler().HandleAsync(
            Form(null, ExternalTransport.Sql, SqlEndpoint, SourceAuthMode.UserPassword, "reader", _sentinel), default);

        Assert.False(result.Ok);
        Assert.Equal("auth", result.Error);
        Assert.Equal(new DataSourceTestResult(false, "auth", 0, "integration.test.failed.auth"), result);

        var evt = Assert.Single(_events);
        Assert.Equal(TestDataSourceConnectionHandler.EventType, evt.EventType);
        Assert.Contains("\"unsaved\":true", evt.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("flert", evt.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(_sentinel, evt.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(_sentinel, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Q15_три_відмови_входу_поспіль_ставлять_ціль_на_паузу_15_хвилин()
    {
        _probe.Throw = new SourceAuthenticationException("ECR-INT-0502", "refused");
        var form = Form(null, ExternalTransport.Sql, SqlEndpoint, SourceAuthMode.UserPassword, "reader", _sentinel);

        for (var i = 0; i < SourceTargetLockout.MaxConsecutiveFailures; i++)
        {
            Assert.Equal("auth", (await Handler().HandleAsync(form, default)).Error);
        }

        var paused = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(form, default));
        Assert.Equal("ECR-REQ-0429", paused.ErrorCode);
        Assert.Equal(TestDataSourceFormHandler.TargetPausedKey, paused.Details!["messageKey"]);
        Assert.Equal(3, _probe.Calls);

        _clock.Advance(SourceTargetLockout.Pause + TimeSpan.FromSeconds(1));
        _probe.Throw = null;
        Assert.True((await Handler().HandleAsync(form, default)).Ok);
    }

    // ── AC-12 ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC12_заміна_облікових_даних_скидає_пули_а_збереження_без_зміни_ні()
    {
        var source = Stored(rejected: false);
        var pool = Substitute.For<IConnectionPoolReset>();
        var save = new SaveDataSourceHandler(
            _store, Substitute.For<ISecretProvider>(), _access, _uow, _audit, _user, _clock,
            credentials: _resolver, pools: [pool]);

        await save.UpdateAsync(
            5, Name(), ExternalTransport.Sql, SqlEndpoint, null, null, null, true, "\"AQI=\"", null, false,
            SourceCredentialsInput.Unchanged, default);
        pool.DidNotReceive().Reset();

        await save.UpdateAsync(
            5, Name(), ExternalTransport.Sql, SqlEndpoint, null, null, null, true, "\"AQI=\"", null, false,
            new SourceCredentialsInput(null, null, new SecretString(_sentinel), false), default);
        pool.Received(1).Reset();
        Assert.Equal(SourceAuthMode.UserPassword, source.AuthMode);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, string> Name() => new() { ["en"] = "S" };

    private static DataSourceFormTest Form(
        int? id, ExternalTransport transport, string endpoint, SourceAuthMode mode, string? user, string? password)
        => new(id, transport, endpoint, null, null, mode, user, SecretString.From(password), Reason);

    private TestDataSourceFormHandler Handler()
        => new(
            _store, [_probe],
            new SaveDataSourceHandler(_store, Substitute.For<ISecretProvider>(), _access, _uow, _audit, _user, _clock),
            new SourceProbeGate(), _lockout, _access, _audit, _user, _clock,
            Substitute.For<ILogger<TestDataSourceFormHandler>>(), _resolver);

    private void Allow(params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = 9 };
        foreach (var permission in permissions)
        {
            builder = builder.Permission(permission);
        }

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    private DataSource Stored(bool rejected)
    {
        var source = Windows();
        source.ReplaceCredentials(SourceAuthMode.UserPassword, "reader", [1, 2, 3]);
        if (rejected)
        {
            source.MarkCredentialsRejected(_clock.UtcNow);
        }

        return source;
    }

    private DataSource Windows()
    {
        var source = new DataSource(EcrCode.Create("SRC"), new LocalizedText(Name()), ExternalTransport.Sql, SqlEndpoint, "DataSource.SRC");
        source.Configure(null, null, 4);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(source, 5);
        typeof(DataSource).GetProperty(nameof(DataSource.RowVersion))!.SetValue(source, new byte[] { 1, 2 });
        _store.FindAsync(5, Arg.Any<CancellationToken>()).Returns(source);
        return source;
    }

    private sealed class FakeProbe : IExternalConnectionProbe
    {
        public int Calls { get; private set; }

        public ResolvedCredentials? LastCredentials { get; private set; }

        public Exception? Throw { get; set; }

        public ExternalTransport Transport => ExternalTransport.Sql;

        public Task ProbeAsync(DataSource spec, ResolvedCredentials? credentials, CancellationToken ct)
        {
            Calls++;
            LastCredentials = credentials;
            return Throw is { } error ? Task.FromException(error) : Task.CompletedTask;
        }
    }

    private sealed class FakeResolver : IDataSourceCredentialResolver
    {
        public int Resolves { get; private set; }

        public byte[] Protect(SecretString password) => [7, 7];

        public ResolvedCredentials? Resolve(DataSource source)
        {
            Resolves++;
            return new ResolvedCredentials(source.UserName!, new SecretString("stored-password"));
        }
    }

    private sealed class CountingAdapter : IExternalDataSource
    {
        public int Calls { get; private set; }

        public ExternalTransport Transport => ExternalTransport.Sql;

        public Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<SourceEntityDescriptor>>([]);
        }

        public Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
