// tests/Ecr.Adapters.Tests/SourceCredentialFailureTests.cs
using Ecr.Adapters.PiAf;
using Ecr.Adapters.Sql;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Ecr.Adapters.Tests;

/// <summary>
/// Невдале відкриття з'єднання з логіном і паролем (P-1126-SRCCRED, огляд: H2, AC-6b, L2, L4, L10, R-15).
/// </summary>
/// <remarks>
/// ⚠ <c>OdbcException</c>/<c>SqlException</c> ззовні збірки не створити, тому перевіряється шов <c>FailOpenAsync</c> —
/// той самий метод, яким закінчується справжній <c>catch</c> адаптера.
/// Мутації (кожна збирається): прибрати виклик <c>SourceCredentialRejection.ReportAsync</c> у <c>FailOpenAsync</c>
/// PiSqlClient / Sql — червоніють AC-6b відповідного адаптера; <c>Other → Transport</c> у <c>ClassifyOpenFailure</c> —
/// червоніє H2; прибрати 18486 зі списку Sql — червоніє H2 Sql; прибрати форму в дужках зі скрабера — червоніє L4.
/// </remarks>
public sealed class SourceCredentialFailureTests
{
    private readonly string _sentinel = "ZZpwd-" + Guid.NewGuid().ToString("N") + "}x";
    private readonly IDataSourceCredentialRejections _rejections = Substitute.For<IDataSourceCredentialRejections>();
    private readonly CapturingLogger<PiSqlClientDataSource> _piLog = new();
    private readonly CapturingLogger<SqlDataSource> _sqlLog = new();

    // ── H2 ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("28000", false, "Authentication")]
    [InlineData("28000", true, "Authentication")]
    [InlineData("IM002", false, "Transport")]
    [InlineData("IM002", true, "Other")]
    [InlineData("42000", true, "Other")]
    [InlineData("08S01", true, "Transport")]
    [InlineData("08001", true, "Transport")]
    [InlineData("HYT00", true, "Transport")]
    [InlineData("HYT01", true, "Transport")]
    public void H2_PiSqlClient_з_логіном_повторює_лише_транспортні_SQLSTATE(string state, bool userPassword, string expected)
        => Assert.Equal(expected, PiSqlClientDataSource.ClassifyOpenFailure([state], userPassword).ToString());

    [Fact]
    public async Task H2_невідомий_SQLSTATE_з_логіном_не_перемикає_на_запасну_адресу()
    {
        var error = await Adapter().FailOpenAsync(Source(ExternalTransport.PiSqlClient), true, true, ["IM002"], new InvalidOperationException("x"), []);

        Assert.Equal("err.ECR-INT-0503.connectFailedNoRetry", ((BusinessRuleException)error).Details!["messageKey"]);
        Assert.False(PiSqlClientDataSource.IsConnectFailure(error));
        await _rejections.DidNotReceiveWithAnyArgs().ReportRejectedAsync(default!, default);
    }

    [Theory]
    [InlineData(18456, true)]
    [InlineData(18452, true)]
    [InlineData(18470, true)]
    [InlineData(18486, true)]
    [InlineData(18487, true)]
    [InlineData(18488, true)]
    [InlineData(4060, false)]
    [InlineData(53, false)]
    public void H2_Sql_відмови_входу_включно_із_заблокованим_і_протермінованим(int number, bool auth)
        => Assert.Equal(auth, SqlDataSource.IsAuthenticationNumber(number));

    // ── AC-6b / L2 ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AC6b_перша_відмова_28000_збереженим_паролем_ставить_мітку_в_базі_й_на_сутності()
    {
        var source = Source(ExternalTransport.PiSqlClient);

        var error = await Adapter().FailOpenAsync(source, true, true, ["28000"], new InvalidOperationException("denied"), []);

        Assert.IsType<SourceAuthenticationException>(error);
        await _rejections.Received(1).ReportRejectedAsync(Arg.Is<DataSource>(s => ReferenceEquals(s, source)), Arg.Any<CancellationToken>());
        Assert.True(source.CredentialsRejectedInProcess);
        Assert.Null(source.CredentialsRejectedAt);
    }

