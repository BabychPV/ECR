// src/Ecr.Application/Consistency/RunConsistencyCheckHandler.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;

namespace Ecr.Application.Consistency;

/// <summary>
/// Прогін перевірки узгодженості НА ВИМОГУ. Право <c>System.RunJob</c>.
/// </summary>
/// <remarks>
/// ⛔ Це те, що лишилося від <c>BE-30</c> після рішення людини на <c>Q15-03</c>
/// (директива №15, рішення 2): «взяти до відома» знахідку — <b>ні</b>, знахідка
/// зникає сама, коли наступна перевірка проходить. Отже єдиний спосіб зняти
/// полагоджену знахідку з очей — ПРОГНАТИ перевірку ще раз, а до цього
/// обробника зробити це з продукту було нічим: задача ходила лише за нічним
/// розкладом, і людина, що о десятій ранку усунула причину, бачила свою
/// знахідку в переліку до наступної ночі.
///
/// ⛔ Право — <c>System.RunJob</c>, одне з восьми, які сід видає і які не
/// перевіряв ЖОДЕН обробник (<c>BE-28</c>). Право, яке можна видати й яке
/// нічого не відкриває, — брехня в матриці доступу: адміністратор бачить
/// галочку й вірить їй. Цей обробник — перший його викликач.
///
/// ⚠ Причина ОБОВ'ЯЗКОВА і йде в журнал безпеки. Судження, не вимога з
/// директиви: <c>System.RunJob</c> — небезпечне право (у складені ролі не
/// входить, ФВ-6.12), а сам прогін — повний обхід усіх партицій
/// <c>doc.CellValue</c> зі стелею десять хвилин НА ЗАПИТ. Запис «хто і навіщо
/// запустив це вдень» коштує один рядок, а його відсутність перетворює
/// найважчу операцію системи на анонімну.
/// </remarks>
public sealed class RunConsistencyCheckHandler(
    IBackgroundJobScheduler jobs,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IAuditWriter audit,
    IClock clock)
{
    /// <summary>Право, без якого перевірку не запустити (`02-contracts.md` §9).</summary>
    public const string Permission = "System.RunJob";

    /// <summary>Код відмови: перевірка вже в черзі або вже виконується.</summary>
    /// <remarks>
    /// ⚠ Значення збігається з <see cref="CancelJobHandler.NotActiveErrorCode"/>
    /// і <see cref="RestartJobHandler.NotFailedErrorCode"/> НАВМИСНО: це одна
    /// природа відмови («стан черги не дозволяє дію»), один HTTP-статус, і арм
    /// у <c>ExceptionHandlingMiddleware</c> мапить саме код. Нової родини кодів
    /// не заводиться.
    /// </remarks>
    public const string AlreadyRunningErrorCode = "ECR-JOB-0409";

    /// <summary>Тип події в журналі безпеки (<c>aud.SecurityEvent</c>).</summary>
    public const string EventType = "ConsistencyCheckRunRequested";

    /// <summary>Стеля довжини причини.</summary>
    /// <remarks>
    /// ⚠ Число літералом, а не «скільки влізе»: <c>DetailsJson</c> — це
    /// <c>nvarchar(max)</c>, тож базі байдуже, і саме тому межа потрібна ТУТ.
    /// Без неї поле причини приймає вставлений лог на мегабайт, і журнал
    /// безпеки, який читають очима, перестає читатися.
    /// </remarks>
    public const int MaxReasonLength = 400;

    /// <summary>Стани, у яких перевірка вважається незавершеною.</summary>
    /// <remarks>
    /// ⚠ Рядки звірені з тим, ХТО їх пише (<c>IntegrationLogs.cs</c>):
    /// <c>Queue</c> → <c>Queued</c>, <c>Begin</c> → <c>Running</c>. Решта —
    /// термінальні.
    /// </remarks>
    private static readonly string[] InFlightStates = ["Queued", "Running"];

    /// <summary>
    /// Спільний суфікс імен задачі перевірки в <c>itg.JobProgress</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Імен ДВА, і це не недогляд. <c>JobCode</c> — повне ім'я типу, яким
    /// задачу поставили: ручний прогін ставить маркер
    /// (<c>Ecr.Application.Ports.IConsistencyCheckJob</c>), нічний розклад —
    /// конкретний клас (<c>Ecr.Infrastructure.Jobs.ConsistencyCheckJob</c>),
    /// якого прикладний шар не бачить і назвати типом не може. Спільний у них
    /// саме суфікс.
    /// </remarks>
    private const string JobCodeSuffix = "ConsistencyCheckJob";

    /// <summary>Чи цей <c>JobCode</c> належить перевірці узгодженості.</summary>
    /// <param name="jobCode">Код задачі з <c>itg.JobProgress</c>.</param>
    /// <remarks>
    /// ⚠ Звірка за суфіксом крихка сама по собі — перейменування класу зламало
    /// б її мовчки. Тому обидва справжні <c>typeof(...).FullName</c> прибиті
    /// тестом <c>ConsistencyIssuesControllerTests</c>, який бачить і маркер, і
    /// конкретний клас.
    /// </remarks>
    public static bool IsConsistencyCheckCode(string? jobCode)
        => jobCode is not null && jobCode.EndsWith(JobCodeSuffix, StringComparison.Ordinal);

    /// <summary>Ставить перевірку узгодженості в чергу.</summary>
    /// <param name="reason">Причина; обов'язкова, потрапляє в журнал безпеки.</param>
    /// <param name="ct">Скасування запиту (не задачі).</param>
    /// <returns>Ідентифікатор задачі — ним клієнт опитує <c>GET /jobs/{jobId}</c>.</returns>
    /// <exception cref="AccessDeniedException">Анонім або немає <c>System.RunJob</c>.</exception>
    /// <exception cref="BusinessRuleException">Немає причини, або перевірка вже йде.</exception>
    public async Task<string> HandleAsync(string? reason, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        // ⚠ `RequireAsync` уже відмовила анонімові — сюди доходить лише
        // названий користувач; кидок лишається, бо автор події журналу не може
        // бути «невідомо хто».
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Потрібна автентифікація.",
                         new Dictionary<string, object?>
                         {
                             ["messageKey"] = "err.ECR-AUTH-0401.signInRequired",
                         });

        var trimmed = (reason ?? string.Empty).Trim();

        // ⚠ Порожній рядок і пробіли — це «причини немає», а не причина:
        // поле, яке приймає пробіл, перетворює обов'язковість на формальність.
        if (trimmed.Length == 0)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                "Прогін перевірки узгодженості на вимогу потребує причини.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.consistencyRunReasonRequired",
                });
        }

        if (trimmed.Length > MaxReasonLength)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Причина довша за {MaxReasonLength} символів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.consistencyRunReasonTooLong",
                    ["max"] = MaxReasonLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        // ⛔ Друга перевірка того самого стану — це НЕ подвійна робота, від
        // якої можна відмахнутися. Прогін читає всі партиції `doc.CellValue`
        // анти-джойном зі стелею десять хвилин НА ЗАПИТ; два таких прогони
        // одночасно подвоюють навантаження рівно на тих таблицях, через які
        // адміністратор і хвилюється. А записати вони мають те саме: `MERGE`
        // зіставляє знахідки за трійкою (RuleCode, EntityType, EntityId) серед
        // незакритих, тож другий прогін не додасть жодного рядка, якого не
        // додасть перший.
        //
        // ⚠ Тому 409, а не «мовчки віддати чужий jobId під виглядом свого».
        // Прогін, що ЙДЕ, міг початися ДО того, як людина усунула причину, —
        // його результат відповідає на вчорашнє питання. Відмова називає
        // задачу і її стан: клієнт може показати її прогрес або дочекатися
        // кінця і повторити, і обидва варіанти чесні.
        if (await InFlightAsync(ct).ConfigureAwait(false) is { } busy)
        {
            throw new BusinessRuleException(
                AlreadyRunningErrorCode,
                $"Перевірка узгодженості вже виконується: задача {busy.JobId} у стані «{busy.State}».",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-JOB-0409.consistencyCheckRunning",
                    ["jobId"] = busy.JobId,
                    ["state"] = busy.State,
                });
        }

        // ⚠ Автор передається в чергу: без нього той, хто щойно натиснув
        // кнопку, не прочитав би стан ВЛАСНОЇ задачі без `System.ViewHealth`
        // (Q-156) — тобто отримав би `jobId` і `403` на першому ж опитуванні.
        var jobId = await jobs
            .EnqueueAsync<IConsistencyCheckJob>(payload: null, ct, userId)
            .ConfigureAwait(false);

        // ⛔ Журнал пишеться ПІСЛЯ постановки, а не перед нею. `IAuditWriter`
        // комітить власним підключенням одразу (`AuditWriter.CreateCommand`),
        // тож запис перед постановкою лишив би в журналі доказ прогону, якого
        // не сталося, — а журнал, що розходиться з тим, що описує, доказом не
        // є (той самий вибір, що в `ReopenPeriodHandler`).
        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                EventType,
                TargetUserId: null,
                TargetRoleId: null,
                JsonSerializer.Serialize(new { jobId, reason = trimmed }),
                userId,
                currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        return jobId;
    }

    /// <summary>Незавершена перевірка узгодженості; <c>null</c> — такої немає.</summary>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Перелік звужується СТАНОМ, а не кодом задачі: фільтр за кодом у
    /// сховищі — рівність, а кодів тут два (див. <see cref="JobCodeSuffix"/>).
    /// Зате незавершених задач у системі одиниці, тож сторінка на
    /// <see cref="ListJobsHandler.MaxLimit"/> бачить їх усі.
    ///
    /// ⚠ Задача, що померла разом із процесом, черги не тримає: її переводить
    /// у <c>Failed</c> прибирання застряглих (<c>IJobProgressStore.StaleAfter</c>).
    /// Без цього одне аварійне завершення закрило б кнопку назавжди.
    /// </remarks>
    private async Task<JobSummary?> InFlightAsync(CancellationToken ct)
    {
        foreach (var state in InFlightStates)
        {
            var page = await jobs
                .ListRecentAsync(new JobListFilter(State: state), ListJobsHandler.MaxLimit, ct)
                .ConfigureAwait(false);

            if (page.FirstOrDefault(job => IsConsistencyCheckCode(job.JobCode)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
