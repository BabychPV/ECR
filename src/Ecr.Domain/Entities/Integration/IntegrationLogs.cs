// src/Ecr.Domain/Entities/Integration/IntegrationLogs.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Integration;

/// <summary>Прогін збору з зовнішнього джерела (<c>itg.CollectionRun</c>).</summary>
public sealed class CollectionRun : Entity<long>
{
    private CollectionRun() { }

    /// <summary>Починає прогін збору.</summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="rangeFrom">Початок інтервалу.</param>
    /// <param name="rangeTo">Кінець інтервалу.</param>
    /// <param name="isCatchUp">Чи це наздоганяння пропущеного вікна.</param>
    /// <param name="triggeredByUserId">Хто запустив; <c>null</c> — за розкладом.</param>
    /// <param name="utcNow">Час початку в UTC.</param>
    public CollectionRun(
        int sourceEntityId,
        DateTime rangeFrom,
        DateTime rangeTo,
        bool isCatchUp,
        int? triggeredByUserId,
        DateTime utcNow)
    {
        SourceEntityId = sourceEntityId;
        RangeFrom = rangeFrom;
        RangeTo = rangeTo;
        IsCatchUp = isCatchUp;
        TriggeredByUserId = triggeredByUserId;
        StartedAt = utcNow;
        Status = "Running";
    }

    public int SourceEntityId { get; private set; }
    public DateTime RangeFrom { get; private set; }
    public DateTime RangeTo { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime? FinishedAt { get; private set; }
    public int PointsRetrieved { get; private set; }
    public string Status { get; private set; } = null!;

    /// <summary>Чи це наздоганяння пропущеного вікна (ІНТ-3.2).</summary>
    public bool IsCatchUp { get; private set; }

    public string? ErrorMessage { get; private set; }
    public int? TriggeredByUserId { get; private set; }

    /// <summary>Фіксує завершення збору.</summary>
    /// <param name="status">"Succeeded" або "Failed".</param>
    /// <param name="pointsRetrieved">Скільки точок отримано.</param>
    /// <param name="utcNow">Час завершення в UTC.</param>
    /// <param name="errorMessage">Текст помилки при провалі.</param>
    /// <remarks>
    /// ⚠ Нуль точок — не «успіх без даних». Джерело, яке нічого не віддало,
    /// і джерело, якого не спитали, для звіту виглядають однаково, а це різні
    /// стани: перший — привід дивитися на джерело, другий — на розклад.
    /// Розрізняє їх саме журнал покриття (ІНТ-3.3).
    /// </remarks>
    public void Complete(string status, int pointsRetrieved, DateTime utcNow, string? errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        Status = status;
        PointsRetrieved = pointsRetrieved;
        FinishedAt = utcNow;
        ErrorMessage = errorMessage;
    }
}

/// <summary>
/// Покритий інтервал збору (<c>itg.CollectionCoverage</c>).
/// </summary>
/// <remarks>
/// ⚠ **Ознака здоров'я — саме журнал покриття, а не тиша** (ІНТ-3.3).
/// Відсутність помилок означає лише те, що ніхто не скаржився; дірка в
/// покритті означає, що даних за проміжок немає — і це видно, лише якщо
/// покриття записується.
/// </remarks>
public sealed class CollectionCoverage : Entity<long>
{
    private CollectionCoverage() { }

    /// <summary>Записує покритий інтервал.</summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="coveredFrom">Початок покриття.</param>
    /// <param name="coveredTo">Кінець покриття.</param>
    /// <param name="collectionRunId">Прогін, що його дав.</param>
    public CollectionCoverage(
        int sourceEntityId, DateTime coveredFrom, DateTime coveredTo, long collectionRunId)
    {
        SourceEntityId = sourceEntityId;
        CoveredFrom = coveredFrom;
        CoveredTo = coveredTo;
        CollectionRunId = collectionRunId;
    }

