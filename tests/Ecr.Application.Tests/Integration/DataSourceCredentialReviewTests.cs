// tests/Ecr.Application.Tests/Integration/DataSourceCredentialReviewTests.cs
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
/// Знахідки незалежного огляду P-1126-SRCCRED: H1, M1, M2, M3, L1, L6, L7, R-15 (перевірка з форми й збереження).
/// </summary>
/// <remarks>
/// Мутації (кожна збирається): <c>Principal</c> без зняття домену й регістру — червоніє H1; прибрати
/// <c>SourceCredentialRejection.ReportAsync</c> у формі — червоніє M1; прибрати <c>RequireDriverSafe</c> — червоніє M2;
/// прибрати <c>WeakensEncryption</c> — червоніє M3; прибрати <c>catch (CredentialsUnreadableException)</c> — червоніє L1.
/// </remarks>
public sealed class DataSourceCredentialReviewTests
{
    private const string Reason = "Огляд.";
    private const string PiEndpoint = "Driver={PI SQL Client};AF Server=af01;AF Database=ECR";

    private readonly string _sentinel = "ZZpwd-" + Guid.NewGuid().ToString("N");
    private readonly IDataSourceStore _store = Substitute.For<IDataSourceStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IDataSourceCredentialRejections _rejections = Substitute.For<IDataSourceCredentialRejections>();
    private readonly IDataSourceCredentialResolver _resolver = Substitute.For<IDataSourceCredentialResolver>();
    private readonly List<SecurityEventRecord> _events = [];
    private readonly Probe _probe = new();
    private readonly SourceTargetLockout _lockout = new();
    private readonly CapturingLog<TestDataSourceFormHandler> _log = new();
    private readonly TestClock _clock = new(new DateTime(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc));

    public DataSourceCredentialReviewTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Integration.Manage").Build());
        _audit.WriteSecurityEventAsync(Arg.Do<SecurityEventRecord>(_events.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        _resolver.Protect(Arg.Any<SecretString>()).Returns([7, 7]);
        _resolver.Resolve(Arg.Any<DataSource>()).Returns(new ResolvedCredentials("reader", new SecretString("stored")));
    }

    // ── H1 ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("READER", PiEndpoint)]
    [InlineData("CORP\\reader", PiEndpoint)]
    [InlineData("reader@corp.local", PiEndpoint)]
    [InlineData("reader", PiEndpoint + ";Timeout=15")]
    [InlineData("reader", "AF Database=ECR;af server=AF01;Driver={PI SQL Client}")]
    public async Task H1_пауза_цілі_не_обходиться_регістром_доменом_чи_зайвим_ключем(string user, string endpoint)
    {
        _probe.Throw = new SourceAuthenticationException("ECR-INT-0502", "refused");
        for (var i = 0; i < SourceTargetLockout.MaxConsecutiveFailures; i++)
        {
            await Handler().HandleAsync(Form(null, PiEndpoint, "reader", "wrong-" + i), default);
        }

        var paused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Form(null, endpoint, user, "another"), default));

