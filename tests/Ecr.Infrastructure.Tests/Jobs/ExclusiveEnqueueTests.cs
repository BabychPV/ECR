// tests/Ecr.Infrastructure.Tests/Jobs/ExclusiveEnqueueTests.cs
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Новий перерахунок ВИТІСНЯЄ попередній над тією самою ціллю (<c>H-23c</c>).
/// </summary>
/// <remarks>
/// ⛔ До цього <c>IBackgroundJobScheduler.CancelAsync</c> не кликав ніхто:
/// довгу фонову задачу — річний перерахунок — не можна було зупинити НІЯК,
/// доки вона не завершиться сама. У чинній системі такий перерахунок іде
/// двадцять хвилин; наш із дворічною звіркою буде довшим, і про те, що його не
/// спинити, дізнаються один раз — у робочий день.
///
/// ⚠ Планувальник тут справжній, але НЕ запущений: перевіряється сховище
/// задач, а не їх виконання. Бази для цього не треба, і саме тому перевірка
/// входить у звичайний прогін, а не в інтеграційний.
/// </remarks>
public sealed class ExclusiveEnqueueTests
{
    private static async Task<(QuartzJobScheduler Jobs, IScheduler Quartz)> SchedulerAsync()
    {
        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            // Своє ім'я на кожен тест: спільний інстанс за замовчуванням
            // приніс би в перевірку задачі сусіднього тесту.
            ["quartz.scheduler.instanceName"] = $"ecr-tests-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });

        return (new QuartzJobScheduler(factory), await factory.GetScheduler().ConfigureAwait(false));
    }

    private static async Task<List<string>> KeysAsync(IScheduler scheduler)
    {
        var keys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup()).ConfigureAwait(false);

        return [.. keys.Select(k => k.Name)];
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "H-23c")]
    public async Task Повторний_перерахунок_тієї_самої_цілі_скасовує_попередній()
    {
        var (jobs, quartz) = await SchedulerAsync();
        var target = RecalculateDocumentHandler.TargetOf(700, new PeriodKey(202601));

        var first = await jobs.EnqueueExclusiveAsync<IRecalculationJob>(
            target, new { DocumentId = 700L }, CancellationToken.None);

        var second = await jobs.EnqueueExclusiveAsync<IRecalculationJob>(
            target, new { DocumentId = 700L }, CancellationToken.None);

        var keys = await KeysAsync(quartz);

        // ⛔ Регресія: витіснення прибрали — і два повні перерахунки одного
        // документа за один період ідуть одночасно. Обидва пишуть у
        // `calc.CalculationResult` і обидва перемикають актуальність прогону:
        // числа лишаються правдоподібними, а який прогін переміг — не скаже
        // ніхто.
        Assert.DoesNotContain(first, keys, StringComparer.Ordinal);
        Assert.Contains(second, keys, StringComparer.Ordinal);
        Assert.Single(keys);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "H-23c")]
    public async Task Перерахунок_іншого_періоду_попередній_не_чіпає()
    {
        // ⚠ Ціль — пара «документ + період», а не самий документ. Інакше
        // заповнення грудня скасовувало б перерахунок листопада: це різна
        // робота над різними партиціями, і витісняти її одну одною означало б
        // мовчки не порахувати закритий місяць.
        var (jobs, quartz) = await SchedulerAsync();

        var november = await jobs.EnqueueExclusiveAsync<IRecalculationJob>(
            RecalculateDocumentHandler.TargetOf(700, new PeriodKey(202611)),
            new { DocumentId = 700L }, CancellationToken.None);

        var december = await jobs.EnqueueExclusiveAsync<IRecalculationJob>(
            RecalculateDocumentHandler.TargetOf(700, new PeriodKey(202612)),
            new { DocumentId = 700L }, CancellationToken.None);

        var keys = await KeysAsync(quartz);

        Assert.Contains(november, keys, StringComparer.Ordinal);
        Assert.Contains(december, keys, StringComparer.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Ідентифікатор_задачі_лягає_в_URL_без_кодування()
    {
        // ⛔ Ідентифікатор задачі — це СЕГМЕНТ ШЛЯХУ в `GET /api/v1/jobs/{jobId}`.
        // Доти роздільником тут стояв `#`, а він в URL починає ФРАГМЕНТ: усе
        // після нього до сервера не доїжджає взагалі. Клієнт застосунку
        // рятував себе сам (`encodeURIComponent` у всіх дев'яти місцях), тому
        // з інтерфейсу дефект був невидимий — але `tools/smoke.ps1`, єдина
        // перевірка продукту «як у користувача», падала на кроці 17:
        // `GET /api/v1/jobs/IRecalculationJob#doc1-p202609#46fec230…` повертав
        // 404. Шістнадцять кроків до нього проходили.
        var (jobs, _) = await SchedulerAsync();

        var jobId = await jobs.EnqueueExclusiveAsync<IRecalculationJob>(
            RecalculateDocumentHandler.TargetOf(700, new PeriodKey(202609)),
            new { DocumentId = 700L },
            CancellationToken.None);

        // ⚠ Твердження — не «немає ґратки», а сильніше й точніше: рядок
        // ПРОХОДИТЬ кодування незміненим. Перелік заборонених символів
        // довелося б доповнювати щоразу, коли хтось вигадає новий роздільник;
        // тотожність із власним закодованим виглядом ловить будь-який із них.
        Assert.Equal(jobId, Uri.EscapeDataString(jobId));
    }
}
