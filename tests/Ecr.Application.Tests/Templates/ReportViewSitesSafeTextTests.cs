// tests/Ecr.Application.Tests/Templates/ReportViewSitesSafeTextTests.cs
using System.Data.Common;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// F13 RC #872 delta P2 (SEC, TIER2), ще два місця того самого витоку: збій ПОСТАНОВКИ перегенерації вʼюх після
/// публікації версії (<see cref="PublishTemplateVersionHandler"/>) і на старті застосунку
/// (<see cref="ReportViewsJob.QueueAllOnStartupAsync(IBackgroundJobScheduler?, IReportViewGenerator, IReportViewStatus?, ILogger?, CancellationToken)"/>).
/// Картку <c>reportviews</c> читає АНОНІМНИЙ <c>/health/ready</c>: сирий текст винятку туди не йде — лише заголовок і
/// кореляція, код не <c>0</c>; повний виняток — у Warning під тією самою кореляцією.
/// </summary>
/// <remarks>
/// Червоне до фіксу (у місці повернути <c>"…not queued: " + ex.Message</c> і код <c>0</c>): секрет на картці, кореляції
/// немає, код 0 (і Warning без винятку).
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportViewSitesSafeTextTests(SqlServerFixture sql)
{
    private const string Secret = "SECRET-sqlprod01.corp\\ECR;Password=hunter2;object=dbo.HiddenTable";
    private const int Actor = 9;
    private static readonly DateTime Now = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "F13-872-delta-P2")]
    public async Task Publish_failure_to_enqueue_views_keeps_the_secret_off_the_card_and_in_the_log()
    {
        var versionId = await ArrangeDraftAsync();
        var status = new ReportViewStatus();
        var log = new CapturingLogger<PublishTemplateVersionHandler>();
        var error = new InvalidOperationException("wrapper " + Secret, new FakeSqlException(Secret, number: 1205));

        await PublishAsync(versionId, status, Throwing(error), log, correlationId: "corr-publish-0123456789");

        // Публікація не відкочена: збій постановки — найкраще зусилля.
        await using (var db = Context())
        {
            Assert.Equal(
                TemplateVersionStatus.Published,
                await db.TemplateVersions.AsNoTracking().Where(v => v.Id == versionId).Select(v => v.Status).SingleAsync());
        }

        var failure = Assert.Single(status.Snapshot());
        Assert.Equal(versionId, failure.TemplateVersionId);
        Assert.DoesNotContain("SECRET", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", failure.Message, StringComparison.Ordinal);
        Assert.Contains("corr-publish-0123456789", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1205, failure.Code);

        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning && e.Exception is not null);
        Assert.Same(error, warning.Exception);
        Assert.Contains(Secret, warning.Exception!.ToString(), StringComparison.Ordinal);
        Assert.Contains("corr-publish-0123456789", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "F13-872-delta-P2")]
    public async Task Startup_failure_to_enqueue_views_keeps_the_secret_off_the_card_and_in_the_log()
    {
        var status = new ReportViewStatus();
        var log = new CapturingLogger<ReportViewsJob>();
        var error = new TimeoutException(Secret);

        var queued = await ReportViewsJob.QueueAllOnStartupAsync(
            Throwing(error), Substitute.For<IReportViewGenerator>(), status, log, CancellationToken.None);

        Assert.False(queued);
        var failure = Assert.Single(status.Snapshot());
        Assert.Null(failure.TemplateVersionId);
        Assert.DoesNotContain("SECRET", failure.Message, StringComparison.Ordinal);
        Assert.Equal(error.HResult, failure.Code);
        Assert.NotEqual(0, failure.Code);

        // Старт — поза запитом: кореляція нова, і та сама на картці й у журналі.
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Same(error, warning.Exception);
        var correlation = Assert.IsType<string>(warning.State["CorrelationId"]);
        Assert.False(string.IsNullOrWhiteSpace(correlation));
        Assert.Contains(correlation, failure.Message, StringComparison.Ordinal);
    }

    private static IBackgroundJobScheduler Throwing(Exception error)
    {
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnqueueCoalescedAsync<IReportViewsJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .ThrowsAsync(error);
        return jobs;
    }

    private async Task PublishAsync(
        int versionId, IReportViewStatus status, IBackgroundJobScheduler jobs, ILogger<PublishTemplateVersionHandler> log,
        string correlationId)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }
                .Permission(PublishTemplateVersionHandler.Permission).Permission("Template.View").Build());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Actor);
        user.CorrelationId.Returns(correlationId);

        await using var db = Context();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var handler = new PublishTemplateVersionHandler(
            new Repository<TemplateVersion, int>(db),
            new TemplateVersionStore(db),
            new RealFormulaEngine(),
            new CalculationBindingStore(db),
            new MetadataCache(memory, db),
            new UnitCatalog(db),
            access,
            user,
            new AuditWriter(db),
            new UnitOfWork(db),
            new TestClock(Now),
            Substitute.For<IReportViewGenerator>(),
            logger: log,
            viewStatus: status,
            jobs: jobs);

        await handler.PublishAsync(versionId, Actor, "safe card text", CancellationToken.None);
    }

    private async Task<int> ArrangeDraftAsync()
    {
        var code = "SV" + Guid.NewGuid().ToString("N")[..8];
        await using var db = Context();
        var template = new Template(EcrCode.Create(code), Text(code), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create("S"), Text("S"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(sheet.Id, EcrCode.Create("T1"), Text("T1"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        db.ColumnDefs.Add(new ColumnDef(table.Id, EcrCode.Create("C1"), Text("C1"), 1, CellDataType.Decimal));
        await db.SaveChangesAsync();
        return version.Id;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.CommandTimeout(120))
            .Options);

    /// <summary>Помилка бази з номером, як <c>SqlException</c> (тип має публічну властивість <c>Number</c>).</summary>
    private sealed class FakeSqlException(string message, int number) : DbException(message)
    {
        public int Number { get; } = number;
    }

    private sealed record Entry(LogLevel Level, string Message, Exception? Exception, IReadOnlyDictionary<string, object?> State);

    private sealed class CapturingLogger<T> : ILogger<T>
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
