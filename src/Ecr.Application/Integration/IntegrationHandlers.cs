// src/Ecr.Application/Integration/IntegrationHandlers.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Errors;

namespace Ecr.Application.Integration;

/// <summary>
/// Запуск збору для сутності джерела. Право <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⛔ Парного «опублікувати в AF» не існує: PI AF — виключно джерело (D-44).
/// <para>
/// Збір ідемпотентний за природним ключем: повторний запуск того самого
/// діапазону не дублює даних (ФВ-11.3). Відмова джерела — не збій операції:
/// діапазон іде в наздоганяння, а прогін завершується.
/// </para>
/// </remarks>
public sealed class CollectFromSourceHandler(
    ICollectionStore sources,
    IBackgroundJobScheduler jobs,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на керування інтеграцією (`02-contracts.md` §9).</summary>
    public const string Permission = "Integration.Manage";

    /// <summary>Ставить збір у чергу.</summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="fromUtc">Початок діапазону; <c>null</c> — за розкладом.</param>
    /// <param name="toUtc">Кінець діапазону; <c>null</c> — «зараз».</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Ідентифікатор задачі.</returns>
    /// <exception cref="NotFoundException">Сутності джерела немає або вона вимкнена.</exception>
    public async Task<string> HandleAsync(
        int sourceEntityId, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct)
    {
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⚠ Існування сутності перевіряється ТУТ. Задача, поставлена на
        // неіснуючу сутність, завершилася б помилкою через хвилину, і
        // користувач шукав би причину в джерелі, а не в номері, який щойно
        // ввів.
        _ = await sources.FindSourceEntityAsync(sourceEntityId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.SourceEntityNotFound,
                $"Сутності джерела {sourceEntityId} немає або вона вимкнена.");

        return await jobs
            .EnqueueAsync<ICollectionJob>(new CollectionTask(sourceEntityId, fromUtc, toUtc), ct)
            .ConfigureAwait(false);
    }
}

/// <summary>Завдання на збір.</summary>
/// <param name="SourceEntityId">Сутність джерела.</param>
/// <param name="FromUtc">Початок; <c>null</c> — за <c>LookbackDays</c> розкладу.</param>
/// <param name="ToUtc">Кінець; <c>null</c> — «зараз».</param>
public sealed record CollectionTask(int SourceEntityId, DateTime? FromUtc, DateTime? ToUtc);

