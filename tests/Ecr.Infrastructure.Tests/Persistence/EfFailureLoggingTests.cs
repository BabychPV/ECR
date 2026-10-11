using Ecr.Domain.Entities.Notifications;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// E1-06: збій, який застосунок сам перетворює на 409/503, не пишеться EF Core рівнем <c>Error</c>.
/// </summary>
/// <remarks>
/// ⛔ <c>CommandError</c> і <c>SaveChangesFailed</c> EF пише як <c>Error</c> завжди, навіть для гонки за унікальним
/// індексом, яку <c>UnitOfWork</c> мапить у 409; журнал подій Windows (від <c>Warning</c>) діставав «Error» без збою
/// сервера. Контекст тут побудовано тими самими опціями журналу, що й у DI (<c>ConfigureEfFailureLogLevels</c>), і
/// проти справжнього унікального індексу. Мутація: прибрати <c>ConfigureEfFailureLogLevels</c> — з'являються записи
/// <c>Error</c> категорій EF.
/// </remarks>
[Collection("SqlServer")]
public sealed class EfFailureLoggingTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "E1-06")]
    public async Task Порушення_унікального_індексу_не_пишеться_EF_рівнем_Error()
    {
        var channelId = await ArrangeChannelAsync();
        var entries = new List<(LogLevel Level, string Category, string Message)>();
        using var factory = LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new CapturingProvider(entries)));

        await using (var winner = Context())
        {
            winner.NotificationRules.Add(
                new NotificationRule(NotificationEventKind.JobFailed, channelId, NotificationSeverity.Error));
            await winner.SaveChangesAsync();
        }

        await using var loser = Context(factory);
        loser.NotificationRules.Add(
            new NotificationRule(NotificationEventKind.JobFailed, channelId, NotificationSeverity.Warning));
        await Assert.ThrowsAsync<DbUpdateException>(() => loser.SaveChangesAsync());

        var efErrors = entries
            .Where(e => e.Category.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                        && e.Level >= LogLevel.Error)
            .Select(e => $"{e.Level} {e.Category}: {e.Message}")
            .ToList();

        Assert.True(efErrors.Count == 0, "EF пише збій як Error:" + Environment.NewLine + string.Join(Environment.NewLine, efErrors));

        // Слід лишається — рівнем Warning: його видно і в журналі подій, і у файловому.
        Assert.Contains(
            entries,
            e => e.Level == LogLevel.Warning && e.Category.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    private async Task<int> ArrangeChannelAsync()
    {
        await using var db = Context();
        var channel = new NotificationChannel(
            NotificationChannelKind.TeamsWebhook, $"E106 {_tag}", "{}", new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc), null);
        db.NotificationChannels.Add(channel);
        await db.SaveChangesAsync();
        return channel.Id;
    }

    private EcrDbContext Context(ILoggerFactory? loggerFactory = null)
    {
        var options = new DbContextOptionsBuilder<EcrDbContext>()
            .ConfigureEfFailureLogLevels()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"));

        if (loggerFactory is not null)
        {
            options.UseLoggerFactory(loggerFactory);
        }

        return new EcrDbContext(options.Options);
    }

    private sealed class CapturingProvider(List<(LogLevel, string, string)> entries) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, List<(LogLevel, string, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (entries)
                {
                    entries.Add((logLevel, category, formatter(state, exception)));
                }
            }
        }
    }
}
