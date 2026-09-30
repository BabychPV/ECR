// tests/Ecr.Infrastructure.Tests/Jobs/JobRetryPolicyTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <see cref="JobRetryPolicy"/> — одне визначення ретраїв на Quartz і чергу в базі (MI-02, F1c).
/// </summary>
/// <remarks>
/// Поведінку <see cref="QuartzJobAdapter"/> після виносу тримають наявні
/// <c>QuartzJobAdapterRetryTests</c> і <c>QuartzJobFailureMessageTests</c>; тут —
/// сама політика, якою тепер користуються обидва виконавці.
/// Мутації: 30·2^n замість 30·2^(n-1) — перший тест червоний; прибрати
/// <see cref="JobLeaseLostException"/> з переліку — другий червоний.
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage5)]
public sealed class JobRetryPolicyTests
{
    [Fact]
    public void Затримки_30_60_120_і_три_ретраї_четверта_спроба_остання()
    {
        Assert.Equal(
            [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120)],
            Enumerable.Range(1, JobRetryPolicy.MaxRetryAttempts).Select(JobRetryPolicy.DelayBefore));

        var transient = new TimeoutException("мережа");
        Assert.True(JobRetryPolicy.ShouldRetry(0, transient));
        Assert.True(JobRetryPolicy.ShouldRetry(2, transient));
        Assert.False(JobRetryPolicy.ShouldRetry(3, transient));

        Assert.Throws<ArgumentOutOfRangeException>(() => JobRetryPolicy.DelayBefore(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => JobRetryPolicy.DelayBefore(4));
        Assert.Equal(QuartzJobAdapter.MaxRetryAttempts, JobRetryPolicy.MaxRetryAttempts);
    }

    [Fact]
    public void Вердикти_і_втрачена_оренда_не_повторюються_транзієнтні_так()
    {
        Assert.False(JobRetryPolicy.IsWorthRetrying(new JobLeaseLostException("j-1")));
        Assert.False(JobRetryPolicy.IsWorthRetrying(new NotFoundException("ECR-PRD-0404", "немає")));
        Assert.False(JobRetryPolicy.IsWorthRetrying(new AccessDeniedException("ECR-AUTH-0403", "ні")));

        Assert.True(JobRetryPolicy.IsWorthRetrying(new BusinessRuleException("ECR-INT-0503", "джерело лежить")));
        Assert.True(JobRetryPolicy.IsWorthRetrying(new InvalidOperationException("дедлок")));

        Assert.Equal("ECR-INT-0503", JobRetryPolicy.ErrorCodeOf(new BusinessRuleException("ECR-INT-0503", "x")));
        Assert.Equal(ErrorCodes.Internal, JobRetryPolicy.ErrorCodeOf(new TimeoutException()));
    }
}
