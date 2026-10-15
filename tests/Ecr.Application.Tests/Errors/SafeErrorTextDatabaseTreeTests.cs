using System.Data.Common;
using Ecr.Application.Errors;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Errors;

/// <summary>
/// <c>SafeErrorText.For</c>: збій БД, схований в <c>AggregateException</c> (паралельні гілки оркестратора) або в
/// ланцюзі «вичерпані повтори EF / клієнтський таймаут команди», читається як «database error», а не «unexpected».
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: повернути стару циклу лише по <c>InnerException</c> із перевіркою <c>DbException</c>
/// — тести Aggregate/Timeout/RetryLimit червоні (текст «unexpected error»).
/// </remarks>
public sealed class SafeErrorTextDatabaseTreeTests
{
    private const string Correlation = "corr-1";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void DbException_у_InnerException_лишається_помилкою_бази()
    {
        var text = SafeErrorText.For(new InvalidOperationException("x", new FakeDbException()), Correlation, "the recalculation");

        Assert.StartsWith("A database error", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void DbException_у_InnerExceptions_AggregateException_читається_як_помилка_бази()
    {
        var aggregate = new AggregateException(new InvalidOperationException("a"), new FakeDbException());

        var text = SafeErrorText.For(aggregate, Correlation, "the recalculation");

        Assert.StartsWith("A database error", text, StringComparison.Ordinal);
        Assert.DoesNotContain("unexpected", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void TimeoutException_з_DbException_всередині_читається_як_помилка_бази()
    {
        var error = new InvalidOperationException(
            "outer", new TimeoutException("Execution Timeout Expired.", new FakeDbException()));

        var text = SafeErrorText.For(error, Correlation, "the recalculation");

        Assert.StartsWith("A database error", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Голий_TimeoutException_без_БД_всередині_лишається_unexpected()
    {
        var error = new InvalidOperationException("outer", new TimeoutException("HTTP call timed out."));

        var text = SafeErrorText.For(error, Correlation, "the recalculation");

        Assert.Contains("unexpected error", text, StringComparison.Ordinal);
        Assert.DoesNotContain("A database error", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void RetryLimitExceededException_за_іменем_типу_читається_як_помилка_бази()
    {
        var text = SafeErrorText.For(new RetryLimitExceededException(), Correlation, "the recalculation");

        Assert.StartsWith("A database error", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Aggregate_без_причин_бази_лишається_unexpected_і_не_протікає_текст_винятку()
    {
        var aggregate = new AggregateException(new InvalidOperationException("Password=secret"));

        var text = SafeErrorText.For(aggregate, Correlation, "the recalculation");

        Assert.Contains("unexpected error", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
    }

    private sealed class FakeDbException : DbException
    {
    }

    /// <summary>Тезка <c>Microsoft.EntityFrameworkCore.Infrastructure.RetryLimitExceededException</c> (Application без EF).</summary>
    private sealed class RetryLimitExceededException : Exception
    {
    }
}
