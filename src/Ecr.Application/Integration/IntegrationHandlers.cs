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
    ICurrentUser currentUser,
    IUiStringCatalog catalog)
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
    /// <para>
    /// ⚠ <c>Q-326</c> (лишалося відкладеним з lane6 медіум-аудиту, `Q-325`):
    /// <c>status.Message</c> резолвиться каталогом рядків ТУТ, а не в момент
    /// запису прогресу. Задача пише структурований конверт
    /// (<see cref="JobProgressMessageEnvelope"/>) без відомої мови ЧИТАЧА;
    /// цей обробник — request-scope з `[Authorize]`, де мова читача
    /// (<c>currentUser.Language</c>) вже точно відома. Стара пряма форма
    /// запису (готовий український текст ДО цієї картки) і нетекстові дані
    /// в тому самому стовпці (<c>exportId</c> у 100 % `ExcelExportJob`)
    /// проходять крізь резолвер без змін (<see cref="JobProgressMessageResolver"/>).
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

        var resolvedMessage = await JobProgressMessageResolver
            .ResolveAsync(catalog, currentUser.Language, status.Message, ct)
            .ConfigureAwait(false);

        return status with { Message = resolvedMessage };
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
    public const int MaxLimit = 50;

    /// <summary>
    /// Стани, які задача може мати в <c>itg.JobProgress</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Невідомий стан — це <c>422</c>, а не порожній перелік. Порожній
    /// перелік на друкарську помилку у фільтрі читається як «таких задач
    /// немає», тобто бреше рівно там, де людина шукає збій.
    /// <para>
    /// ⚠ Рядки звірені з тим, ХТО їх пише: <c>JobProgress.Queue</c> →
    /// <c>Queued</c>, <c>Begin</c> → <c>Running</c>, <c>Finish</c> →
    /// <c>Succeeded</c>/<c>Failed</c>/<c>Cancelled</c>
    /// (<c>IntegrationLogs.cs</c>, <c>QuartzJobAdapter.cs</c>).
    /// <c>Unknown</c> і <c>Unavailable</c> сюди не входять: це відповіді
    /// планувальника про ВІДСУТНІСТЬ запису, а не стани, які можна знайти в
    /// переліку.
    /// </para>
    /// </remarks>
    public static readonly string[] KnownStates =
        ["Queued", "Running", "Succeeded", "Failed", "Cancelled"];

    /// <summary>
    /// Останні задачі, найновіші перші.
    /// </summary>
    /// <param name="state">Стан із <see cref="KnownStates"/>; <c>null</c> — будь-який.</param>
    /// <param name="code">Код (тип) задачі; <c>null</c> — будь-який.</param>
    /// <param name="mine">
    /// <c>true</c> — лише ВЛАСНІ задачі поточного користувача, без права
    /// <c>System.ViewHealth</c>.
    /// </param>
    /// <param name="limit">Скільки повернути, 1…<see cref="MaxLimit"/>; <c>null</c> — стеля.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ <b>Межа доступу, а не зручність</b> (та сама, що в
    /// <see cref="GetJobStatusHandler"/> і <see cref="CancelJobHandler"/>,
    /// Q-156). Автор бачить СВОЇ задачі без <c>System.ViewHealth</c> — права
    /// на стан СИСТЕМИ: інакше оператор, який щойно отримав <c>jobId</c> у
    /// відповіді <c>202</c>, не має жодного способу побачити перелік власних
    /// перерахунків, і шухляда «Мої задачі» порожня для всіх, крім
    /// адміністраторів.
    /// <para>
    /// ⛔ Ідентифікатор власника береться ЛИШЕ з <c>ICurrentUser</c>. У цього
    /// методу немає параметра, яким можна назвати іншого автора, і в дії
    /// контролера теж — тому підставити чужий ідентифікатор нічим: ані
    /// параметром, ані заголовком. Параметр «чиї задачі» перетворив би
    /// звільнення від права на спосіб читати чужу чергу.
    /// </para>
    /// <para>
    /// ⛔ Без <c>mine</c> і без права — <c>403</c>, а НЕ порожній перелік.
    /// Порожній перелік означає «задач немає» і є неправдою: задачі є, їх
    /// просто не можна показувати цьому читачеві.
    /// </para>
    /// </remarks>
    /// <exception cref="AccessDeniedException">Анонім, або чужі задачі без права.</exception>
    /// <exception cref="BusinessRuleException">Невідомий стан або розмір поза межами.</exception>
    public async Task<IReadOnlyList<JobSummary>> HandleAsync(
        string? state, string? code, bool mine, int? limit, CancellationToken ct)
    {
        // ⚠ Анонім не має «своїх» задач за визначенням: `mine` для нього не
        // послаблення права, а порожнє поняття. Тому 401 стоїть ПЕРЕД
        // розгалуженням — інакше `?mine=true` без сеансу давав би 200 і
        // порожній перелік, тобто відповідь замість запиту на вхід.
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Потрібна автентифікація.",
                         new Dictionary<string, object?>
                         {
                             ["messageKey"] = "err.ECR-AUTH-0401.anonymous",
                         });

        // ⛔ Право вимагається рівно тоді, коли запит виходить за межі власних
        // задач. Перевірити його ДО розгалуження означало б скасувати весь
        // сенс `mine`; не перевіряти взагалі — віддати чергу системи будь-кому.
        if (!mine)
        {
            await ListTemplatesHandler
                .RequireAsync(access, currentUser, GetJobStatusHandler.Permission, ct)
                .ConfigureAwait(false);
        }

        if (state is { Length: > 0 } && !KnownStates.Contains(state, StringComparer.Ordinal))
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Стан «{state}» не існує; відомі: {string.Join(", ", KnownStates)}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.jobState",
                    ["state"] = state,
                });
        }

        if (limit is < 1 or > MaxLimit)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Розмір переліку поза межами 1..{MaxLimit}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.jobLimit",
                    ["limit"] = limit,
                });
        }

        // ⛔ `userId` — з `ICurrentUser`, і це єдине джерело автора в усьому
        // ланцюгу. Мутація «підставити сюди число з запиту» неможлива: такого
        // числа в сигнатурі немає.
        var filter = new JobListFilter(state, code, mine ? userId : null);

        return await jobs.ListRecentAsync(filter, limit ?? MaxLimit, ct).ConfigureAwait(false);
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
    public const string NotFailedErrorCode = ErrorCodes.JobStateConflict;

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
            throw new NotFoundException(ErrorCodes.JobNotFound, $"Задачі {jobId} не існує.");
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
                ErrorCodes.JobNotFound,
                $"Задачу {jobId} не можна перезапустити: деталі задачі не пережили перезапуск сервера.");
        }
    }
}

