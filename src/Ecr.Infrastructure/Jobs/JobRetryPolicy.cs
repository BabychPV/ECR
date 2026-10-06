// src/Ecr.Infrastructure/Jobs/JobRetryPolicy.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Ретраї виконавця фонової задачі: скільки, коли і після чого (D-134, №11 T10 #40).
/// </summary>
/// <remarks>
/// ⛔ ОДНЕ визначення на обидва виконавці — <see cref="QuartzJobAdapter"/> і
/// <c>JobWorker</c> (черга в базі, MI-02). Дві копії розійшлися б на першій
/// же правці переліку «не варто повторювати», і та сама задача поводилася б
/// по-різному залежно від <c>Jobs:Queue:Mode</c>.
/// </remarks>
public static class JobRetryPolicy
{
    /// <summary>
    /// Скільки РЕТРАЇВ (не спроб) дозволено після першого провалу.
    /// </summary>
    /// <remarks>
    /// ⚠ Судження, не факт із документа (жоден тікет не називає число):
    /// три ретраї покривають типову транзієнтну відмову (дедлок, обрив
    /// з'єднання з SQL Server) без нескінченного спаму на систематично
    /// зламаній задачі. Значення суто внутрішнє — конфігурації, яку читав би
    /// хтось іззовні, тут немає.
    /// </remarks>
    public const int MaxRetryAttempts = 3;

    /// <summary>Базова затримка експоненційного відступу: 30 с, 60 с, 120 с.</summary>
    public static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Затримка перед ретраєм номер <paramref name="retry"/> (від 1): 30·2^(n-1) с.
    /// </summary>
    /// <remarks>
    /// ⚠ Експоненційний, а не лінійний відступ: транзієнтна відмова джерела
    /// (SQL Server під навантаженням) мусить мати час розвантажитися, а не
    /// отримувати три удари поспіль за секунди.
    /// </remarks>
    public static TimeSpan DelayBefore(int retry)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retry, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(retry, MaxRetryAttempts);

