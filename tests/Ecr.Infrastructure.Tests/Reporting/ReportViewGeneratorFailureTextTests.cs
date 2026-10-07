// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewGeneratorFailureTextTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Збій генерації в'юх у <c>/health/ready</c> не несе тексту системної помилки SQL
/// Server (SEC, TIER2).
/// </summary>
/// <remarks>
/// ⛔ До виправлення <c>ReportViewGenerator</c> клав <c>SqlException.Message</c> як є:
/// ім'я об'єкта, фрагмент запиту, ім'я сервера. Тут — справжня системна помилка
/// 2812 (процедури немає: контекст дивиться в <c>master</c>), текст якої називає
/// процедуру. Мутація: повернути <c>ex.Message</c> — тест червоний. Власні
/// повідомлення процедури (50422/50409) лишаються —
/// <c>PublishViewsBestEffortTests</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportViewGeneratorFailureTextTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Системна_помилка_SQL_у_стані_health_без_тексту_сервера_лише_з_номером()
    {
        var master = new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = "master" };
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(master.ConnectionString).Options);
        var status = new CapturingStatus();

        var error = await Assert.ThrowsAsync<SqlException>(
            () => new ReportViewGenerator(db, status).GenerateAsync(1, CancellationToken.None));

        Assert.Equal(2812, error.Number);
        // Сам виняток лишається повним (його логує викликач) — текст сервера є...
        Assert.Contains("usp_GenerateTemplateViews", error.Message, StringComparison.Ordinal);
        // ...а в стан для /health/ready він не йде: лише номер.
        var failure = Assert.Single(status.Failures);
        Assert.Equal(2812, failure.Code);
        Assert.DoesNotContain("usp_GenerateTemplateViews", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("rpt.", failure.Message, StringComparison.Ordinal);
        Assert.Contains("2812", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Той самий correlation id стоїть і в тексті для /health/ready, і в рядку журналу з повним
    /// винятком (SEC TIER2): оператор зв'язує екранний текст із журналом.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кореляція_з_тексту_health_стоїть_у_рядку_журналу_разом_із_повним_винятком()
    {
        var master = new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = "master" };
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(master.ConnectionString).Options);
        var status = new CapturingStatus();
        var log = new CapturingLog();

        var error = await Assert.ThrowsAsync<SqlException>(
            () => new ReportViewGenerator(db, status, log).GenerateAsync(1, CancellationToken.None));

        var failure = Assert.Single(status.Failures);
        var match = System.Text.RegularExpressions.Regex.Match(failure.Message, @"correlation ([0-9a-f]{8,32})");
        Assert.True(match.Success, failure.Message);

        var entry = Assert.Single(log.Entries);
        Assert.Contains(match.Groups[1].Value, entry.Text, StringComparison.Ordinal);
        Assert.Same(error, entry.Exception);
    }

    private sealed class CapturingLog : Microsoft.Extensions.Logging.ILogger<ReportViewGenerator>
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

    private sealed class CapturingStatus : IReportViewStatus
    {
        public List<ReportViewFailure> Failures { get; } = [];

        public void Failed(ReportViewFailure failure) => Failures.Add(failure);

        public void Succeeded(int? templateVersionId)
        {
        }

        public IReadOnlyList<ReportViewFailure> Snapshot() => Failures;
    }
}