/// <summary>
/// Перелік сутностей збору. Право <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⚠ Ендпоінт додано після аудиту (`A7-03`): екран конфігуратора викликав
/// <c>GET /api/v1/sources</c>, якого не існувало, і завжди показував помилку.
/// Таблиця ендпоінтів контракту оголошувала лише запуск збору — але вимога
/// ФВ-13.13 («імена обираються зі списку, а не вводяться руками») без
/// переліку невиконувана.
/// </remarks>
public sealed class ListSourceEntitiesHandler(
    ICollectionStore sources,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на керування інтеграцією (`02-contracts.md` §9).</summary>
    public const string Permission = "Integration.Manage";

    /// <summary>Віддає сутності разом зі станом останнього прогону і прогалиною.</summary>
    /// <param name="ct">Скасування.</param>
    public async Task<IReadOnlyList<SourceEntityStatus>> HandleAsync(CancellationToken ct)
    {
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        return await sources.ListSourceEntitiesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Стан фонової задачі. Право <c>System.ViewHealth</c>.
/// </summary>
/// <remarks>
/// Усе, що довше за ~5 с, іде у фон і повертає <c>jobId</c>; саме через цей
/// шлях інтерфейс показує прогрес.
/// </remarks>
public sealed class GetJobStatusHandler(
    IBackgroundJobScheduler jobs,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на перегляд стану системи (`02-contracts.md` §9).</summary>
    public const string Permission = "System.ViewHealth";

    /// <summary>Стан задачі; <c>null</c> — такої задачі немає.</summary>
    /// <param name="jobId">Ідентифікатор задачі.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Невідома задача — це <c>null</c>, який контролер перетворює на 404, а
    /// не порожній стан: інакше клієнт нескінченно опитував би ідентифікатор,
    /// якого не існує, і показував би вічний прогрес.
    /// <para>
    /// ⛔ Q-156 (конфлікт, Етап I). До цього метод вимагав `System.ViewHealth`
    /// — право на стан СИСТЕМИ — навіть щоб дізнатися стан ВЛАСНОЇ задачі
    /// експорту чи перерахунку: автор отримував <c>jobId</c> у відповіді
    /// <c>202</c> і одразу після — <c>403</c> на першому ж опитуванні
    /// (`S-28`). Рішення людини: автор задачі читає її стан завжди,
    /// `System.ViewHealth` лишається для стеження за ЧУЖИМИ задачами.
    /// Існування перевіряється ПЕРЕД грантом (той самий порядок, що й у
    /// Q-179/Q-180) — інакше невідомий `jobId` завжди впав би на «немає
    /// права», ховаючи справжню причину (задачі просто немає) за 403.
    /// </para>
    /// </remarks>
    public async Task<JobStatus?> HandleAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException("ECR-AUTH-0401", "Потрібна автентифікація.");

        var status = await jobs.GetStatusAsync(jobId, ct).ConfigureAwait(false);

        if (string.Equals(status.State, "Unknown", StringComparison.Ordinal))
        {
            return null;
        }

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        if (!profile.Has(Permission))
        {
            var createdByUserId = await jobs.GetCreatedByUserIdAsync(jobId, ct).ConfigureAwait(false);
            if (createdByUserId != userId)
            {
                throw new AccessDeniedException(
                    "ECR-AUTH-0403", $"Потрібне право {Permission}.",
                    new Dictionary<string, object?> { ["permission"] = Permission });
            }
        }

        return status;
    }
}

/// <summary>
/// Перелік останніх фонових задач. Право <c>System.ViewHealth</c>.
/// </summary>
/// <remarks>
/// ⛔ До цього обробника задачу можна було побачити лише знаючи її GUID
/// (`GET /jobs/{jobId}`): збій перерахунку був видимий у базі й недосяжний з
/// інтерфейсу — оператор бачив «щось не порахувалося» без жодного способу
/// дізнатися, яка задача і чому (директива №09 §6.5, `S-25`; `ФВ-12.4`).
/// </remarks>
public sealed class ListJobsHandler(
    IBackgroundJobScheduler jobs,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Стеля переліку: більше — сторінка, якої тут немає навмисно.</summary>
    /// <remarks>
    /// ⚠ Це журнал того, що ЩОЙНО сталося, не архів: адміністратор дивиться
    /// на нього одразу після дії, а не гортає місяцями назад.
    /// </remarks>
    private const int Limit = 50;

    /// <summary>Останні задачі, найновіші перші.</summary>
    /// <param name="ct">Скасування.</param>
    public async Task<IReadOnlyList<JobSummary>> HandleAsync(CancellationToken ct)
    {
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, GetJobStatusHandler.Permission, ct)
            .ConfigureAwait(false);

        return await jobs.ListRecentAsync(Limit, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Ручний перезапуск проваленої фонової задачі. Право <c>System.ViewHealth</c>
/// (директива №11, T10 #40).
/// </summary>
/// <remarks>
/// ⛔ До цього обробника провалену задачу МІГ повторити лише автоматичний
/// ретрай (<c>QuartzJobAdapter</c>) — а той зупиняється на межі спроб навмисно
/// (D-134): систематично зламана задача не має спамити чергу вічно. Після
/// цього людина, що полагодила причину (недоступне джерело, зайняте
/// з'єднання), не мала способу сказати системі «спробуй знову» — лишалося
/// ставити нову задачу вручну й губити її історію прогресу.
/// <para>
/// ⚠ Право — <c>System.ViewHealth</c>, те саме, що й перегляд і перелік, а не
/// право автора власної задачі (як у <see cref="GetJobStatusHandler"/>):
/// перезапуск — мутація стану системи, а не читання власного результату, і
/// призначений для того, хто відповідає за чергу, а не для будь-кого, хто її
/// поставив.
/// </para>
/// </remarks>
public sealed class RestartJobHandler(
    IBackgroundJobScheduler jobs,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Код помилки: задачу можна перезапустити, лише коли вона провалилась.</summary>
    public const string NotFailedErrorCode = "ECR-JOB-0409";

    /// <summary>Перезапускає провалену задачу.</summary>
    /// <param name="jobId">Ідентифікатор задачі.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Задачі немає, або деталь зникла з планувальника.</exception>
    /// <exception cref="BusinessRuleException">Задача не в стані <c>Failed</c>.</exception>
    public async Task HandleAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        await ListTemplatesHandler
            .RequireAsync(access, currentUser, GetJobStatusHandler.Permission, ct)
            .ConfigureAwait(false);

        var status = await jobs.GetStatusAsync(jobId, ct).ConfigureAwait(false);

        if (string.Equals(status.State, "Unknown", StringComparison.Ordinal))
        {
            throw new NotFoundException("ECR-JOB-0404", $"Задачі {jobId} не існує.");
        }

        if (!string.Equals(status.State, "Failed", StringComparison.Ordinal))
        {
            throw new BusinessRuleException(
                NotFailedErrorCode,
                $"Задача {jobId} у стані «{status.State}», перезапустити можна лише провалену.");
        }

        var restarted = await jobs.RestartAsync(jobId, ct).ConfigureAwait(false);

        if (!restarted)
        {
            // ⚠ Стан у базі каже Failed, а деталі в планувальнику вже немає —
            // сховище Quartz В ПАМ'ЯТІ (D-66) і не пережило перезапуск процесу
            // між провалом і спробою перезапуску.
            throw new NotFoundException(
                "ECR-JOB-0404",
                $"Задачу {jobId} не можна перезапустити: деталі задачі не пережили перезапуск сервера.");
        }
    }
}