    /// <summary>Той самий конструктор, але без прогону (для <see cref="Skipped"/>).</summary>
    private CollectionCoverage(int sourceEntityId, DateTime coveredFrom, DateTime coveredTo)
    {
        SourceEntityId = sourceEntityId;
        CoveredFrom = coveredFrom;
        CoveredTo = coveredTo;
        CollectionRunId = null;
    }

    /// <summary>
    /// Причина, чому інтервал НЕ перенесено в комірки (<c>D-118</c>).
    /// </summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="periodKey">Період, якого це стосується.</param>
    /// <param name="status">Статус: <c>SkippedPeriodClosed</c>, <c>ConflictKeptManual</c>.</param>
    /// <param name="details">Пояснення для людини; без стеків (ФВ-6.11).</param>
    /// <param name="utcNow">Момент запису.</param>
    /// <remarks>
    /// ⛔ Це ОКРЕМИЙ вид рядка: інтервал зібрано, але значення не лягли в
    /// комірки. Мовчазний пропуск тут — найдорожчий із можливих: збір
    /// відпрацював, звіт склався, а числа за пізній інтервал у ньому немає, і
    /// дізнаються про це на звірці через місяць.
    ///
    /// ⚠ Записується в ТУ САМУ таблицю покриття, а не в окрему: питання «що з
    /// цим інтервалом» має одну відповідь в одному місці.
    ///
    /// ⛔ Q-186. Раніше тут стояв `collectionRunId: 0` — значення-«заглушка»
    /// проти РЕАЛЬНОГО `FK_CCov_Run` на `itg.CollectionRun.Id`, якого з таким
    /// `Id` не існує НІКОЛИ (`IDENTITY` не видає `0`). Кожен виклик падав на
    /// цьому ключі: подія «пропуск»/«конфлікт» НЕ прив'язана до жодного
    /// прогону збору за визначенням, і мала лишатися `NULL`, а не мати
    /// вигаданий ідентифікатор.
    /// </remarks>
    public static CollectionCoverage Skipped(
        int sourceEntityId, int periodKey, string status, string details, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        return new CollectionCoverage(sourceEntityId, utcNow, utcNow)
        {
            PeriodKey = periodKey,
            Status = status,
            Details = details,
        };
    }

    public int SourceEntityId { get; private set; }
    public DateTime CoveredFrom { get; private set; }
    public DateTime CoveredTo { get; private set; }

    /// <summary>Прогін, що дав це покриття; <c>null</c> — подія «пропуск»/«конфлікт» (<see cref="Skipped"/>).</summary>
    public long? CollectionRunId { get; private set; }

    /// <summary>Період, якого стосується статус; <c>null</c> — звичайне покриття.</summary>
    public int? PeriodKey { get; private set; }

    /// <summary>
    /// Статус матеріалізації; <c>null</c> — інтервал покрито звичайним шляхом.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>null</c> тут не «невідомо», а «нічого незвичайного»: рядків
    /// покриття на порядки більше, ніж пропусків, і заповнювати їх усіх
    /// словом «Ok» означало б платити місцем за відсутність інформації.
    /// </remarks>
    public string? Status { get; private set; }

    /// <summary>Пояснення для людини; без стеків (ФВ-6.11).</summary>
    public string? Details { get; private set; }
}

/// <summary>
/// Прогін архівації (<c>itg.ArchiveRun</c>).
/// </summary>
/// <remarks>
/// ⚠ <see cref="LastDonePeriodKey"/> — не діагностика, а те, що робить
/// архівацію **відновлюваною** (АРХ-3a, D-24). Обрив посередині має
/// продовжуватися з наступної партиції, а не починатися спочатку: рік — це
/// десятки мільйонів рядків, і другий прохід не вкладеться у вікно.
/// </remarks>
public sealed class ArchiveRun : Entity<long>
{
    /// <summary>Напрям: у архів.</summary>
    public const string ToArchive = "ToArchive";

