// src/Ecr.Application/Ports/IJobQueue.cs
using Ecr.Domain.Entities.Integration;

namespace Ecr.Application.Ports;

/// <summary>
/// Черга фонових задач у базі (<c>MI-02</c>, <c>D14-01</c>, <c>D-206</c>, <c>D-208</c>):
/// джерело істини — рядок <c>itg.JobProgress</c> з <c>Lane IS NOT NULL</c>.
/// </summary>
/// <remarks>
/// ⛔ Рядки з <c>Lane IS NULL</c> — дзеркало Quartz (старий світ), черга їх не
/// бере й не чіпає. Обидва світи живуть в одній таблиці, доки перемикач
/// <c>Jobs:Queue:Mode</c> не переведе всі задачі (F1c).
///
/// ⚠ Усі моменти (<c>AvailableAt</c>, <c>LeaseUntil</c>) — за годинником СУБД
/// (<c>SYSUTCDATETIME()</c>), а не хоста: оренду порівнюють різні машини, і
/// розбіжність їхніх годинників інакше дала б дві «живі» оренди однієї задачі.
///
/// ⚠ Власник оренди — наявний <c>InstanceId</c>, окремої колонки
/// <c>ClaimedBy</c> немає (<c>D-208</c>; умова — після P3).
/// </remarks>
public interface IJobQueue
{
    /// <summary>
    /// Ставить задачу в ПОТОЧНІЙ транзакції <c>EcrDbContext</c>: відкат
    /// бізнес-зміни відкочує й постановку.
    /// </summary>
    /// <remarks>
    /// Наявна <c>Queued</c> на той самий <see cref="JobEnqueueRequest.TargetKey"/>
    /// поглинає постановку (<see cref="JobEnqueueOutcome.CoalescedIntoQueued"/>):
    /// той самий <c>JobId</c>, payload НЕ оновлюється, <c>AvailableAt</c> — MIN.
    /// Інваріант: payload задачі з ціллю визначається самою ціллю.
    /// <para>
    /// ⚠ Виняток (O1): код задачі з масивом злиття (<c>IFormulaRecalculationJob</c>,
    /// <see cref="FormulaRecalculationTarget.MergedArrayPath"/>) — масив нового
    /// payload дописується в наявну; не вміщається в <see cref="JobQueueLimits.MaxPayloadLength"/>
    /// — наявна відчіпляється від цілі (лишається в черзі як є), нова стає на ціль.
    /// Те саме злиття — в <see cref="RequeueAsync"/>, коли повернуту поглинає задача позаду.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">Лейн не з <see cref="JobLanes.All"/>.</exception>
    public Task<JobEnqueueResult> EnqueueAsync(JobEnqueueRequest request, CancellationToken ct);

    /// <summary>
    /// Бере одну задачу з лейнів: спершу прострочені <c>Running</c>
    /// (переклейм), далі <c>Queued</c>, лише якщо на ціль немає <c>Running</c>.
    /// </summary>
    /// <returns><c>null</c> — брати нічого (включно з 2601/2627 при переході в <c>Running</c>).</returns>
    public Task<ClaimedJob?> ClaimAsync(
        IReadOnlyCollection<string> lanes, string owner, TimeSpan lease, CancellationToken ct);

    /// <summary>Подовжує оренду; заразом повідомляє про запит скасування.</summary>
    public Task<LeaseState> RenewAsync(JobClaimToken claim, TimeSpan lease, CancellationToken ct);

    /// <summary>
    /// Fencing видимості: у ВІДКРИТІЙ транзакції бере X-лок на рядок задачі за
    /// токеном і тримає його до коміту.
    /// </summary>
    /// <returns><c>false</c> — оренду втрачено; викликач відкочує транзакцію.</returns>
    public Task<bool> FenceAsync(JobClaimToken claim, CancellationToken ct);

    /// <summary>Завершує задачу успіхом; <c>false</c> — оренду втрачено.</summary>
    public Task<bool> CompleteAsync(JobClaimToken claim, CancellationToken ct);

    /// <summary>Завершує задачу провалом; <c>false</c> — оренду втрачено.</summary>
    public Task<bool> FailAsync(JobClaimToken claim, string reason, string? errorCode, CancellationToken ct);

    /// <summary>
    /// Повертає задачу в чергу із затримкою (ретрай виконавця). Наявна
    /// <c>Queued</c> позаду на ту саму ціль поглинає її.
    /// </summary>
    public Task<bool> RequeueAsync(JobClaimToken claim, TimeSpan delay, CancellationToken ct);

