// tests/Ecr.Infrastructure.Tests/Integration/OutboxDispatcherErrorTextTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Integration;

/// <summary>
/// Помилка відправки в <c>itg.NotificationOutbox.Error</c> (її читає інтерфейс
/// обслуговування) не несе тексту винятку транспорту (SEC, TIER2).
/// </summary>
/// <remarks>
/// ⛔ До виправлення <c>OutboxDispatcher</c> клав <c>error.Message</c> як є: хост SMTP,
/// логін, фрагмент рядка підключення. Мутація: повернути <c>error.Message</c> —
/// перший тест червоний; власний виняток продукту лишає свій текст (другий тест).
/// </remarks>
[Collection("SqlServer")]
public sealed class OutboxDispatcherErrorTextTests(SqlServerFixture sql)
{
    private static readonly string[] Secrets = ["Secret123", "db01", "smtp.corp.internal", "secret.cfg"];

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Довільний_виняток_відправки_лягає_кодом_і_кореляцією_без_тексту_транспорту()
    {
        var error = await FailedErrorAsync(new InvalidOperationException(
            "Cannot connect to smtp.corp.internal:587. Server=db01;User Id=svc;Password=Secret123 "
            + @"C:\Users\svc\app\secret.cfg"));

        Assert.Contains("ECR-SYS-0500", error, StringComparison.Ordinal);
        Assert.Contains("correlation", error, StringComparison.Ordinal);
        foreach (var secret in Secrets)
        {
            Assert.DoesNotContain(secret, error, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Власний_виняток_продукту_лишає_свій_текст()
    {
        var error = await FailedErrorAsync(
            new BusinessRuleException("ECR-INT-0503", "Пошту не налаштовано: вкажіть відправника."));

        Assert.Equal("Пошту не налаштовано: вкажіть відправника.", error);
    }

    /// <summary>
    /// AN-67: id кореляції в тексті помилки (<c>itg.NotificationOutbox.Error</c>) дорівнює id у рядку журналу
    /// з повним винятком — оператор зв'язує текст із журналом. Мутація: окремий
    /// <c>SafeErrorText.NewCorrelationId()</c> для тексту — id різні, тест червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кореляція_з_тексту_помилки_дорівнює_кореляції_в_рядку_журналу()
    {
        var thrown = new InvalidOperationException("Cannot connect to smtp.corp.internal:587. Password=Secret123");
        var log = new CapturingLog();

        var error = await FailedErrorAsync(thrown, log);

        var inText = System.Text.RegularExpressions.Regex.Match(error, @"correlation ([0-9a-f]{8,32})");
        Assert.True(inText.Success, error);
        var entry = Assert.Single(log.Entries);
        var inLog = System.Text.RegularExpressions.Regex.Match(entry.Text, @"кореляція ([0-9a-f]{8,32})\.");
        Assert.True(inLog.Success, entry.Text);
        Assert.Equal(inText.Groups[1].Value, inLog.Groups[1].Value);
        Assert.Same(thrown, entry.Exception);
    }

    private sealed class CapturingLog : Microsoft.Extensions.Logging.ILogger<OutboxDispatcher>
    {
        public List<(string Text, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((formatter(state, exception), exception));
    }

    private async Task<string> FailedErrorAsync(
        Exception thrown, Microsoft.Extensions.Logging.ILogger<OutboxDispatcher>? log = null)
    {
        var start = new DateTime(2036, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var subject = $"E1-{Guid.NewGuid():N}";

        await using (var db = CreateContext())
        {
            // ⚠ Чужі незавершені рядки колекції потрапили б у ту саму партію.
            await db.NotificationOutbox
                .Where(n => n.State == "Pending" || n.State == "Sending")
                .ExecuteDeleteAsync()
                .ConfigureAwait(false);

            db.NotificationOutbox.Add(
                new NotificationOutboxItem("test.errtext", subject, "Текст", "ops@example.local", start));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        await using (var db = CreateContext())
        {
            await new OutboxDispatcher(db, new TestClock(start.AddMinutes(1)), new ThrowingSender(thrown), log)
                .FlushAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }

        await using var verify = CreateContext();
        var row = await verify.NotificationOutbox
            .AsNoTracking()
            .SingleAsync(n => n.Subject == subject)
            .ConfigureAwait(false);

        Assert.NotNull(row.Error);
        return row.Error;
    }

    private sealed class ThrowingSender(Exception error) : INotificationSender
    {
        public bool IsConfigured => true;

        public Task SendAsync(IReadOnlyList<string> recipients, string subject, string body, CancellationToken ct)
            => Task.FromException(error);
    }
}