    [Fact]
    public async Task AC6b_перша_відмова_18456_збереженим_паролем_ставить_мітку_в_базі_й_на_сутності()
    {
        var source = Source(ExternalTransport.Sql);

        var error = await SqlAdapter().FailOpenAsync(source, true, true, [18456], 18456, new InvalidOperationException("denied"), []);

        Assert.IsType<SourceAuthenticationException>(error);
        await _rejections.Received(1).ReportRejectedAsync(Arg.Is<DataSource>(s => ReferenceEquals(s, source)), Arg.Any<CancellationToken>());
        Assert.True(source.CredentialsRejectedInProcess);
        Assert.Null(source.CredentialsRejectedAt);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task AC6b_перевірка_з_форми_або_Windows_мітки_не_ставлять(bool stored, bool userPassword)
    {
        var source = Source(ExternalTransport.PiSqlClient);

        await Adapter().FailOpenAsync(source, stored, userPassword, ["28000"], new InvalidOperationException("denied"), []);
        await SqlAdapter().FailOpenAsync(source, stored, userPassword, [18456], 18456, new InvalidOperationException("denied"), []);

        await _rejections.DidNotReceiveWithAnyArgs().ReportRejectedAsync(default!, default);
        Assert.False(source.CredentialsRejectedInProcess);
    }

    [Fact]
    public async Task L2_після_відмови_повторне_взяття_зєднання_в_тому_ж_прогоні_до_драйвера_не_йде()
    {
        var source = Source(ExternalTransport.PiSqlClient);
        var store = Substitute.For<ICollectionStore>();
        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(source);
        var resolver = Substitute.For<IDataSourceCredentialResolver>();
        var adapter = new PiSqlClientDataSource(
            store, Substitute.For<ISecretProvider>(), logger: _piLog, credentials: resolver, rejections: _rejections);

        await adapter.FailOpenAsync(source, true, true, ["28000"], new InvalidOperationException("denied"), []);

        await Assert.ThrowsAsync<CredentialsRejectedException>(() => adapter.DiscoverAsync(1, CancellationToken.None));
        resolver.DidNotReceiveWithAnyArgs().Resolve(default!);
    }

    // ── L10 ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task L10_збій_запису_мітки_журналюється_безпечно_а_назовні_та_сама_відмова_входу()
    {
        _rejections.ReportRejectedAsync(Arg.Any<DataSource>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db down PWD=" + _sentinel));
        var source = Source(ExternalTransport.Sql);

        var error = await SqlAdapter().FailOpenAsync(source, true, true, [18456], 18456, new InvalidOperationException("denied"), [_sentinel]);

        Assert.IsType<SourceAuthenticationException>(error);
        Assert.True(source.CredentialsRejectedInProcess);
        Assert.Null(source.CredentialsRejectedAt);
        Assert.Contains(_sqlLog.Entries, e => e.Level == LogLevel.Error && e.Exception is null);
        Assert.DoesNotContain(_sqlLog.Entries, e => e.Text.Contains(_sentinel, StringComparison.Ordinal));
    }

    // ── R-15 / L4 ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task R15_збір_із_неправильним_паролем_маркер_ні_в_журналі_ні_в_тексті_відмови()
    {
        var braced = "{" + _sentinel.Replace("}", "}}", StringComparison.Ordinal) + "}";
        var raw = new InvalidOperationException($"driver: UID=reader;PWD={braced}; plain {_sentinel}; escaped {braced}");

        var pi = await Adapter().FailOpenAsync(Source(ExternalTransport.PiSqlClient), true, true, ["28000"], raw, [_sentinel, "reader"]);
        var sql = await SqlAdapter().FailOpenAsync(Source(ExternalTransport.Sql), true, true, [18456], 18456, raw, [_sentinel, "reader"]);

        // Текст відмови — це й itg.CollectionRun.ErrorMessage, і ProblemDetails.
        Assert.DoesNotContain(_sentinel, pi.Message + sql.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_piLog.Entries.Concat(_sqlLog.Entries), e => e.Text.Contains(_sentinel, StringComparison.Ordinal)
                                                                           || e.Text.Contains(braced, StringComparison.Ordinal)
                                                                           || e.Exception is not null);
    }

    [Fact]
    public void L4_скрабер_прибирає_і_форму_в_фігурних_дужках_з_подвоєною_дужкою()
    {
        var escaped = _sentinel.Replace("}", "}}", StringComparison.Ordinal);

        var safe = SafeLogException.Scrub($"PWD={{{escaped}}} also {escaped}", _sentinel);

        Assert.DoesNotContain("ZZpwd-", safe, StringComparison.Ordinal);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    private PiSqlClientDataSource Adapter()
        => new(Substitute.For<ICollectionStore>(), Substitute.For<ISecretProvider>(), logger: _piLog, rejections: _rejections);

    private SqlDataSource SqlAdapter()
        => new(Substitute.For<ICollectionStore>(), Substitute.For<ISecretProvider>(), logger: _sqlLog, rejections: _rejections);

    private static DataSource Source(ExternalTransport transport)
    {
        var source = new DataSource(
            EcrCode.Create("SRC"), new LocalizedText(new Dictionary<string, string> { ["en"] = "S" }),
            transport, "Server=db", "DataSource.SRC");
        source.ReplaceCredentials(SourceAuthMode.UserPassword, "reader", [1, 2, 3]);
        return source;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Text, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