    /// <summary>
    /// Відкладає задачу (O1): те саме, що <see cref="RequeueAsync"/>, але спроба НЕ
    /// рахується — <c>Attempt</c> повертається до значення до захоплення,
    /// <c>ReclaimCount</c> не змінюється.
    /// </summary>
    /// <remarks>
    /// ⚠ Для задачі, яка не почала роботу, бо ресурс зайнятий (лок документа
    /// перерахунку): вона звільняє слот виконавця замість чекати, і хоч скільки
    /// разів відкладеться — ретраїв не вичерпує.
    /// </remarks>
    /// <returns><c>false</c> — оренду втрачено.</returns>
    public Task<bool> DeferAsync(JobClaimToken claim, TimeSpan delay, CancellationToken ct);

    /// <summary>Закриває задачу станом <c>Cancelled</c> після запиту скасування.</summary>
    public Task<bool> AcknowledgeCancelAsync(JobClaimToken claim, CancellationToken ct);

    /// <summary>Скасування: <c>Queued</c> — одразу, <c>Running</c> — позначкою для виконавця.</summary>
    public Task<CancelOutcome> RequestCancelAsync(string jobId, CancellationToken ct);

    /// <summary>Чи просили скасувати задачу.</summary>
    public Task<bool> IsCancelRequestedAsync(string jobId, CancellationToken ct);

    /// <summary>Ручний перезапуск завершеної задачі: нова серія спроб і переклеймів.</summary>
    public Task<bool> RestartAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// Закриває прострочені <c>Running</c>, яких <see cref="ClaimAsync"/> уже не
    /// переклеймить: вичерпала <paramref name="maxReclaims"/> — <c>Failed</c> з
    /// ключем причини (отруйна задача, правка Б «Аудиту»); просили скасувати —
    /// <c>Cancelled</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Просту прострочену оренду в <c>Queued</c> не повертає: її переклеймлює
    /// сам <see cref="ClaimAsync"/> (той самий <c>JobId</c>, <c>ReclaimCount</c> + 1).
    /// </remarks>
    /// <returns>Скільки рядків змінено.</returns>
    public Task<int> ExpireAsync(int maxReclaims, CancellationToken ct);
}

/// <summary>
/// Лейни черги — ФІКСОВАНІ константи. Рядковий літерал лейна поза цим класом
/// забороняє сторож <c>JobLaneTests</c>.
/// </summary>
public static class JobLanes
{
    /// <summary>Усе, що не має власного пулу виконавців.</summary>
    public const string Default = "default";

    /// <summary>Перерахунок (<c>IRecalculationJob</c>): окремий пул воркерів (<c>D-206</c>).</summary>
    public const string Recalc = "recalc";

    /// <summary>Межа стовпця <c>itg.JobProgress.Lane</c> (<c>varchar(32)</c>).</summary>
    public const int MaxLength = JobProgress.MaxLaneLength;

    /// <summary>Усі відомі лейни.</summary>
    public static IReadOnlyList<string> All { get; } = [Default, Recalc];

    /// <summary>Чи лейн відомий (порівняння точне, з урахуванням регістру).</summary>
    public static bool IsKnown(string? lane) => lane is not null && All.Contains(lane, StringComparer.Ordinal);
}

/// <summary>Межі черги.</summary>
public static class JobQueueLimits
{
    /// <summary>
    /// Скільки разів задачу можна переклеймити після втраченої оренди, перш ніж
    /// <see cref="IJobQueue.ExpireAsync"/> закриє її <c>Failed</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Лічильник — окрема колонка <c>ReclaimCount</c>, а НЕ <c>Attempt</c>
    /// (<c>D-208</c>). <c>Attempt</c> — серія ретраїв ВИКОНАВЦЯ після винятку
    /// (4 спроби, 30/60/120 с, <c>IsWorthRetrying</c>), її бачить користувач як
    /// «спробу N» (BE-08). Переклейм — смерть процесу чи втрачена оренда: винятку
    /// немає, класифікувати нічого. Спільний лічильник валив би задачу, яка двічі
    /// впала на мережі й один раз пережила розгортання, а отруйна задача, що
    /// вбиває процес, забирала б хост чотири рази замість <see cref="MaxReclaims"/>.
    /// </remarks>
    public const int MaxReclaims = 3;

    /// <summary>Типова оренда (<c>D-208</c>).</summary>
    public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(2);

    /// <summary>Типовий інтервал подовження оренди.</summary>
    public static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(30);

    /// <summary>Межа payload у символах (≈ 1 МБ у UTF-16).</summary>
    public const int MaxPayloadLength = 512 * 1024;
}