/// <summary>
/// Скасування фонової задачі. Право <c>System.ViewHealth</c> — або автор
/// ВЛАСНОЇ задачі (Q-156, та сама межа, що в <see cref="GetJobStatusHandler"/>).
/// </summary>
/// <remarks>
/// ⛔ До цього обробника довгу задачу не можна було зупинити НІЯК. Річний
/// перерахунок іде двадцять хвилин, і єдиним способом його обірвати було
/// ВИТІСНЕННЯ — повторний запуск тієї самої роботи
/// (<see cref="IBackgroundJobScheduler.EnqueueExclusiveAsync{TJob}"/>, <c>D2-64</c>),
/// тобто «щоб зупинити, запусти ще раз». Кнопки не було, бо маршрут не був
/// оголошений у контракті §9, а сторож «жоден маршрут поза контрактом»
/// блокуючий; тепер оголошений.
/// <para>
/// ⚠ Межа права — ТА САМА, що в <see cref="GetJobStatusHandler"/>, а не та, що
/// в <see cref="RestartJobHandler"/>. Перезапуск — втручання в чергу системи
/// (хтось полагодив причину провалу й вирішує за всіх, що спробувати варто);
/// скасування ВЛАСНОЇ задачі — відмова від роботи, яку ти сам і замовив.
/// Вимагати на неї <c>System.ViewHealth</c> означало б: оператор запустив
/// двадцятихвилинний перерахунок помилково і не має способу його спинити.
/// </para>
/// <para>
/// ⚠ Скасування — ПРОХАННЯ, не вбивство: задача бачить
/// <c>CancellationToken</c> і закриває свій прогін станом <c>Cancelled</c> на
/// найближчій межі батчу (<c>QuartzJobAdapter</c>). Тому відповідь — <c>202</c>,
/// а стан клієнт дочитує тим самим <c>GET /jobs/{jobId}</c>.
/// </para>
/// </remarks>
public sealed class CancelJobHandler(
    IBackgroundJobScheduler jobs,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Код помилки: скасувати можна лише задачу, що ще не завершилась.</summary>
    /// <remarks>
    /// ⚠ Значення збігається з <see cref="RestartJobHandler.NotFailedErrorCode"/>
    /// НАВМИСНО: це одна природа відмови («стан задачі не дозволяє дію») і один
    /// HTTP-статус, а конвеєр (<c>ExceptionHandlingMiddleware</c>) мапить саме
    /// код. Окрема константа лишається тому, що причина в текст відповіді
    /// підставляється різна, і шукати «звідки цей 409» треба від дії, а не від
    /// сусіднього обробника.
    /// </remarks>
    public const string NotActiveErrorCode = ErrorCodes.JobStateConflict;

    /// <summary>Стани, у яких задачу ще є що скасовувати.</summary>
    /// <remarks>
    /// ⚠ Рядки звірені з тим, ХТО їх пише: <c>JobProgress.Queue</c> ставить
    /// <c>Queued</c>, <c>JobProgress.Begin</c> — <c>Running</c>
    /// (<c>IntegrationLogs.cs</c>). Решта (<c>Succeeded</c>, <c>Failed</c>,
    /// <c>Cancelled</c>) термінальні, а <c>Unknown</c>/<c>Unavailable</c> —
    /// відповіді планувальника про відсутність запису, не стани задачі.
    /// </remarks>
    private static readonly string[] Active = ["Queued", "Running"];

    /// <summary>Просить задачу завершитися.</summary>
    /// <param name="jobId">Ідентифікатор задачі.</param>
    /// <param name="ct">Скасування самого запиту (не задачі).</param>
    /// <exception cref="NotFoundException">Задачі з таким ідентифікатором немає.</exception>
    /// <exception cref="AccessDeniedException">Чужа задача без <c>System.ViewHealth</c>.</exception>
    /// <exception cref="BusinessRuleException">Задача вже в термінальному стані.</exception>
    public async Task HandleAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        // ⚠ Кожна відмова цього обробника несе `messageKey` (Q-341): мови
        // продукту — `en`/`ru`/`kz`, української серед них немає, і готове
        // українське речення нижче лишається ЗАПАСНИМ на випадок, коли ключа в
        // каталозі не знайдено. Підстановки йдуть сирими полями поруч, а не
        // вклеєними в текст, — інакше локалізувати нічого.
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Потрібна автентифікація.",
                         new Dictionary<string, object?>
                         {
                             ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite",
                         });

        // ⚠ Існування перевіряється ПЕРЕД правом — той самий порядок, що в
        // `GetJobStatusHandler` (Q-156, Q-179/Q-180): інакше невідомий `jobId`
        // завжди падав би на «немає права», ховаючи справжню причину за 403.
        var status = await jobs.GetStatusAsync(jobId, ct).ConfigureAwait(false);

        if (string.Equals(status.State, "Unknown", StringComparison.Ordinal))
        {
            throw new NotFoundException(
                ErrorCodes.JobNotFound, $"Задачі {jobId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-JOB-0404.job",
                    ["jobId"] = jobId,
                });
        }

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        if (!profile.Has(GetJobStatusHandler.Permission))
        {
            // Автор скасовує СВОЮ задачу без `System.ViewHealth`; чужу — ні.
            // ⚠ `null` (системна задача за розкладом) автором не є нікому:
            // порівняння з `int?` дало б `false`, але покладатися тут на
            // семантику `Nullable` мовчки — не варто, і тест на це є.
            var ownerId = await jobs.GetCreatedByUserIdAsync(jobId, ct).ConfigureAwait(false);

            if (ownerId is null || ownerId.Value != userId)
            {
                throw new AccessDeniedException(
                    "ECR-AUTH-0403", $"Потрібне право {GetJobStatusHandler.Permission}.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-AUTH-0403.jobNotYours",
                        ["permission"] = GetJobStatusHandler.Permission,
                    });
            }
        }

        // ⚠ Стан перевіряється ПІСЛЯ права: «задача вже завершилась» — це
        // відомість про чужу задачу, і віддавати її тому, хто не має права її
        // бачити, означало б зробити з 409 оракул існування.
        if (!Active.Contains(status.State, StringComparer.Ordinal))
        {
            throw new BusinessRuleException(
                NotActiveErrorCode,
                $"Задача {jobId} у стані «{status.State}» — скасовувати нічого.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-JOB-0409.notActive",
                    ["jobId"] = jobId,
                    ["state"] = status.State,
                });
        }

        await jobs.CancelAsync(jobId, ct).ConfigureAwait(false);
    }
}
