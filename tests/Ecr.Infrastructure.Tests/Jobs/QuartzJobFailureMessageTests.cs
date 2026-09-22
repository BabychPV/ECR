// tests/Ecr.Infrastructure.Tests/Jobs/QuartzJobFailureMessageTests.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Задача, що падає з ДОВГОЮ кириличною причиною — саме тим, що не влазило.
/// </summary>
/// <remarks>
/// ⚠ Тип оголошено на рівні простору імен, а не вкладеним у тестовий клас, і
/// це не стиль: <c>JobCode</c> у базі — <c>nvarchar(64)</c>, а туди лягає
/// <c>FullName</c> типу задачі. Вкладене ім'я
/// (<c>…+QuartzJobFailureMessageTests+…</c>) саме переросло б стовпець — і
/// тест падав би на власному імені замість перевіряти повідомлення.
/// </remarks>
internal sealed class CyrillicFailingJob : IBackgroundJob
{
    /// <summary>Початок причини — за ним її й упізнають у базі.</summary>
    internal const string ReasonHead = "Не вдалося прочитати теги джерела";

    /// <summary>Причина провалу: 260+ кириличних літер, як у справжній аварії.</summary>
    internal const string Reason =
        ReasonHead + " PI Web API: сервер відповів відмовою, з'єднання розірвано "
        + "на середині вибірки, а повторна спроба впала на тайм-ауті читання. "
        + "Перевірте доступність вузла збору та обліковий запис служби, від імені "
        + "якого виконується збір даних.";

    /// <inheritdoc />
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        => throw new InvalidOperationException(Reason);
}