/// <summary>Запит на постановку задачі.</summary>
/// <param name="JobCode">Код задачі (тип маркера).</param>
/// <param name="Lane">Лейн з <see cref="JobLanes"/>.</param>
/// <param name="PayloadJson">Аргументи задачі, JSON.</param>
/// <param name="TargetKey">
/// Ціль <c>{ТипМаркера}~{ціль}</c>; <c>null</c> — задача без коалесценції
/// (дельти перерахунку до <c>CAL-01</c>).
/// </param>
/// <param name="CreatedByUserId">Автор; <c>null</c> — системна (Q-156).</param>
/// <param name="CorrelationId">Кореляція запиту-постановника.</param>
/// <param name="DocumentId">Документ задачі.</param>
/// <param name="Delay">Відкласти доступність; <c>null</c> — одразу.</param>
/// <param name="SupersedeRunning">
/// <c>true</c> — позначити <c>Running</c> на ту саму ціль до скасування
/// (<c>EnqueueExclusive</c>, H-23c); PATCH — <c>false</c>.
/// </param>
public sealed record JobEnqueueRequest(
    string JobCode,
    string Lane,
    string PayloadJson,
    string? TargetKey = null,
    int? CreatedByUserId = null,
    string? CorrelationId = null,
    long? DocumentId = null,
    TimeSpan? Delay = null,
    bool SupersedeRunning = false);

/// <summary>Що зробила постановка.</summary>
public enum JobEnqueueOutcome
{
    /// <summary>Новий рядок.</summary>
    Created,

    /// <summary>Злито з наявною <c>Queued</c> на ту саму ціль.</summary>
    CoalescedIntoQueued,
}

/// <summary>Результат постановки.</summary>
/// <param name="JobId">Задача, яку опитує клієнт (при коалесценції — наявна).</param>
/// <param name="Outcome">Що сталося.</param>
public sealed record JobEnqueueResult(string JobId, JobEnqueueOutcome Outcome);

/// <summary>Оренда задачі: пара «задача + токен захоплення».</summary>
/// <param name="JobId">Задача.</param>
/// <param name="Token">Токен, виданий <see cref="IJobQueue.ClaimAsync"/>; кожен переклейм — новий.</param>
public sealed record JobClaimToken(string JobId, Guid Token);

/// <summary>Захоплена задача.</summary>
/// <param name="Claim">Оренда.</param>
/// <param name="JobCode">Код задачі.</param>
/// <param name="Lane">Лейн.</param>
/// <param name="PayloadJson">Аргументи.</param>
/// <param name="TargetKey">Ціль або <c>null</c>.</param>
/// <param name="Attempt">Спроба виконавця, від 1 (BE-08).</param>
/// <param name="ReclaimCount">Скільки разів задачу переклеймили після втраченої оренди.</param>
/// <param name="Reclaimed">Узято прострочену <c>Running</c>, а не <c>Queued</c>.</param>
/// <param name="LeaseUntil">Кінець оренди за годинником СУБД.</param>
/// <param name="CreatedByUserId">Автор.</param>
/// <param name="CorrelationId">Кореляція постановки.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="QueueWaitMs">
/// Скільки мілісекунд задача була ГОТОВА (від <c>AvailableAt</c>) і чекала, поки її візьмуть, —
/// за годинником СУБД (<c>ФВ-12.2</c>); <c>null</c> для переклейму простроченої <c>Running</c>
/// (тоді «чекання» — це збій оренди, а не черга) і там, де черга значення не дає.
/// </param>
public sealed record ClaimedJob(
    JobClaimToken Claim,
    string JobCode,
    string Lane,
    string? PayloadJson,
    string? TargetKey,
    int Attempt,
    int ReclaimCount,
    bool Reclaimed,
    DateTime LeaseUntil,
    int? CreatedByUserId,
    string? CorrelationId,
    long? DocumentId,
    long? QueueWaitMs = null);

/// <summary>Стан оренди після <see cref="IJobQueue.RenewAsync"/>.</summary>
public enum LeaseState
{
    /// <summary>Оренду подовжено.</summary>
    Held,

    /// <summary>Подовжено, але задачу просили скасувати.</summary>
    CancelRequested,

    /// <summary>Оренду втрачено (токен чужий або задача вже не <c>Running</c>).</summary>
    Lost,
}

/// <summary>Результат <see cref="IJobQueue.RequestCancelAsync"/>.</summary>
public enum CancelOutcome
{
    /// <summary>Задачі немає.</summary>
    NotFound,

    /// <summary><c>Queued</c> → <c>Cancelled</c> одразу.</summary>
    Cancelled,

    /// <summary><c>Running</c>: позначено <c>CancelRequestedAt</c>, закриє виконавець.</summary>
    CancelRequested,

    /// <summary>Задача вже завершена.</summary>
    AlreadyFinished,
}

/// <summary>
/// Оренда задачі, яку виконує поточний scope (ambient). Ім'я не
/// <c>IJobExecutionContext</c> — воно зайняте Quartz.
/// </summary>
public interface IJobLeaseContext
{
    /// <summary>Оренда поточної задачі; <c>null</c> — scope виконується не з черги.</summary>
    public JobClaimToken? Current { get; }
}
