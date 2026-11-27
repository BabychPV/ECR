// tests/Ecr.Application.Tests/Integration/DataSourceCredentialHandlerTests.cs
using System.Text;
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
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Облікові дані джерела у збереженні (P-1126-SRCCRED, порція 2): AC-2, AC-2a, AC-4, AC-5b, AC-7, AC-8, AC-9, AC-10, AC-15.
/// </summary>
/// <remarks>
/// Мутації (кожна збирається): прибрати перевірку <c>targetChanged || userName</c> у <c>Decide</c> — червоніють тести AC-7;
/// прибрати <c>AuditCredentialsAsync</c> з <c>UpdateAsync</c> — червоніє AC-2a; повернути Windows-гілку S3 для
/// UserPassword — червоніє AC-5b; прибрати перевірку транспорту — червоніє AC-9.
/// </remarks>
public sealed class DataSourceCredentialHandlerTests
{
    private const string PiEndpoint = "Driver={PI SQL Client};AF Server=af01;AF Database=ECR";
    private const string SqlEndpoint = "Server=flert;Database=Vol";
    private const string Version = "\"AQI=\"";

    private static readonly Dictionary<string, string> Name = new() { ["en"] = "Source" };

    /// <summary>Маркер-пароль: випадковий, щоб збіг ніде не був випадковим.</summary>
    private readonly string _sentinel = "ZZpwd-" + Guid.NewGuid().ToString("N");

    private readonly IDataSourceStore _store = Substitute.For<IDataSourceStore>();
    private readonly ISecretProvider _secrets = Substitute.For<ISecretProvider>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly FakeProtector _protector = new();
    private readonly List<SecurityEventRecord> _events = [];
    private readonly List<StructureChangeRecord> _changes = [];