        Assert.Equal(TestDataSourceFormHandler.TargetPausedKey, paused.Details!["messageKey"]);
        Assert.Equal(3, _probe.Calls);
    }

    [Fact]
    public void H1_інший_хост_або_інший_обліковий_запис_це_інша_ціль()
    {
        var baseline = TestDataSourceFormHandler.Fingerprint(ExternalTransport.PiSqlClient, PiEndpoint, null, "reader");

        Assert.NotEqual(baseline, TestDataSourceFormHandler.Fingerprint(ExternalTransport.PiSqlClient, PiEndpoint.Replace("af01", "af02"), null, "reader"));
        Assert.NotEqual(baseline, TestDataSourceFormHandler.Fingerprint(ExternalTransport.PiSqlClient, PiEndpoint, null, "writer"));
    }

    // ── M1 / L1 ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task M1_відмова_входу_збереженим_паролем_з_форми_ставить_мітку_джерелу()
    {
        var stored = Stored();
        _probe.Throw = new SourceAuthenticationException("ECR-INT-0502", "refused");

        var result = await Handler().HandleAsync(Form(5, PiEndpoint, "reader", null), default);

        Assert.Equal("auth", result.Error);
        await _rejections.Received(1).ReportRejectedAsync(Arg.Is<DataSource>(s => ReferenceEquals(s, stored)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task M1_відмова_входу_паролем_з_тіла_мітки_не_ставить()
    {
        Stored();
        _probe.Throw = new SourceAuthenticationException("ECR-INT-0502", "refused");

        await Handler().HandleAsync(Form(5, PiEndpoint, "reader", _sentinel), default);

        await _rejections.DidNotReceiveWithAnyArgs().ReportRejectedAsync(default!, default);
    }

    [Fact]
    public async Task L1_збережений_пароль_не_читається_категорія_і_подія_а_не_502()
    {
        Stored();
        _resolver.Resolve(Arg.Any<DataSource>()).Returns(_ => throw new CredentialsUnreadableException("SRC"));

        var result = await Handler().HandleAsync(Form(5, PiEndpoint, "reader", null), default);

        Assert.Equal("credentialsUnreadable", result.Error);
        Assert.Equal(0, _probe.Calls);
        Assert.Single(_events);
    }

    // ── M2 / M3 / L6 / L7 ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("user;x", "ok", "userName")]
    [InlineData("user}", "ok", "userName")]
    [InlineData("reader", "p{w", "password")]
    [InlineData("reader", "p;AF Server=evil", "password")]
    public async Task M2_PiSqlClient_відхиляє_дужки_й_крапку_з_комою_у_збереженні_і_формі(string user, string password, string field)
    {
        var save = await Assert.ThrowsAsync<BusinessRuleException>(() => Save().CreateAsync(
            "SRC", Name(), ExternalTransport.PiSqlClient, PiEndpoint, null, null, null, null,
            new SourceCredentialsInput(SourceAuthMode.UserPassword, user, new SecretString(password), false), default));
        var form = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Form(null, PiEndpoint, user, password), default));

        Assert.All([save, form], e =>
        {
            Assert.Equal(DataSourceCredentialRules.CredentialsCharactersKey, e.Details!["messageKey"]);
            Assert.Equal(field, e.Details!["field"]);
            Assert.DoesNotContain(password, e.Message, StringComparison.Ordinal);
        });

        // Sql цих символів не обмежує: SqlClient бере значення в лапки сам.
        await Save().CreateAsync(
            "SQL1", Name(), ExternalTransport.Sql, "Server=flert;Database=Vol", null, null, null, null,
            new SourceCredentialsInput(SourceAuthMode.UserPassword, user, new SecretString(password), false), default);
    }

    [Theory]
    [InlineData("Server=flert;Encrypt=false")]
    [InlineData("Server=flert;Encrypt=no")]
    [InlineData("Server=flert;Encrypt=Optional")]
    [InlineData("Server=flert;TrustServerCertificate=true")]
    [InlineData("Server=flert;Trust Server Certificate=yes")]
    public async Task M3_Sql_з_паролем_без_перевіреного_шифрування_422(string endpoint)
    {
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(() => Save().CreateAsync(
            "SRC", Name(), ExternalTransport.Sql, endpoint, null, null, null, null,
            new SourceCredentialsInput(SourceAuthMode.UserPassword, "reader", new SecretString(_sentinel), false), default));

        Assert.Equal(DataSourceCredentialRules.PasswordUnencryptedKey, refused.Details!["messageKey"]);
        Assert.Equal("endpoint", refused.Details!["field"]);
    }

    [Theory]
    [InlineData("Server=flert;Encrypt=Strict")]
    [InlineData("Server=flert;Encrypt=Mandatory;TrustServerCertificate=false")]
    public async Task M3_Sql_із_суворим_шифруванням_проходить(string endpoint)
        => await Save().CreateAsync(
            "SRC", Name(), ExternalTransport.Sql, endpoint, null, null, null, null,
            new SourceCredentialsInput(SourceAuthMode.UserPassword, "reader", new SecretString(_sentinel), false), default);

    [Fact]
    public async Task L6_Authentication_у_рядку_з_паролем_422()
    {
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(() => Save().CreateAsync(
            "SRC", Name(), ExternalTransport.Sql, "Server=flert;Authentication=SqlPassword", null, null, null, null,
            new SourceCredentialsInput(SourceAuthMode.UserPassword, "reader", new SecretString(_sentinel), false), default));

        Assert.Equal(DataSourceCredentialRules.CredentialsInvalidKey, refused.Details!["messageKey"]);
    }

    [Fact]
    public async Task L7_невідомий_режим_входу_422_у_збереженні_і_формі()
    {
        var save = await Assert.ThrowsAsync<BusinessRuleException>(() => Save().CreateAsync(
            "SRC", Name(), ExternalTransport.Sql, "Server=flert", null, null, null, null,
            new SourceCredentialsInput((SourceAuthMode)7, "reader", new SecretString(_sentinel), false), default));
        var form = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            new DataSourceFormTest(null, ExternalTransport.Sql, "Server=flert", null, null, (SourceAuthMode)7, "reader", null, Reason), default));

        Assert.Equal("authMode", save.Details!["field"]);
        Assert.Equal("authMode", form.Details!["field"]);
    }

    // ── R-15: журнал перевірки з неправильним паролем ───────────────────────────────────────────────────────────

    [Fact]
    public async Task R15_перевірка_з_неправильним_паролем_маркер_ні_в_журналі_ні_у_відповіді_ні_в_аудиті()
    {
        _probe.Throw = new SourceAuthenticationException("ECR-INT-0502", $"Login failed: UID=reader;PWD={_sentinel}; {_sentinel}");

        var result = await Handler().HandleAsync(Form(null, PiEndpoint, "reader", _sentinel), default);

        Assert.Equal("auth", result.Error);
        Assert.NotEmpty(_log.Entries);
        Assert.DoesNotContain(_log.Entries, e => e.Contains(_sentinel, StringComparison.Ordinal));
        Assert.DoesNotContain(_sentinel, JsonSerializer.Serialize(result) + string.Join("|", _events.Select(e => e.DetailsJson)), StringComparison.Ordinal);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, string> Name() => new() { ["en"] = "S" };

    private static DataSourceFormTest Form(int? id, string endpoint, string user, string? password)
        => new(id, ExternalTransport.PiSqlClient, endpoint, null, null, SourceAuthMode.UserPassword, user, SecretString.From(password), Reason);

    private SaveDataSourceHandler Save()
        => new(_store, Substitute.For<ISecretProvider>(), _access, _uow, _audit, _user, _clock, credentials: _resolver);

    private TestDataSourceFormHandler Handler()
        => new(_store, [_probe], Save(), new SourceProbeGate(), _lockout, _access, _audit, _user, _clock, _log, _resolver, _rejections);

    private DataSource Stored()
    {
        var source = new DataSource(EcrCode.Create("SRC"), new LocalizedText(Name()), ExternalTransport.PiSqlClient, PiEndpoint, "DataSource.SRC");
        source.ReplaceCredentials(SourceAuthMode.UserPassword, "reader", [1, 2, 3]);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(source, 5);
        _store.FindAsync(5, Arg.Any<CancellationToken>()).Returns(source);
        return source;
    }

    private sealed class Probe : IExternalConnectionProbe
    {
        public int Calls { get; private set; }

        public Exception? Throw { get; set; }

        public ExternalTransport Transport => ExternalTransport.PiSqlClient;

        public Task ProbeAsync(DataSource spec, ResolvedCredentials? credentials, CancellationToken ct)
        {
            Calls++;
            return Throw is { } error ? Task.FromException(error) : Task.CompletedTask;
        }
    }

    private sealed class CapturingLog<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(formatter(state, exception) + " " + exception);
    }
}
