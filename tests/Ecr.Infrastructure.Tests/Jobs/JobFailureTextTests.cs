// tests/Ecr.Infrastructure.Tests/Jobs/JobFailureTextTests.cs
using System.Data.Common;
using Ecr.Application.Errors;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Текст провалу фонової задачі (<c>JobStatus.Error</c>, <c>System.ViewHealth</c>,
/// <c>DetailsJson</c> прогону обслуговування) не несе внутрішніх подробиць
/// довільного винятку (SEC, TIER2).
/// </summary>
/// <remarks>
/// ⛔ До виправлення <c>JobFailureText.For</c> для не-БД винятків віддавав
/// <c>Exception.Message</c> як є: рядок підключення з <c>Password=…</c>, шлях
/// файлу конфігурації, ім'я сервера. Мутація: повернути <c>error.Message</c> у
/// гілку «не БД» — перші два тести червоні.
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage5)]
public sealed class JobFailureTextTests
{
    private const string Correlation = "corr-sec-0001";

    private static readonly string[] Secrets = ["Password", "Secret123", "db01", "secret.cfg", @"C:\Users\svc"];

    [Fact]
    public void Виняток_із_рядком_підключення_не_віддає_ні_пароля_ні_сервера()
    {
        var text = JobRetryPolicy.FailureText(
            new InvalidOperationException("Server=db01;Password=Secret123;Database=Ecr"), Correlation);

        AssertClean(text);
        Assert.Contains(ErrorCodes.Internal, text, StringComparison.Ordinal);
        Assert.Contains(Correlation, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Виняток_зі_шляхом_файлу_не_віддає_шлях()
    {
        var text = JobRetryPolicy.FailureText(
            new FileNotFoundException(@"Could not find file 'C:\Users\svc\app\secret.cfg'.", @"C:\Users\svc\app\secret.cfg"),
            Correlation);

        AssertClean(text);
        Assert.Contains(ErrorCodes.Internal, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Вкладений_виняток_теж_не_протікає()
    {
        var text = JobRetryPolicy.FailureText(
            new InvalidOperationException("зовнішній", new TimeoutException("Server=db01;Password=Secret123")),
            Correlation);

        AssertClean(text);
    }

    [Fact]
    public void Помилка_бази_поводиться_як_раніше()
    {
        var text = JobRetryPolicy.FailureText(new FakeDbException("Server=db01;Password=Secret123"), Correlation);

        AssertClean(text);
        Assert.Equal(
            $"A database error interrupted the job. The details are in the server log (correlation {Correlation}).",
            text);
    }

    [Fact]
    public void Власні_винятки_продукту_віддають_свій_текст_як_раніше()
    {
        Assert.Equal(
            "джерело лежить",
            JobRetryPolicy.FailureText(new BusinessRuleException(ErrorCodes.SourceUnavailable, "джерело лежить"), Correlation));
        Assert.Equal(
            "немає",
            JobRetryPolicy.FailureText(new NotFoundException("ECR-PRD-0404", "немає"), Correlation));
    }

    [Fact]
    public void Сторож_стека_виразу_називає_свій_код()
    {
        var text = JobRetryPolicy.FailureText(new InsufficientExecutionStackException("deep"), Correlation);

        Assert.Contains(ErrorCodes.ExpressionTooComplex, text, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureText_для_задачі_теж_чистий_і_вкладається_в_межу_стовпця()
    {
        var text = JobRetryPolicy.FailureText(
            new InvalidOperationException("Server=db01;Password=Secret123;" + new string('x', 5000)), Correlation);

        AssertClean(text);
        Assert.True(text.Length <= Ecr.Application.Ports.IJobProgressStore.MaxErrorLength);
    }

    private static void AssertClean(string text)
    {
        foreach (var secret in Secrets)
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class FakeDbException(string message) : DbException(message);
}
