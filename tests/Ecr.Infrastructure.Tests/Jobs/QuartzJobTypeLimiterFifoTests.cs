// tests/Ecr.Infrastructure.Tests/Jobs/QuartzJobTypeLimiterFifoTests.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Z5-04 / R2-03: межа Excel у режимі Quartz - черга чекаючих за порядком надходження, а очікування місця не
/// запускає відлік стелі відкладень за локом (30 хв).
/// </summary>
/// <remarks>
/// Без реального часу й потоків: годинник - ручний <see cref="TimeProvider"/>, адаптер викликається напряму з
/// підставним контекстом Quartz. Мутація: повернути <c>TryEnter(Type)</c> без черги в адаптері - тест FIFO червоний
/// (місце дістається тому, хто звернувся першим після звільнення); повернути <c>DeferredSince(context) ?? clock.UtcNow</c> -
/// тест стелі червоний (триґер очікування несе <c>ecr.deferredSince</c>).
/// </remarks>
public sealed class QuartzJobTypeLimiterFifoTests
{
    private const string JobId = "excel-job-2";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Z5-04")]
    public void Z5_04_місце_дістається_тій_що_чекає_найдовше_а_не_тій_що_звернулась_першою_після_звільнення()
    {
        var limiter = new QuartzJobTypeLimiter(1);

        var first = limiter.TryEnter(typeof(ExcelExportJob), "a");
        Assert.NotNull(first);

        // b стала в чергу першою, c - другою.
        Assert.Null(limiter.TryEnter(typeof(ExcelExportJob), "b"));
        Assert.Null(limiter.TryEnter(typeof(ExcelExportJob), "c"));
        Assert.Equal(2, limiter.ExcelWaiting);

        first!.Dispose();

        // Тригер c спрацював раніше за тригер b: без черги вона забрала б місце. З чергою - ні.
        Assert.Null(limiter.TryEnter(typeof(ExcelExportJob), "c"));
        var second = limiter.TryEnter(typeof(ExcelExportJob), "b");
        Assert.NotNull(second);

        // Нова задача теж не обходить чергу, а c - наступна.
        Assert.Null(limiter.TryEnter(typeof(ExcelExportJob), "d"));
        second!.Dispose();
        Assert.Null(limiter.TryEnter(typeof(ExcelExportJob), "d"));
        using var third = limiter.TryEnter(typeof(ExcelExportJob), "c");
        Assert.NotNull(third);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Z5-04")]
    public void Z5_04_вільні_місця_роздаються_першим_у_черзі_в_межах_їх_кількості_а_мертва_голова_випадає()
    {
        var time = new ManualTime();
        var limiter = new QuartzJobTypeLimiter(2, time);

        var a = limiter.TryEnter(typeof(ExcelExportJob), "a");
        var b = limiter.TryEnter(typeof(ExcelExportJob), "b");
        Assert.NotNull(a);
        Assert.NotNull(b);

        // c і d чекають; обидва місця звільняються - обидва проходять, у порядку черги.
        Assert.Null(limiter.TryEnter(typeof(ExcelExportJob), "c"));
        Assert.Null(limiter.TryEnter(typeof(ExcelExportJob), "d"));
        a!.Dispose();
        b!.Dispose();
        using var d = limiter.TryEnter(typeof(ExcelExportJob), "d");
        Assert.NotNull(d);
        using var c = limiter.TryEnter(typeof(ExcelExportJob), "c");
        Assert.NotNull(c);

        // Мертва голова (задачу скасовано, триґер не повернеться) не блокує чергу довіку.
        var tight = new QuartzJobTypeLimiter(1, time);
        var holder = tight.TryEnter(typeof(ExcelExportJob), "holder");
        Assert.Null(tight.TryEnter(typeof(ExcelExportJob), "dead"));
        Assert.Null(tight.TryEnter(typeof(ExcelExportJob), "alive"));
        holder!.Dispose();

        time.Advance(tight.WaiterExpiry + TimeSpan.FromSeconds(1));

        // «dead» не з'явилась - випала; «alive» (тут вона вперше за час - теж випала й стає новоприбулою) проходить.
        using var granted = tight.TryEnter(typeof(ExcelExportJob), "alive");
        Assert.NotNull(granted);
        Assert.Equal(0, tight.ExcelWaiting);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Z5-04")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Z5_04_очікування_місця_не_запускає_відлік_стелі_відкладень_а_наявний_відлік_зберігається(
        bool hadLockDeferral)
    {
        var limiter = new QuartzJobTypeLimiter(1);
        using var holder = limiter.TryEnter(typeof(ExcelExportJob), "holder");
        Assert.NotNull(holder);

        var services = new ServiceCollection();
        services.AddSingleton<Ecr.Domain.Abstractions.IClock, SystemClock>();
        services.AddSingleton<IExcelExportJob>(new NoopExcelJob());
        services.AddSingleton(limiter);
        await using var provider = services.BuildServiceProvider();

        var since = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var triggerBuilder = TriggerBuilder.Create().WithIdentity("t1").StartNow();
        if (hadLockDeferral)
        {
            triggerBuilder.UsingJobData(
                JobDeferral.QuartzSinceKey, since.Ticks.ToString(CultureInfo.InvariantCulture));
        }

        ITrigger? scheduled = null;
        var scheduler = Substitute.For<IScheduler>();
        scheduler.ScheduleJob(Arg.Do<ITrigger>(t => scheduled = t), Arg.Any<CancellationToken>())
            .Returns(DateTimeOffset.UtcNow);

        var context = Substitute.For<IJobExecutionContext>();
        context.JobDetail.Returns(
            JobBuilder.Create<QuartzJobAdapter>()
                .WithIdentity(JobId)
                .UsingJobData(QuartzJobScheduler.JobCodeKey, typeof(IExcelExportJob).FullName!)
                .UsingJobData(QuartzJobScheduler.PayloadKey, "{}")
                .Build());
        context.Trigger.Returns(triggerBuilder.Build());
        context.Scheduler.Returns(scheduler);
        context.CancellationToken.Returns(CancellationToken.None);

        await new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance).Execute(context);

        Assert.NotNull(scheduled);
        var map = scheduled!.JobDataMap;
        if (hadLockDeferral)
        {
            Assert.Equal(since.Ticks.ToString(CultureInfo.InvariantCulture), map.GetString(JobDeferral.QuartzSinceKey));
        }
        else
        {
            Assert.False(map.ContainsKey(JobDeferral.QuartzSinceKey));
        }
    }

    /// <summary>Годинник, що рухається лише руками.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long now;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => Volatile.Read(ref now);

        public void Advance(TimeSpan by) => Interlocked.Add(ref now, (long)by.TotalMilliseconds);
    }

    private sealed class NoopExcelJob : IExcelExportJob
    {
        public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct) => Task.CompletedTask;
    }
}
