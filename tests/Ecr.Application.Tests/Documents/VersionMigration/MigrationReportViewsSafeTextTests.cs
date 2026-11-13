// tests/Ecr.Application.Tests/Documents/VersionMigration/MigrationReportViewsSafeTextTests.cs
using System.Data.Common;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Ecr.Application.Tests.Documents.VersionMigration;

/// <summary>
/// F13 RC #872, delta P2 (SEC, TIER2): збій постановки перегенерації вʼюх після переносу пишеться на картку
/// <c>reportviews</c>, а її читає АНОНІМНИЙ <c>/health/ready</c>. Сирий текст винятку туди не йде — лише фіксований
/// англійський текст із кореляцією; повний виняток — у журнал (Warning) під тією самою кореляцією.
/// </summary>
/// <remarks>
/// Червоне до фіксу (повернути <c>"…not queued: " + ex.Message</c> і код <c>0</c>): картка містить секрет з
/// повідомлення винятку, кореляції в ній немає, код <c>0</c>.
/// </remarks>
public sealed class MigrationReportViewsSafeTextTests
{
    private const string Secret = "SECRET-sqlprod01.corp\\ECR;Password=hunter2;object=dbo.HiddenTable";
    private const int TemplateId = 7;
    private const int VersionId = 71;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "F13-872-delta-P2")]
    public async Task Database_failure_to_enqueue_keeps_the_secret_off_the_card_and_in_the_log()
    {
        var (status, log) = (new ReportViewStatus(), new CapturingLogger());
        var error = new InvalidOperationException("wrapper " + Secret, new FakeSqlException(Secret, number: 1205));

        var queued = await MigrationReportViews.EnqueueAsync(
            Throwing(error), status, log, TemplateId, VersionId, columnRenames: 1, userId: 3, "corr-0123456789abcdef", CancellationToken.None);

        Assert.False(queued);
        var failure = Assert.Single(status.Snapshot());
        Assert.Equal(VersionId, failure.TemplateVersionId);

        // ⛔ Предмет тесту: секрету на картці немає, кореляція є; код — номер помилки SQL, не 0.
        Assert.DoesNotContain("SECRET", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", failure.Message, StringComparison.Ordinal);
        Assert.Contains("corr-0123456789abcdef", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1205, failure.Code);

        // Журнал: Warning з повним винятком і тією самою кореляцією.
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Same(error, warning.Exception);
        Assert.Contains(Secret, warning.Exception!.ToString(), StringComparison.Ordinal);
        Assert.Contains("corr-0123456789abcdef", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "F13-872-delta-P2")]
    public async Task Non_database_failure_without_request_correlation_gets_a_new_one_on_card_and_log()
    {
        var (status, log) = (new ReportViewStatus(), new CapturingLogger());
        var error = new TimeoutException(Secret);

        await MigrationReportViews.EnqueueAsync(
            Throwing(error), status, log, TemplateId, VersionId, columnRenames: 1, userId: 3, correlationId: null, CancellationToken.None);

        var failure = Assert.Single(status.Snapshot());
        Assert.DoesNotContain("SECRET", failure.Message, StringComparison.Ordinal);
        Assert.Equal(error.HResult, failure.Code);
        Assert.NotEqual(0, failure.Code);

        // Кореляцію згенеровано одну на обидва місця: текст картки містить ту, що в журналі.
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        var correlation = Assert.IsType<string>(warning.State["CorrelationId"]);
        Assert.False(string.IsNullOrWhiteSpace(correlation));
        Assert.Contains(correlation, failure.Message, StringComparison.Ordinal);
        Assert.Same(error, warning.Exception);
    }

    private static IBackgroundJobScheduler Throwing(Exception error)
    {
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnqueueCoalescedAsync<IReportViewsJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .ThrowsAsync(error);
        return jobs;
    }

    /// <summary>Помилка бази з номером, як <c>SqlException</c> (тип має публічну властивість <c>Number</c>).</summary>
    private sealed class FakeSqlException(string message, int number) : DbException(message)
    {
        public int Number { get; } = number;
    }

    private sealed record Entry(LogLevel Level, string Message, Exception? Exception, IReadOnlyDictionary<string, object?> State);

    private sealed class CapturingLogger : ILogger
    {
        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            var values = state is IReadOnlyList<KeyValuePair<string, object?>> list
                ? list.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            Entries.Add(new Entry(logLevel, formatter(state, exception), exception, values));
        }
    }
}