    /// <summary>Напрям: з архіву.</summary>
    public const string FromArchive = "FromArchive";

    private ArchiveRun() { }

    /// <summary>Починає прогін архівації.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="direction"><see cref="ToArchive"/> або <see cref="FromArchive"/>.</param>
    /// <param name="fromPeriodKey">Перший період діапазону.</param>
    /// <param name="toPeriodKey">Останній період діапазону.</param>
    /// <param name="triggeredByUserId">Хто запустив.</param>
    /// <param name="utcNow">Час початку в UTC.</param>
    public ArchiveRun(
        int projectId,
        string direction,
        int fromPeriodKey,
        int toPeriodKey,
        int? triggeredByUserId,
        DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);

        ProjectId = projectId;
        Direction = direction;
        FromPeriodKey = fromPeriodKey;
        ToPeriodKey = toPeriodKey;
        TriggeredByUserId = triggeredByUserId;
        StartedAt = utcNow;
        Status = "Running";
    }

    public int ProjectId { get; private set; }
    public string Direction { get; private set; } = null!;
    public int FromPeriodKey { get; private set; }
    public int ToPeriodKey { get; private set; }

    /// <summary>Останній перенесений період; звідси продовжується після збою.</summary>
    public int? LastDonePeriodKey { get; private set; }

    public DateTime StartedAt { get; private set; }
    public DateTime? FinishedAt { get; private set; }
    public long RowsMoved { get; private set; }

    /// <summary>Три суми джерела: <c>COUNT</c>, <c>CHECKSUM_AGG</c>, <c>SUM</c>.</summary>
    public string? ChecksumSourceJson { get; private set; }

    /// <summary>Ті самі три суми цілі — звіряються перед видаленням джерела.</summary>
    public string? ChecksumTargetJson { get; private set; }

    public string Status { get; private set; } = null!;
    public string? ErrorMessage { get; private set; }
    public int? TriggeredByUserId { get; private set; }

    /// <summary>Фіксує успішно перенесену партицію.</summary>
    /// <param name="periodKey">Період, який щойно завершено.</param>
    /// <param name="rowsMoved">Скільки рядків перенесено.</param>
    /// <remarks>
    /// Викликається ПІСЛЯ звірки контрольних сум цієї партиції: позначити
    /// період завершеним до звірки означало б, що відновлення пропустить
    /// партицію, дані якої не збіглися.
    /// </remarks>
    public void MarkPeriodDone(int periodKey, long rowsMoved)
    {
        LastDonePeriodKey = periodKey;
        RowsMoved += rowsMoved;
    }

    /// <summary>Записує контрольні суми поточної партиції.</summary>
    /// <param name="sourceJson">Суми джерела.</param>
    /// <param name="targetJson">Суми цілі.</param>
    public void RecordChecksums(string? sourceJson, string? targetJson)
    {
        ChecksumSourceJson = sourceJson;
        ChecksumTargetJson = targetJson;
    }

    /// <summary>Фіксує завершення прогону.</summary>
    /// <param name="status">"Succeeded" або "Failed".</param>
    /// <param name="utcNow">Час завершення в UTC.</param>
    /// <param name="errorMessage">Текст помилки при провалі.</param>
    public void Complete(string status, DateTime utcNow, string? errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        Status = status;
        FinishedAt = utcNow;
        ErrorMessage = errorMessage;
    }
}

/// <summary>Прогін службової задачі (<c>itg.MaintenanceRun</c>).</summary>
/// <remarks>
/// Записується **завжди**, зокрема успішний і порожній. Задача, яка мовчить,
/// коли нічого не знайшла, і мовчить, коли не запустилася, — це задача, про
/// зупинку якої дізнаються з наслідків.
/// </remarks>
public sealed class MaintenanceRun : Entity<long>
{
    private MaintenanceRun() { }

    /// <summary>Починає прогін.</summary>
    /// <param name="jobCode">Код задачі.</param>
    /// <param name="utcNow">Час початку в UTC.</param>
    public MaintenanceRun(string jobCode, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobCode);

