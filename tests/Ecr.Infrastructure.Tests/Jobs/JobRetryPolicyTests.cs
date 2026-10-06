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

    /// <summary>
    /// Н-Л4: <see cref="BusinessRuleException"/> ретраїться лише з кодом «недоступно»;
    /// завелика відповідь джерела — ні, хоч код у неї той самий.
    /// </summary>
    /// <remarks>
    /// Мутації: прибрати арм <see cref="SourceResponseTooLargeException"/> — червоний
    /// перший рядок; <c>BusinessRuleException =&gt; true</c> — червоні рядки 0422.
    /// </remarks>
    [Fact]
    [Trait("Requirement", "L7-01")]
    public void Надто_глибокий_вираз_не_повторюється_і_має_власний_код()
    {
        // ⛔ L7-01: сторож стека обходу дерева виразу. Той самий вираз — той самий збій:
        // повтор лише ховав причину на 210 с і закінчувався ECR-SYS-0500.
        var tooDeep = new InsufficientExecutionStackException("too deep");

        Assert.False(JobRetryPolicy.IsWorthRetrying(tooDeep));
        Assert.False(JobRetryPolicy.ShouldRetry(0, tooDeep));
        Assert.Equal(ErrorCodes.ExpressionTooComplex, JobRetryPolicy.ErrorCodeOf(tooDeep));
    }

    [Fact]
    public void Прикладна_відмова_ретраїться_лише_з_кодом_недоступності()
    {
        Assert.False(JobRetryPolicy.IsWorthRetrying(
            new SourceResponseTooLargeException(ErrorCodes.SourceUnavailable, "понад 50 МБ")));
        Assert.False(JobRetryPolicy.IsWorthRetrying(new BusinessRuleException("ECR-INT-0422", "запит не налаштовано")));
        Assert.False(JobRetryPolicy.IsWorthRetrying(new BusinessRuleException("ECR-UOM-0422", "одиниця не переводиться")));

        Assert.True(JobRetryPolicy.IsWorthRetrying(new BusinessRuleException(ErrorCodes.SourceUnavailable, "джерело лежить")));
        Assert.True(JobRetryPolicy.IsWorthRetrying(new BusinessRuleException(ErrorCodes.Archiving, "архівування")));
    }

    /// <summary>
    /// Сторож: кожен код каталогу зі статусом <c>503</c> («спробуйте пізніше») —
    /// у переліку транзієнтних, і в переліку немає нічого іншого.
    /// </summary>
    /// <remarks>
    /// ⛔ Новий код <c>…-0503</c>, не внесений у <see cref="JobRetryPolicy.TransientRuleCodes"/>,
    /// мовчки перестав би ретраїтись; код іншого статусу в переліку — повертав би
    /// три марні ретраї вердиктам. Обидва напрямки червоні тут, а не в проді.
    /// Мутація: прибрати <c>Archiving</c> з переліку — тест червоний.
    /// </remarks>
    [Fact]
    public void Сторож_перелік_транзієнтних_кодів_дорівнює_кодам_зі_статусом_503()
    {
        var unavailable = typeof(ErrorCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(code => code.EndsWith("-0503", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            unavailable.Order(StringComparer.Ordinal),
            JobRetryPolicy.TransientRuleCodes.Order(StringComparer.Ordinal));
    }
}