/// <summary>
/// Провал задачі мусить ДОЇХАТИ до бази — на реальному SQL Server, де межі
/// стовпців справжні.
/// </summary>
/// <remarks>
/// ⛔ Дефект, знайдений наскрізною перевіркою (<c>tools/smoke.ps1</c>, крок
/// 23). Причина провалу йшла в конверт <c>jobs.retryScheduled</c> як є;
/// українське повідомлення в JSON екранується по шість символів на літеру,
/// тож конверт переростав <c>nvarchar(400)</c> і SQL Server відповідав
/// <c>Msg 2628</c>. Виняток летів із блоку <c>catch</c>, тому стан НІКОЛИ не
/// ставав <c>Failed</c>: клієнт вічно бачив «виконується», триґер ретраю вже
/// був поставлений — задача перезапускалася кожні 30/60 с і падала знову, а
/// текст причини губився назавжди.
/// <para>
/// ⚠ Підробки тут не досить: у підставленому <c>IJobProgressStore</c> межі
/// стовпця не існує взагалі, тож дефект був зелений у наявних
/// <c>QuartzJobAdapterRetryTests</c> і лишився б зеленим після будь-якого
/// «виправлення». Доводить лише справжній запис у справжню таблицю.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class QuartzJobFailureMessageTests(SqlServerFixture sql)
{
    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<EcrDbContext>(o => o.UseSqlServer(sql.ConnectionString));
        services.AddScoped<IJobProgressStore, JobProgressStore>();
        services.AddSingleton<Ecr.Domain.Abstractions.IClock>(
            new TestClock(new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc)));
        services.AddScoped<CyrillicFailingJob>();

        return services.BuildServiceProvider();
    }

    /// <summary>Контекст так, наче Quartz щойно відпустив N-ту спробу.</summary>
    private static IJobExecutionContext ContextAt(string jobId, int attempt)
    {
        var jobData = new JobDataMap
        {
            { QuartzJobScheduler.JobCodeKey, typeof(CyrillicFailingJob).FullName! },
            { QuartzJobScheduler.PayloadKey, "null" },
        };

        var jobDetail = Substitute.For<IJobDetail>();
        jobDetail.Key.Returns(new JobKey(jobId));
        jobDetail.JobDataMap.Returns(jobData);

        var triggerData = new JobDataMap();
        if (attempt > 0)
        {
            triggerData.Put(QuartzJobScheduler.RetryAttemptKey, attempt.ToString(CultureInfo.InvariantCulture));
        }

        var trigger = Substitute.For<ITrigger>();
        trigger.JobDataMap.Returns(triggerData);

        var context = Substitute.For<IJobExecutionContext>();
        context.JobDetail.Returns(jobDetail);
        context.Trigger.Returns(trigger);
        context.Scheduler.Returns(Substitute.For<IScheduler>());
        context.CancellationToken.Returns(CancellationToken.None);

        return context;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.4")]
    public async Task Довга_кирилична_причина_не_валить_запис_і_задача_доходить_до_Failed()
    {
        Assert.True(
            CyrillicFailingJob.Reason.Length >= 200,
            $"Причина мусить бути довгою: потрібно ≥ 200 літер, а є {CyrillicFailingJob.Reason.Length}.");

        var jobId = $"longfail-{Guid.NewGuid():N}";
        await using var provider = BuildProvider();
        var adapter = new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);

        // Перший провал: ретрай запланований, повідомлення про нього — у базі.
        await adapter.Execute(ContextAt(jobId, attempt: 0));

        await using (var db = sql.CreateContext())
        {
            var afterRetry = await new JobProgressStore(db).FindAsync(jobId, CancellationToken.None);

            Assert.NotNull(afterRetry);

            // (а) Запис прогресу не впав — конверт у стовпці, і він читається.
            Assert.True(
                JobProgressMessageCodec.TryDecode(afterRetry.Message, out var envelope),
                $"У стовпці мав бути конверт, а лежить: {afterRetry.Message ?? "«нічого»"}.");

            Assert.Equal("jobs.retryScheduled", envelope.Key);

            // (в) Причина лишилася ВПІЗНАВАНОЮ, а не порожньою.
            Assert.StartsWith(
                CyrillicFailingJob.ReasonHead, envelope.Params!["error"], StringComparison.Ordinal);

            // ⚠ Ретрай — не провал: клієнт і далі бачить «виконується».
            Assert.Equal("Running", afterRetry.State);
        }

        // Решта дозволених ретраїв.
        for (var attempt = 1; attempt < QuartzJobAdapter.MaxRetryAttempts; attempt++)
        {
            await adapter.Execute(ContextAt(jobId, attempt));
        }

        // Остання спроба: ліміт вичерпано, задача мусить зупинитися провалом.
        await Assert.ThrowsAsync<JobExecutionException>(
            () => adapter.Execute(ContextAt(jobId, QuartzJobAdapter.MaxRetryAttempts)));

        await using (var db = sql.CreateContext())
        {
            var status = await new JobProgressStore(db).FindAsync(jobId, CancellationToken.None);

            Assert.NotNull(status);

            // (б) Саме це й не відбувалося: стан застрягав на `Running` назавжди.
            Assert.Equal("Failed", status.State);

            // (в) Причина провалу збережена й читабельна.
            Assert.StartsWith(CyrillicFailingJob.ReasonHead, status.Error!, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Межі стовпців і межі в коді — одні й ті самі числа.
    /// </summary>
    /// <remarks>
    /// ⚠ Константи живуть в <c>Ecr.Application</c>, а схема — в
    /// <c>Ecr.Infrastructure</c>, і посилатися одна на одну вони не можуть за
    /// побудовою шарів. Тож розбіжність («підняли стовпець, забули код» або
    /// навпаки) не ловить ніщо, крім цього тесту — і тому обидва числа тут
    /// літерали.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Межа_стовпця_і_межа_в_коді_це_одне_й_те_саме_число()
    {
        using var db = sql.CreateContext();
        var entity = db.Model.FindEntityType(typeof(JobProgress))!;

        Assert.Equal(400, entity.FindProperty(nameof(JobProgress.Message))!.GetMaxLength());
        Assert.Equal(400, JobProgressMessageCodec.MaxEncodedLength);

        Assert.Equal(2000, entity.FindProperty(nameof(JobProgress.Error))!.GetMaxLength());
        Assert.Equal(2000, IJobProgressStore.MaxErrorLength);
    }
}