        JobCode = jobCode;
        StartedAt = utcNow;
        Status = "Running";
    }

    public string JobCode { get; private set; } = null!;
    public DateTime StartedAt { get; private set; }
    public DateTime? FinishedAt { get; private set; }
    public string Status { get; private set; } = null!;

    /// <summary>Що саме знайдено; читається людиною.</summary>
    public string? DetailsJson { get; private set; }

    /// <summary>Фіксує завершення.</summary>
    /// <param name="status">"Succeeded", "Degraded" або "Failed".</param>
    /// <param name="detailsJson">Подробиці знахідок.</param>
    /// <param name="utcNow">Час завершення в UTC.</param>
    public void Complete(string status, string? detailsJson, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        Status = status;
        DetailsJson = detailsJson;
        FinishedAt = utcNow;
    }
}

/// <summary>
/// Прогрес фонової задачі (<c>itg.JobProgress</c>).
/// </summary>
/// <remarks>
/// Живе в базі, а не в пам'яті планувальника: інстансів застосунку кілька, і
/// клієнт, що опитує прогрес, потрапляє не обов'язково на той, який задачу
/// виконує.
/// </remarks>
public sealed class JobProgress
{
    private JobProgress() { }

    /// <summary>Створює запис прогресу.</summary>
    /// <param name="jobId">Ідентифікатор задачі — він же ключ.</param>
    /// <param name="jobCode">Код задачі.</param>
    /// <param name="utcNow">Час початку в UTC.</param>
    public JobProgress(string jobId, string jobCode, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobCode);

        JobId = jobId;
        JobCode = jobCode;
        StartedAt = utcNow;
        UpdatedAt = utcNow;
        State = "Running";
    }

    public string JobId { get; private set; } = null!;
    public string JobCode { get; private set; } = null!;
    public int Percent { get; private set; }
    public string? Message { get; private set; }
    public string State { get; private set; } = null!;
    public DateTime StartedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public string? Error { get; private set; }

    /// <summary>
    /// Ставить стан «у черзі».
    /// </summary>
    /// <param name="utcNow">Момент постановки в UTC.</param>
    /// <remarks>
    /// ⚠ Запис зʼявляється вже при ПОСТАНОВЦІ, а не при запуску. Інакше між
    /// відповіддю <c>202</c> з <c>jobId</c> і фактичним стартом задачі стан
    /// не існує — і клієнт, який опитує його одразу, отримує <c>404</c> на
    /// задачу, яку щойно прийняли.
    /// </remarks>
    public void Queue(DateTime utcNow)
    {
        State = "Queued";
        Percent = 0;
        Message = null;
        Error = null;
        UpdatedAt = utcNow;
    }

    /// <summary>Ставить стан «виконується».</summary>
    /// <param name="utcNow">Момент старту в UTC.</param>
    public void Begin(DateTime utcNow)
    {
        State = "Running";
        Percent = 0;
        Message = null;
        Error = null;
        StartedAt = utcNow;
        UpdatedAt = utcNow;
    }

    /// <summary>Оновлює прогрес.</summary>
    /// <param name="percent">Відсоток 0…100.</param>
    /// <param name="message">Що зараз відбувається.</param>
    /// <param name="utcNow">Момент оновлення в UTC.</param>
    public void Report(int percent, string? message, DateTime utcNow)
    {
        Percent = Math.Clamp(percent, 0, 100);
        Message = message;
        UpdatedAt = utcNow;
    }

    /// <summary>Фіксує завершення задачі.</summary>
    /// <param name="state">"Succeeded" або "Failed".</param>
    /// <param name="error">Текст помилки при провалі.</param>
    /// <param name="utcNow">Момент завершення в UTC.</param>
    public void Finish(string state, string? error, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        State = state;
        Error = error;
        UpdatedAt = utcNow;
        Percent = error is null ? 100 : Percent;
    }
}
