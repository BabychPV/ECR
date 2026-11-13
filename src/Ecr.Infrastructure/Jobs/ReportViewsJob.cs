// src/Ecr.Infrastructure/Jobs/ReportViewsJob.cs
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// ✎ P-4b (D-479): перегенерація вʼюх SSRS <c>rpt.v_*</c> шаблону у фоні після публікації його версії —
/// тим самим <see cref="IReportViewGenerator"/> (ті самі процедури), що доти кликала публікація в HTTP-запиті.
/// </summary>
/// <remarks>
/// ⛔ P-4b P2 (F13 на великому бекапі: 25 версій — 42 хв, ~4 хв на версію, і злитий повтор стартував одразу): задача НЕ бере
/// всіх версій шаблону. <c>_vN</c> будуються лише для версій, що чекають (<see cref="IReportViewStatus.DrainWaitingVersions"/> —
/// набір наповнює публікація і він переживає злиття: payload, який злиття замінило, не губить своєї версії) і версії з
/// payload; лише ті з них, що ще Published/Deprecated у цьому шаблоні. Набір порожній (рестарт застосунку, старий payload) —
/// лише поточна Published версія. Стабільні вʼюхи шаблону (процедура з версією перебудовує їх для всього шаблону) — ОДИН
/// раз наприкінці прогону, а не на кожну версію.
/// ⚠ Блоку версії задача не бере й нічого не пише в <c>cfg.*</c>: публікація, правка чернетки чи друга публікація
/// того самого шаблону під час прогону не чекають на неї.
/// ⚠ Збій видно двічі: генератор фіксує причину для картки <c>reportviews</c> (<see cref="IReportViewStatus"/>), а
/// задача кидає далі — у черзі вона <c>Failed</c> з причиною, і її можна повторити («Restart»): забрані версії
/// повертаються в набір, тож повтор збудує їх знову. Картка «оновлюються» знімається в будь-якому разі
/// (<see cref="IReportViewStatus.Settled"/>) — якщо за прогін не з'явилась новіша позначка (тоді чекає злитий повтор,
/// і знімає вже він).
/// </remarks>
public sealed partial class ReportViewsJob(
    EcrDbContext db,
    IReportViewGenerator generator,
    IReportViewStatus? status = null) : IReportViewsJob
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var task = Parse(payload);

        // ✎ P-4b P3: усі шаблони (старт застосунку) — одна генерація по всіх, лише якщо входи змінилися, зі стелею команди
        // ReportViewGenerator.AllTemplatesTimeoutSeconds. Позначки «оновлюються» шаблонів не чіпає (ключ — шаблон).
        if (task.AllTemplates)
        {
            await progress.ReportAsync(0, "all templates (only if changed)", ct).ConfigureAwait(false);
            await generator.GenerateAllAsync(onlyIfChanged: true, ct).ConfigureAwait(false);
            await progress.ReportAsync(100, null, ct).ConfigureAwait(false);
            return;
        }

        // ⚠ Покоління позначки і набір версій — ДО запиту версій: публікація, що закомітилась після цього рядка, ставить
        // новіше покоління, свою версію в набір і злитий повтор, тож Settled нижче картку «оновлюються» не зніме, доки повтор
        // (що забере її версію) не відпрацює.
        var generation = status?.PendingGeneration(task.TemplateId) ?? 0;
        var drained = status?.DrainWaitingVersions(task.TemplateId) ?? [];
        var completed = false;
        try
        {
            var versionIds = await VersionsAsync(task, drained, ct).ConfigureAwait(false);

            // ⚠ Збій однієї версії не відміняє решти (одна задовга таблиця не лишає без вʼюх інші версії) і стабільних вʼюх,
            // але перший кидається наприкінці — задача стає Failed, а не тихо Succeeded.
            Exception? first = null;
            var steps = versionIds.Count + 1;
            for (var i = 0; i < versionIds.Count; i++)
            {
                var versionId = versionIds[i];
                await progress
                    .ReportAsync(i * 100 / steps, $"template {task.TemplateId}, version {versionId}", ct)
                    .ConfigureAwait(false);
                var failed = await TryAsync(() => generator.GenerateVersionViewsAsync(versionId, ct)).ConfigureAwait(false);
                first ??= failed;
            }

            if (versionIds.Count > 0)
            {
                await progress
                    .ReportAsync(versionIds.Count * 100 / steps, $"template {task.TemplateId}, stable views", ct)
                    .ConfigureAwait(false);
                // Опорна версія — будь-яка версія шаблону (процедура бере її шаблон); найновіша з прогону.
                var failed = await TryAsync(() => generator.GenerateStableViewsAsync(task.TemplateId, versionIds[^1], ct))
                    .ConfigureAwait(false);
                first ??= failed;
            }

            if (first is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
            }

            await progress.ReportAsync(100, null, ct).ConfigureAwait(false);
            completed = true;
        }
        finally
        {
            if (!completed && drained.Count > 0)
            {
                status?.RequeueWaitingVersions(task.TemplateId, drained);
            }

            status?.Settled(task.TemplateId, generation);
        }
    }

    /// <summary>
    /// Версії прогону: (набір, що чекав) ∪ (версія з payload), лише Published/Deprecated цього шаблону — ті самі статуси, що
    /// бере процедура (05-rpt-views.sql: Status IN (1, 2), «застаріла версія вʼюх НЕ втрачає»); порожньо — поточна Published.
    /// </summary>
    private async Task<List<int>> VersionsAsync(ReportViewsTask task, IReadOnlyList<int> drained, CancellationToken ct)
    {
        var wanted = new HashSet<int>(drained);
        if (task.TemplateVersionId is { } fromPayload)
        {
            wanted.Add(fromPayload);
        }

        var live = db.TemplateVersions
            .AsNoTracking()
            .Where(v => v.TemplateId == task.TemplateId
                && (v.Status == TemplateVersionStatus.Published || v.Status == TemplateVersionStatus.Deprecated));

        if (wanted.Count > 0)
        {
            var ids = wanted.ToList();
            var found = await live
                .Where(v => ids.Contains(v.Id))
                .OrderBy(v => v.Id)
                .Select(v => v.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (found.Count > 0)
            {
                return found;
            }
        }

        // ⚠ Нічого не чекало (рестарт застосунку стер набір у памʼяті, payload без версії) — лише поточна опублікована, а
        // НЕ всі версії: вʼюхи старих версій уже є (процедура ідемпотентна, але кожна версія коштує хвилини).
        return await live
            .Where(v => v.Status == TemplateVersionStatus.Published)
            .OrderByDescending(v => v.Id)
            .Select(v => v.Id)
            .Take(1)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    private static async Task<Exception?> TryAsync(Func<Task> step)
    {
        try
        {
            await step().ConfigureAwait(false);
            return null;
        }
#pragma warning disable CA1031 // Решта кроків іде далі; перша помилка кидається після них.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return ex;
        }
    }

    /// <summary>Розбирає завдання черги.</summary>
    internal static ReportViewsTask Parse(object? payload)
    {
        if (payload is ReportViewsTask typed)
        {
            return typed;
        }

        var json = payload as string ?? System.Text.Json.JsonSerializer.Serialize(payload);

        var task = System.Text.Json.JsonSerializer.Deserialize<ReportViewsTask>(json, Options);
        return task is { TemplateId: > 0 } || task is { AllTemplates: true }
            ? task
            : throw new InvalidOperationException("Завдання перегенерації вʼюх не розбирається: немає шаблону.");
    }

    /// <summary>
    /// ✎ P-4b P3: крок старту «4b» — ставить задачу «усі шаблони, лише змінене» (ціль <see cref="ReportViewsTarget.All"/>,
    /// злиття) замість синхронної генерації в послідовності старту (F13: на великому бекапі вона впала на стелі 300 с, а
    /// картка лишалась Degraded назавжди).
    /// </summary>
    /// <param name="jobs">Планувальник; <c>null</c> — немає (усічений хост) → синхронно, як до P-4b.</param>
    /// <param name="generator">Генератор (лише для шляху без планувальника).</param>
    /// <param name="status">Стан картки <c>reportviews</c>: збій постановки — збій прогону по всіх.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns><c>true</c> — задачу поставлено.</returns>
    /// <remarks>
    /// ⚠ Збій постановки НЕ переходить у синхронну генерацію (вона й тримала б старт до пів години) — він на картці
    /// (ключ прогону по всіх; знімає його наступний успішний прогін по всіх) і в журналі викликача (виняток не кидається).
    /// Тестам і smoke вʼюхи одразу після старту не потрібні: <c>git grep</c> по tools, tests, e2e — жоден не читає
    /// <c>rpt.v_*</c>, створені стартом (тести інфраструктури кличуть генератор/процедуру самі).
    /// </remarks>
    public static Task<bool> QueueAllOnStartupAsync(
        IBackgroundJobScheduler? jobs, IReportViewGenerator generator, IReportViewStatus? status, CancellationToken ct)
        => QueueAllOnStartupAsync(jobs, generator, status, logger: null, ct);

    /// <summary>Те саме, з журналом: збій постановки — Warning із повним винятком і кореляцією картки.</summary>
    /// <param name="jobs">Планувальник; <c>null</c> — немає (усічений хост) → синхронно, як до P-4b.</param>
    /// <param name="generator">Генератор (лише для шляху без планувальника).</param>
    /// <param name="status">Стан картки <c>reportviews</c>: збій постановки — збій прогону по всіх.</param>
    /// <param name="logger">Журнал старту; <c>null</c> — без запису.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns><c>true</c> — задачу поставлено.</returns>
    public static async Task<bool> QueueAllOnStartupAsync(
        IBackgroundJobScheduler? jobs, IReportViewGenerator generator, IReportViewStatus? status, ILogger? logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(generator);
        if (jobs is null)
        {
            await generator.GenerateAllAsync(onlyIfChanged: true, ct).ConfigureAwait(false);
            return false;
        }

        try
        {
            await jobs
                .EnqueueCoalescedAsync<IReportViewsJob>(ReportViewsTarget.All, new ReportViewsTask(0, AllTemplates: true), ct)
                .ConfigureAwait(false);
            return true;
        }
#pragma warning disable CA1031 // Найкраще зусилля: вʼюхи потрібні SSRS, а не старту застосунку.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            // ⛔ SEC (TIER2, F13 #872 delta P2): картку читає анонімний /health/ready — сирий ex.Message туди не йде
            // (ReportViewQueueFailure); повний виняток — у Warning під тією самою кореляцією (старт — поза запитом).
            var correlation = ReportViewQueueFailure.Correlation(null);
            status?.Failed(ReportViewQueueFailure.Of(null, ex, "Report view regeneration was not queued at startup.", correlation));
            if (logger is not null)
            {
                LogNotQueuedAtStartup(logger, ex, correlation);
            }

            return false;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ReportViewsJob: перегенерацію вʼюх rpt.* на старті не поставлено; кореляція {CorrelationId}. Повтор — перезапуск або EXEC rpt.usp_GenerateStableViews.")]
    private static partial void LogNotQueuedAtStartup(ILogger logger, Exception exception, string correlationId);
}