    public DataSourceCredentialHandlerTests()
    {
        _user.UserId.Returns(9);
        Allow("Integration.Manage");
        _store.CountUsageAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new DataSourceUsage(0, 0));
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        _audit.WriteSecurityEventAsync(Arg.Do<SecurityEventRecord>(_events.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _audit.WriteStructureChangeAsync(Arg.Do<StructureChangeRecord>(_changes.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    // ── AC-2 / AC-2a ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC2_створення_з_паролем_ніде_не_лишає_значення_ні_довжини()
    {
        var view = await Create(ExternalTransport.PiSqlClient, PiEndpoint, Up("reader", _sentinel));

        Assert.Equal(SourceAuthMode.UserPassword, view.AuthMode);
        Assert.Equal("reader", view.UserName);
        Assert.True(view.HasPassword);

        var added = _store.ReceivedCalls().Select(c => c.GetArguments().FirstOrDefault()).OfType<DataSource>().Single();
        Assert.NotEqual(Encoding.UTF8.GetBytes(_sentinel), added.PasswordProtected);
        Assert.Equal(_sentinel, _protector.Unprotect(added.PasswordProtected!));

        var outputs = string.Join(
            "\n",
            JsonSerializer.Serialize(view),
            string.Join("\n", _events.Select(e => e.DetailsJson)),
            string.Join("\n", _changes.Select(c => c.OldJson + c.NewJson)));
        Assert.DoesNotContain(_sentinel, outputs, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(added.PasswordProtected!), outputs, StringComparison.Ordinal);

        var changed = Assert.Single(_events, e => e.EventType == SaveDataSourceHandler.CredentialEventType);
        Assert.Contains("\"passwordChanged\":true", changed.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"hasPassword\":true", _changes.Single().NewJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC2a_заміна_пароля_при_незмінному_знімку_пише_рівно_одну_подію()
    {
        var source = Existing(ExternalTransport.PiSqlClient, PiEndpoint, "reader", "old-password");
        _events.Clear();

        await Update(source, ExternalTransport.PiSqlClient, PiEndpoint, new SourceCredentialsInput(null, null, new SecretString(_sentinel), false));

        // Знімок той самий (hasPassword true → true), тож структурного запису немає, а подія безпеки — є.
        Assert.Empty(_changes);
        var evt = Assert.Single(_events, e => e.EventType == SaveDataSourceHandler.CredentialEventType);
        Assert.Contains("\"passwordChanged\":true", evt.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(_sentinel, evt.DetailsJson, StringComparison.Ordinal);
        Assert.Equal(_sentinel, _protector.Unprotect(source.PasswordProtected!));
    }

    [Fact]
    public async Task AC2a_перехід_на_Windows_стирає_логін_і_пароль_і_пише_подію()
    {
        var source = Existing(ExternalTransport.Sql, SqlEndpoint, "reader", "old-password");

        await Update(source, ExternalTransport.Sql, SqlEndpoint, new SourceCredentialsInput(SourceAuthMode.Windows, null, null, true));

        Assert.Equal(SourceAuthMode.Windows, source.AuthMode);
        Assert.Null(source.UserName);
        Assert.Null(source.PasswordProtected);
        var evt = Assert.Single(_events, e => e.EventType == SaveDataSourceHandler.CredentialEventType);
        Assert.Contains("\"passwordCleared\":true", evt.DetailsJson, StringComparison.Ordinal);
    }

    // ── AC-4 ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ExternalTransport.PiSqlClient, "Driver={PI SQL Client};AF Server=af;userid=svc")]
    [InlineData(ExternalTransport.PiSqlClient, "Driver={PI SQL Client};AF Server=af;UID\t=svc")]
    [InlineData(ExternalTransport.PiSqlClient, "Driver={PI SQL Client};AF Server=af;User Name=svc")]
    [InlineData(ExternalTransport.Sql, "Server=flert;User Id=svc")]
    [InlineData(ExternalTransport.Sql, "Server=flert;PWD=x")]
    public void AC4_ключ_облікових_даних_у_рядку_відхиляється_за_іменем(ExternalTransport transport, string endpoint)
    {
        var refused = Assert.Throws<BusinessRuleException>(
            () => SaveDataSourceHandler.RequireNoCredentials(transport, endpoint, "endpoint"));

        Assert.Equal("err.ECR-REQ-0422.dataSourceEndpointCarriesSecret", refused.Details!["messageKey"]);
        Assert.DoesNotContain("svc", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExternalTransport.PiSqlClient, "Driver={PI SQL Client};AF Server=af;guid=1;superuser=x")]
    [InlineData(ExternalTransport.Sql, "Server=flert;Database=Vol;Integrated Security=true")]
    public void AC4_guid_і_superuser_не_облікові_дані(ExternalTransport transport, string endpoint)
        => SaveDataSourceHandler.RequireNoCredentials(transport, endpoint, "endpoint");

    // ── AC-5b ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC5b_UserPassword_ігнорує_секрет_середовища_на_створенні_і_зміні_адреси()
    {
        _secrets.Find("DataSource.SRC").Returns("env-secret-value");

        // Без secretConfirmation: секрет середовища для UserPassword не діє.
        await Create(ExternalTransport.Sql, SqlEndpoint, Up("reader", _sentinel));

        var source = Existing(ExternalTransport.Sql, SqlEndpoint, "reader", "old");
        await Update(source, ExternalTransport.Sql, "Server=flert2;Database=Vol", new SourceCredentialsInput(null, null, new SecretString(_sentinel), false));

        Assert.Equal("Server=flert2;Database=Vol", source.Endpoint);
    }

    // ── AC-7 ────────────────────────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<ExternalTransport, string, string?, string?> NewTargets => new()
    {
        { ExternalTransport.PiSqlClient, "Driver={PI SQL Client};AF Server=evil;AF Database=ECR", null, null },
        { ExternalTransport.PiSqlClient, PiEndpoint, "Driver={PI SQL Client};AF Server=evil", null },
        { ExternalTransport.Sql, "Server=af01;Database=ECR", null, null },
        { ExternalTransport.PiSqlClient, PiEndpoint, null, "other-user" },
    };

    [Theory]
    [MemberData(nameof(NewTargets))]
    public async Task AC7_нова_ціль_або_логін_без_пароля_422_і_нічого_не_змінено(
        ExternalTransport transport, string endpoint, string? secondary, string? userName)
    {
        // Секрет середовища заданий: відмова reentry мусить прийти РАНІШЕ за S3 (secretReentryRequired).
        _secrets.Find("DataSource.SRC").Returns("env-secret-value");
        var source = Existing(ExternalTransport.PiSqlClient, PiEndpoint, "reader", "old-password");
        var blob = source.PasswordProtected;

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(() => Update(
            source, transport, endpoint, new SourceCredentialsInput(null, userName, null, false), secondary));

        Assert.Equal(DataSourceCredentialRules.PasswordReentryRequiredKey, refused.Details!["messageKey"]);
        Assert.Equal("password", refused.Details!["field"]);
        Assert.Same(blob, source.PasswordProtected);
        Assert.Equal(PiEndpoint, source.Endpoint);
        Assert.Equal("reader", source.UserName);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task AC7_та_сама_ціль_інший_регістр_і_порядок_ключів_без_повторного_введення()
    {
        var source = Existing(ExternalTransport.PiSqlClient, PiEndpoint, "reader", "old-password");

        await Update(
            source, ExternalTransport.PiSqlClient, "af database=ECR;  AF SERVER=af01;Driver={PI SQL Client}",
            SourceCredentialsInput.Unchanged);

        Assert.Equal(SourceAuthMode.UserPassword, source.AuthMode);
        Assert.DoesNotContain(_events, e => e.EventType == SaveDataSourceHandler.CredentialEventType);
    }

    [Fact]
    public void AC7_диференціал_ODBC_фігурні_дужки_з_крапкою_з_комою_це_нова_ціль()
    {
        const string a = "AF Server={x;Server=evil}";
        const string b = "Server=evil};AF Server={x";

        // Загальний розбір бачить ці рядки однаково (саме так було до R-10) …
        Assert.Equal(DataSourceAddress.Canonical(a), DataSourceAddress.Canonical(b));

        // … а ODBC-правила PiSqlClient — ні: ціль змінилася.
        Assert.False(DataSourceAddress.SameTarget(
            ExternalTransport.PiSqlClient, a, null, ExternalTransport.PiSqlClient, b, null));
    }

    // ── AC-8 ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("user\u0001x", "ok-password", "userName")]
    [InlineData("user\u007F", "ok-password", "userName")]
    [InlineData("reader", "pass\0word", "password")]
    [InlineData("", "ok-password", "userName")]
    public async Task AC8_керівні_символи_і_NUL_відхиляються_без_повтору(string userName, string password, string field)
    {
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Create(ExternalTransport.Sql, SqlEndpoint, Up(userName, password)));

        Assert.Equal(DataSourceCredentialRules.CredentialsInvalidKey, refused.Details!["messageKey"]);
        Assert.Equal(field, refused.Details!["field"]);
        Assert.DoesNotContain(password, refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, _protector.Calls);
    }

    [Fact]
    public async Task AC8_довжина_256_і_пароль_без_Trim()
    {
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Create(ExternalTransport.Sql, SqlEndpoint, Up(new string('u', 257), "p")));
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Create(ExternalTransport.Sql, SqlEndpoint, Up("reader", new string('p', 257))));

        // Символи `;`, `}`, `=` у логіні — дозволені тут; рядок драйвера будує builder (AC-3, порція 3).
        await Create(ExternalTransport.Sql, SqlEndpoint, Up("user;Server=evil}", "  spaced pass  "));
        Assert.Equal("  spaced pass  ", _protector.Last);
    }

    // ── AC-9 ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC9_UserPassword_на_PiWebApi_422_і_на_створенні_і_на_зміні_транспорту()
    {
        var created = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Create(ExternalTransport.PiWebApi, "https://pi.corp.example/piwebapi", Up("reader", _sentinel)));
        Assert.Equal(DataSourceCredentialRules.CredentialsTransportKey, created.Details!["messageKey"]);

        var source = Existing(ExternalTransport.Sql, SqlEndpoint, "reader", "old");
        var moved = await Assert.ThrowsAsync<BusinessRuleException>(() => Update(
            source, ExternalTransport.PiWebApi, "https://pi.corp.example/piwebapi",
            new SourceCredentialsInput(null, null, new SecretString(_sentinel), false)));
        Assert.Equal(DataSourceCredentialRules.CredentialsTransportKey, moved.Details!["messageKey"]);
        Assert.Equal(ExternalTransport.Sql, source.Transport);
    }

    [Fact]
    public async Task R06_UserPassword_з_Integrated_Security_у_рядку_422()
    {
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Create(ExternalTransport.Sql, "Server=flert;Integrated Security=true", Up("reader", _sentinel)));

        Assert.Equal("endpoint", refused.Details!["field"]);
        Assert.Equal(DataSourceCredentialRules.CredentialsInvalidKey, refused.Details!["messageKey"]);
    }

    // ── AC-10 ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC10_PUT_старого_клієнта_не_змінює_облікових_даних()
    {
        var source = Existing(ExternalTransport.Sql, SqlEndpoint, "reader", "old");
        var blob = source.PasswordProtected;

        await Update(source, ExternalTransport.Sql, SqlEndpoint, SourceCredentialsInput.Unchanged);

        Assert.Equal(SourceAuthMode.UserPassword, source.AuthMode);
        Assert.Equal("reader", source.UserName);
        Assert.Same(blob, source.PasswordProtected);
        Assert.DoesNotContain(_events, e => e.EventType == SaveDataSourceHandler.CredentialEventType);
    }

    [Fact]
    public async Task AC10_ClearPassword_без_переходу_на_Windows_422_а_Windows_в_UserPassword_без_пароля_422()
    {
        var source = Existing(ExternalTransport.Sql, SqlEndpoint, "reader", "old");
        var clear = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Update(source, ExternalTransport.Sql, SqlEndpoint, new SourceCredentialsInput(null, null, null, true)));
        Assert.Equal("clearPassword", clear.Details!["field"]);

        var windows = Existing(ExternalTransport.Sql, SqlEndpoint, null, null);
        var missing = await Assert.ThrowsAsync<BusinessRuleException>(() => Update(
            windows, ExternalTransport.Sql, SqlEndpoint, new SourceCredentialsInput(SourceAuthMode.UserPassword, "reader", null, false)));
        Assert.Equal("password", missing.Details!["field"]);
        Assert.Equal(SourceAuthMode.Windows, windows.AuthMode);
    }

    // ── AC-15 ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC15_View_бачить_ознаки_а_PUT_з_паролем_без_Manage_403_до_шифрування()
    {
        var source = Existing(ExternalTransport.Sql, SqlEndpoint, "reader", "old");
        _store.ListAsync(Arg.Any<CancellationToken>()).Returns([new DataSourceRow(source, new DataSourceUsage(0, 0))]);
        Allow("Integration.View");

        var row = Assert.Single(await new ListDataSourcesHandler(_store, _secrets, _access, _user).HandleAsync(default));
        Assert.Equal(SourceAuthMode.UserPassword, row.AuthMode);
        Assert.Equal("reader", row.UserName);
        Assert.True(row.HasPassword);

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => Update(
            source, ExternalTransport.Sql, SqlEndpoint, new SourceCredentialsInput(null, null, new SecretString(_sentinel), false)));
        Assert.DoesNotContain(_sentinel, denied.Message + JsonSerializer.Serialize(denied.Details), StringComparison.Ordinal);
        Assert.Equal(0, _protector.Calls);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static SourceCredentialsInput Up(string userName, string password)
        => new(SourceAuthMode.UserPassword, userName, SecretString.From(password), false);

    private void Allow(params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = 9 };
        foreach (var permission in permissions)
        {
            builder = builder.Permission(permission);
        }

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    private SaveDataSourceHandler Handler()
        => new(_store, _secrets, _access, _uow, _audit, _user, new TestClock(new DateTime(2026, 11, 26, 9, 0, 0)),
            credentials: _protector);

    private Task<DataSourceView> Create(ExternalTransport transport, string endpoint, SourceCredentialsInput input)
        => Handler().CreateAsync("SRC", Name, transport, endpoint, null, null, null, null, input, default);

    private Task<DataSourceView> Update(
        DataSource source, ExternalTransport transport, string endpoint, SourceCredentialsInput input, string? secondary = null)
        => Handler().UpdateAsync(5, Name, transport, endpoint, secondary, null, null, true, Version, null, true, input, default);

    /// <summary>Збережене джерело з id 5; <paramref name="userName"/> <c>null</c> — Windows.</summary>
    private DataSource Existing(ExternalTransport transport, string endpoint, string? userName, string? password)
    {
        var source = new DataSource(EcrCode.Create("SRC"), new LocalizedText(Name), transport, endpoint, "DataSource.SRC");
        source.Configure(null, null, 4);
        typeof(DataSource).GetProperty(nameof(DataSource.RowVersion))!.SetValue(source, new byte[] { 1, 2 });

        if (userName is not null)
        {
            source.ReplaceCredentials(SourceAuthMode.UserPassword, userName, _protector.Protect(new SecretString(password!)));
        }

        _protector.Calls = 0;
        _store.FindAsync(5, Arg.Any<CancellationToken>()).Returns(source);
        return source;
    }

    /// <summary>Шифрувальник-подвійник: оборотний, але блоб не містить відкритого тексту.</summary>
    private sealed class FakeProtector : IDataSourceCredentialResolver
    {
        public int Calls { get; set; }

        public string? Last { get; private set; }

        public byte[] Protect(SecretString password)
        {
            Calls++;
            Last = password.Reveal();
            return [0xEC, .. Encoding.UTF8.GetBytes(password.Reveal()).Select(b => (byte)(b ^ 0x5A))];
        }

        public string Unprotect(byte[] blob) => Encoding.UTF8.GetString([.. blob.Skip(1).Select(b => (byte)(b ^ 0x5A))]);

        public ResolvedCredentials? Resolve(DataSource source) => throw new NotSupportedException();
    }
}