        return TimeSpan.FromTicks(RetryBaseDelay.Ticks * (1L << (retry - 1)));
    }

    /// <summary>Чи повторювати задачу, яка вже мала <paramref name="retriesDone"/> ретраїв.</summary>
    public static bool ShouldRetry(int retriesDone, Exception ex)
        => retriesDone < MaxRetryAttempts && IsWorthRetrying(ex);

    /// <summary>
    /// Чи має сенс повторювати задачу після цього винятку.
    /// </summary>
    /// <remarks>
    /// ⛔ Перелічені типи — це ВЕРДИКТ про вже збережений стан, а не збій
    /// дороги до нього: «зріз за період уже поданий» (<c>ECR-RPT-0409</c>),
    /// «сутності немає», «права немає», «джерело не пускає» (<c>H-20</c>).
    /// Той самий стан через 30 с дасть той самий вердикт, тож три ретраї
    /// (30+60+120 = 210 с) лише ховають причину: користувач увесь цей час
    /// бачить «виконується», а справжнє пояснення доїжджає аж наприкінці.
    /// <para>
    /// ⛔ Розрізнення — лише за ТИПОМ винятку, ніколи за текстом
    /// повідомлення: текст пишуть люди, і список за підрядком мовчки
    /// перестане працювати від першої ж правки формулювання.
    /// </para>
    /// <para>
    /// ⚠ <see cref="BusinessRuleException"/> повторюється ЛИШЕ з кодом
    /// «недоступно, спробуйте пізніше» (<see cref="TransientRuleCodes"/>): ним
    /// із адаптерів збору приїжджає <c>ECR-INT-0503</c> («джерело недоступне»),
    /// тобто рівно та транзієнтна відмова, заради якої ретрай і будували. Решта
    /// кодів — вердикт про дані чи налаштування (<c>ECR-INT-0422</c> «запит не
    /// налаштовано», «одиниця змінилась», <c>ECR-UOM-0422</c>, <c>ECR-IMP-0422</c>):
    /// раніше вони теж ретраїлись тричі. Код — константа каталогу, не текст
    /// повідомлення, тож заборона нижче не порушується.
    /// </para>
    /// <para>
    /// ⛔ <see cref="SourceResponseTooLargeException"/> — виняток із цього
    /// правила: код у нього <c>ECR-INT-0503</c> (контракт), але завелика
    /// відповідь від повтору не меншає (Н-Л4).
    /// </para>
    /// <para>
    /// ⚠ <see cref="ConcurrencyConflictException"/> тут НЕМАЄ навмисно —
    /// конфлікт версій минає сам, щойно повтор перечитає свіжий стан.
    /// </para>
    /// <para>
    /// ⚠ <see cref="JobLeaseLostException"/> — теж ні: оренду вже тримає інший
    /// виконавець, і повтор тут означав би другу копію тієї самої роботи.
    /// </para>
    /// <para>
    /// ⛔ <see cref="InsufficientExecutionStackException"/> (L7-01) — ні: це сторож
    /// стека обходу дерева виразу, а дерево від повтору не мілішає. Раніше задача
    /// тричі повторювала той самий детермінований збій (210 с «виконується») і
    /// падала з <c>ECR-SYS-0500</c>; тепер — одразу, з <c>ECR-EXPR-0422</c>.
    /// </para>
    /// </remarks>
    public static bool IsWorthRetrying(Exception ex) => ex switch
    {
        SourceResponseTooLargeException => false,
        InsufficientExecutionStackException => false,
        BusinessRuleException rule => TransientRuleCodes.Contains(rule.ErrorCode),
        DomainException
            or NotFoundException
            or AccessDeniedException
            or SourceAuthenticationException
            or JobLeaseLostException => false,
        _ => !JobFailureText.IsConstraintViolation(ex),
    };

    /// <summary>
    /// Коди <see cref="BusinessRuleException"/>, що означають «зараз недоступно»
    /// і минають самі: джерело лежить (<c>ECR-INT-0503</c>), система архівує або
    /// планувальник ще не піднявся (<c>ECR-SYS-0503</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Явним переліком, а не розбором цифр <c>0503</c>: та сама причина, що й
    /// у <c>ExceptionHandlingMiddleware</c> — одна помилка в правилі розбору
    /// тихо переназначила б поведінку всьому каталогу.
    /// </remarks>
    public static readonly IReadOnlySet<string> TransientRuleCodes =
        new HashSet<string>(StringComparer.Ordinal) { ErrorCodes.SourceUnavailable, ErrorCodes.Archiving };

    /// <summary>
    /// Код каталогу для провалу (BE-08): власний код доменної чи прикладної
    /// помилки, інакше — <see cref="ErrorCodes.Internal"/> (непередбачена).
    /// </summary>
    public static string ErrorCodeOf(Exception ex) => SafeErrorText.CodeOf(ex);

    /// <summary>
    /// Конверт прогресу «задача повторить спробу» (<c>jobs.retryScheduled</c>, Q-326),
    /// уже закодований у межах стовпця.
    /// </summary>
    /// <remarks>
    /// ⛔ Через <see cref="JobProgressMessageCodec.EncodeWithinLimit"/>, не
    /// <c>Encode</c>: український текст винятку в JSON екранується по шість
    /// символів на літеру, і конверт легко переростав <c>nvarchar(400)</c>
    /// (<c>tools/smoke.ps1</c>, крок 23: <c>Msg 2628</c>, задача вічно
    /// «виконується»).
    /// </remarks>
    public static string RetryScheduledMessage(int nextRetry, TimeSpan delay, Exception ex, string correlationId)
        => JobProgressMessageCodec.EncodeWithinLimit(
            new JobProgressMessageEnvelope(
                "jobs.retryScheduled",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["attempt"] = nextRetry.ToString(CultureInfo.InvariantCulture),
                    ["max"] = MaxRetryAttempts.ToString(CultureInfo.InvariantCulture),
                    ["delaySeconds"] = delay.TotalSeconds.ToString("0", CultureInfo.InvariantCulture),
                    ["error"] = JobFailureText.For(ex, correlationId),
                }),
            "error");

    /// <summary>Текст провалу для <c>/jobs</c>: без стека, без тексту винятку БАЗИ (V-03), у межах стовпця.</summary>
    public static string FailureText(Exception ex, string correlationId)
        => JobProgressMessageCodec.Shorten(JobFailureText.For(ex, correlationId), IJobProgressStore.MaxErrorLength);
}
