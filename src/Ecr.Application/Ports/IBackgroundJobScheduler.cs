// src/Ecr.Application/Ports/IBackgroundJobScheduler.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Планувальник фонових задач. Порт існує, щоб заміна реалізації
/// (Quartz ↔ Hangfire) коштувала день, а не рефакторинг (D-09): допустимість
/// LGPL — відкрите питання до ІБ.
/// </summary>
public interface IBackgroundJobScheduler
{
    /// <summary>
    /// Чи постановка приєднується до ПОТОЧНОЇ транзакції викликача (MI-02 (в)).
    /// </summary>
    /// <remarks>
    /// ⛔ <c>true</c> — черга в базі: постановка всередині транзакції запису
    /// відкочується разом із нею і стає видимою воркеру лише з її комітом, тож
    /// ставити треба ВСЕРЕДИНІ, останнім оператором. <c>false</c> — черга поза
    /// базою (Quartz у пам'яті): задача стартує раніше за коміт і прочитала б
    /// старі дані або дані, яких після відкату не буде, тож ставити треба ПІСЛЯ
    /// коміту. Типово <c>false</c> — поведінка, що була до MI-02.
    /// </remarks>
    public bool EnlistsInCallerTransaction => false;

    /// <summary>Ставить задачу в чергу негайно.</summary>
    /// <param name="payload">Завдання.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="createdByUserId">
    /// Хто поставив задачу; <c>null</c> — системна задача без автора
    /// (за розкладом, витіснена іншою задачею). Потрібен, щоб автор міг
    /// прочитати стан ВЛАСНОЇ задачі без права <c>System.ViewHealth</c>
    /// (Q-156) — <see cref="GetStatusAsync"/> порівнює це значення з
    /// поточним користувачем.
    /// </param>
    public Task<string> EnqueueAsync<TJob>(object? payload, CancellationToken ct, int? createdByUserId = null)
        where TJob : IBackgroundJob;

    /// <summary>
    /// Ставить задачу в чергу, ВИТІСНЯЮЧИ незавершену задачу того самого типу
    /// й тієї самої цілі.
    /// </summary>
    /// <typeparam name="TJob">Маркер задачі.</typeparam>
    /// <param name="targetKey">
    /// Ціль: те, над чим задача працює (документ і період, проєкт). Дві задачі
    /// з однаковою ціллю — це та сама робота, а не дві різні.
    /// </param>
    /// <param name="payload">Завдання.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатор нової задачі.</returns>
    /// <remarks>
    /// ⛔ Заради цього методу існує <see cref="CancelAsync"/>, у якого доти не
    /// було жодного викликача (<c>H-23c</c>): довгу задачу — річний
    /// перерахунок — не можна було зупинити НІЯК, доки вона не завершиться
    /// сама. У чинній системі такий перерахунок іде двадцять хвилин, а з
    /// дворічною звіркою наш буде довшим; про те, що його не спинити, дізнаються
    /// один раз і в найгірший день.
    ///
    /// ⛔ Витіснення — не оптимізація. Два повні перерахунки одного документа
    /// за один період пишуть у <c>calc.CalculationResult</c> одночасно й обидва
    /// перемикають актуальність прогону: числа лишаються правдоподібними, а
    /// який із двох прогонів переміг — не скаже ніхто.
    ///
    /// ⚠ Скасування — саме скасування, а не переривання потоку: задача бачить
    /// <c>CancellationToken</c> і закриває свій прогін станом <c>Cancelled</c>.
    /// Убита посеред пакета, вона лишила б половину результатів записаними без
    /// жодного сліду про це.
    /// </remarks>
    /// <param name="createdByUserId">Хто поставив задачу; <c>null</c> — системна (Q-156).</param>
    public Task<string> EnqueueExclusiveAsync<TJob>(
        string targetKey, object? payload, CancellationToken ct, int? createdByUserId = null)
        where TJob : IBackgroundJob;

    /// <summary>
    /// Ставить задачу «виконати ПІСЛЯ» на ціль БЕЗ витіснення: незавершена
    /// задача того самого типу й цілі не переривається, а постановка
    /// зливається з тією, що вже чекає.
    /// </summary>
    /// <typeparam name="TJob">Маркер задачі.</typeparam>
    /// <param name="targetKey">
    /// Ціль — той самий сенс і той самий ключ, що в
    /// <see cref="EnqueueExclusiveAsync{TJob}"/>: обидва методи бачать задачі одне одного.
    /// </param>
    /// <param name="payload">Завдання.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="createdByUserId">Хто поставив задачу; <c>null</c> — системна (Q-156).</param>
    /// <returns>
    /// Ідентифікатор задачі, яка виконає роботу: наявної (злиття) або нової.
    /// </returns>
    /// <remarks>
    /// ⛔ Для автоматичних постановок, що йдуть сплесками (автоперерахунок після
    /// матеріалізації PI, HSE301 A4): три сутності, що пишуть в один документ,
    /// дають три постановки, і з <see cref="EnqueueExclusiveAsync{TJob}"/>
    /// кожна переривала б попередній перерахунок — на «гарячому» документі він
    /// не доходив би до кінця ніколи.
    /// <para>
    /// ⚠ Семантика «після» залежить від реалізації: черга в базі ставить
    /// <c>Queued</c> ПОЗАДУ <c>Running</c> (claim не бере її, доки ціль зайнята);
    /// Quartz у пам'яті такого бар'єра не має, тож за наявної задачі на ціль —
    /// у черзі чи виконуваної — нову зараз не ставить і повертає наявну; злиття
    /// з ВИКОНУВАНОЮ позначає її, і після завершення задача ставиться ще раз
    /// (один раз, з payload останньої постановки).
    /// </para>
    /// <para>
    /// ⚠ Виняток — задачі з масивом злиття (<see cref="IFormulaRecalculationJob"/>,
    /// <see cref="FormulaRecalculationTarget.MergedArrayPath"/>): payload не
    /// відкидається, а його масив дописується в задачу, що чекає; у Quartz задача,
    /// що вже виконується, злиття не приймає — поруч ставиться нова.
    /// </para>
    /// </remarks>
    public Task<string> EnqueueCoalescedAsync<TJob>(
        string targetKey, object? payload, CancellationToken ct, int? createdByUserId = null)
        where TJob : IBackgroundJob;

    /// <summary>Планує задачу за cron-виразом.</summary>
    /// <remarks>
    /// Ключ періодичної задачі — тип плюс payload; cron у нього НЕ входить, тож
    /// повторний виклик із новим cron на тому самому payload ЗАМІНЮЄ розклад, а
    /// не плодить двійника. Невалідний cron (<see cref="IsValidCron"/>) —
    /// <see cref="ArgumentException"/> ще до звернення до планувальника.
    /// </remarks>
    public Task ScheduleAsync<TJob>(string cronExpression, object? payload, CancellationToken ct) where TJob : IBackgroundJob;

    /// <summary>
    /// Знімає періодичну задачу, поставлену <see cref="ScheduleAsync{TJob}"/>
    /// з тим самим типом і payload.
    /// </summary>
    /// <returns><c>false</c> — такої задачі в планувальнику не було.</returns>
    /// <remarks>
    /// ⛔ <see cref="CancelAsync"/> тут не годиться: він приймає <c>jobId</c>,
    /// якого в розкладу зовні немає. Без цього методу вимкнений в інтерфейсі
    /// розклад збирав би далі аж до перезапуску застосунку (ФВ-14.3).
    /// </remarks>
    public Task<bool> UnscheduleAsync<TJob>(object? payload, CancellationToken ct) where TJob : IBackgroundJob;

    /// <summary>Перевіряє cron-вираз, не ставлячи нічого.</summary>
    /// <param name="expression">Вираз.</param>
    /// <param name="error">Чому вираз не приймається; <c>null</c>, коли він валідний.</param>
    /// <remarks>
    /// Формат — cron Quartz: 6 полів «секунди хвилини години день-місяця місяць
    /// день-тижня» плюс необов'язковий 7-й — рік; рівно одне з полів дня має
    /// бути <c>?</c>. Приклад: <c>0 15 2 * * ?</c> — щодня о 02:15:00.
    /// ⚠ П'ятипольний unix-cron (<c>15 2 * * *</c>) НЕ приймається.
    /// </remarks>
    public bool IsValidCron(string expression, out string? error);

    /// <summary>Скасовує задачу.</summary>
    public Task CancelAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// Вручну перезапускає провалену задачу (директива №11, T10 #40).
    /// </summary>
    /// <param name="jobId">Ідентифікатор задачі, яку ставили раніше.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>
    /// <c>false</c> — деталей задачі більше немає в планувальнику (наприклад,
    /// після перезапуску процесу: чергу тримає сховище В ПАМ'ЯТІ, D-66).
    /// </returns>
    /// <remarks>
    /// ⚠ Задача лишається в черзі Quartz дурабельною (<c>StoreDurably</c>) саме
    /// доти, доки не вичерпає ретраї (<see cref="IBackgroundJob"/>) — інакше
    /// відновлювати після провалу не було б чого: без триґера і без durable
    /// Quartz сам прибирає деталі задачі одразу після останнього прогону.
    /// </remarks>
    public Task<bool> RestartAsync(string jobId, CancellationToken ct);

    /// <summary>Стан виконання для UI прогресу.</summary>
    public Task<JobStatus> GetStatusAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// Хто поставив задачу; <c>null</c> — системна задача, або такої задачі
    /// немає.
    /// </summary>
    /// <remarks>
    /// ⛔ Окремий метод, а не поле в <see cref="JobStatus"/> (Q-156):
    /// <c>JobStatus</c> — тіло HTTP-відповіді <c>GET /jobs/{jobId}</c>, і
    /// ідентифікатор автора там нікому не потрібен — лише
    /// <see cref="GetStatusAsync"/>-виклику ВСЕРЕДИНІ <c>GetJobStatusHandler</c>,
    /// щоб порівняти з поточним користувачем ДО того, як тіло взагалі
    /// збирається.
    /// </remarks>
    public Task<int?> GetCreatedByUserIdAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// Останні задачі, найновіші перші — для черги в інтерфейсі.
    /// </summary>
    /// <param name="filter">Звуження переліку; порожній — усі задачі.</param>
    /// <param name="limit">Скільки повернути.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ До цього методу задачу можна було переглянути, лише знаючи її GUID
    /// (<c>GET /jobs/{jobId}</c>): збій перерахунку був видимий десь, але не
    /// БУВ ЗНАЙДЕНИЙ, доки хтось не назве точний ідентифікатор
    /// (директива №09 §6.5, `S-25`; `ФВ-12.4`).
    /// </remarks>
    public Task<IReadOnlyList<JobSummary>> ListRecentAsync(
        JobListFilter filter, int limit, CancellationToken ct);
}

/// <summary>
/// Звуження переліку задач (BE-08).
/// </summary>
/// <param name="State">
/// Стан задачі (<c>Queued</c>, <c>Running</c>, <c>Succeeded</c>, <c>Failed</c>,
/// <c>Cancelled</c>); <c>null</c> — будь-який.
/// </param>
/// <param name="JobCode">Код (тип) задачі; <c>null</c> — будь-який.</param>
/// <param name="CreatedByUserId">
/// Автор задачі; <c>null</c> — задачі всіх авторів.
/// <para>
/// ⛔ Значення береться ВИКЛЮЧНО з <c>ICurrentUser</c> в обробнику
/// (<c>ListJobsHandler</c>) і НІКОЛИ з запиту. Це межа доступу, а не
/// зручність: перелік «моїх» задач не вимагає <c>System.ViewHealth</c>, тож
/// параметр, яким можна назвати ЧУЖИЙ ідентифікатор, був би не фільтром, а
/// витоком — будь-хто читав би чужу чергу, назвавши чуже число.
/// </para>
/// </param>
public sealed record JobListFilter(
    string? State = null, string? JobCode = null, int? CreatedByUserId = null)
{
    /// <summary>Порожнє звуження: усі задачі всіх авторів.</summary>
    public static readonly JobListFilter None = new();
}

/// <summary>Задача в переліку черги — легша за <see cref="JobStatus"/>.</summary>
/// <param name="JobId">Ідентифікатор.</param>
/// <param name="JobCode">Код задачі (тип).</param>
/// <param name="State">Стан.</param>
/// <param name="Percent">Прогрес у відсотках.</param>
/// <param name="UpdatedAt">Момент останнього оновлення в UTC.</param>
/// <param name="StartedAt">
/// Момент постановки в чергу, а після старту — момент СТАРТУ задачі в UTC
/// (<c>JobProgress.Begin</c> перезаписує його). Момент постановки — <paramref name="CreatedAt"/>.
/// </param>
/// <param name="Attempt">Номер спроби від 1; <c>null</c> — ще не стартувала (BE-08).</param>
/// <param name="CorrelationId">Кореляція з логом і запитом-постановником (BE-08).</param>
/// <param name="CreatedByDisplayName">Ім'я автора; <c>null</c> — системна задача (BE-08).</param>
/// <param name="Message">
/// Повідомлення прогресу мовою читача (BE-08). Каталог рядків вантажиться
/// ОДИН раз на весь перелік (<c>JobProgressMessageResolver.ResolveManyAsync</c>),
/// а не на кожен рядок.
/// </param>
/// <param name="CreatedAt">Перша постановка в чергу, UTC; <c>null</c> — розклад (BE-08).</param>
/// <param name="ErrorCode">Код каталогу помилок провалу (BE-08).</param>
/// <param name="DocumentId">Документ задачі; <c>null</c> — не документна (BE-08).</param>
/// <param name="MaxAttempts">
/// Спроб загалом, як у <see cref="JobStatus.MaxAttempts"/>; рядок переліку завжди
/// з журналу, тож від сховища — завжди число, <c>null</c> лише від інших реалізацій.
/// </param>
/// <param name="ResultUrl">Як <see cref="JobStatus.ResultUrl"/> (UX-09).</param>
/// <param name="CreatedByUserId">
/// Id автора; <c>null</c> — системна задача. Видимість та сама, що й
/// <paramref name="CreatedByDisplayName"/> — клієнт вирішує показ «Повторити».
/// </param>
public sealed record JobSummary(
    string JobId,
    string JobCode,
    string State,
    int Percent,
    DateTime UpdatedAt,
    DateTime StartedAt,
    int? Attempt = null,
    string? CorrelationId = null,
    string? CreatedByDisplayName = null,
    string? Message = null,
    DateTime? CreatedAt = null,
    string? ErrorCode = null,
    long? DocumentId = null,
    int? MaxAttempts = null,
    string? ResultUrl = null,
    int? CreatedByUserId = null);

/// <summary>Фонова задача.</summary>
public interface IBackgroundJob
{
    /// <summary>Виконує задачу. Має бути ідемпотентною і відновлюваною.</summary>
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct);
}

/// <summary>Канал прогресу для довгих операцій (усе довше ~5 с — у фон).</summary>
public interface IJobProgress
{
    public Task ReportAsync(int percent, string? message, CancellationToken ct);
}

/// <summary>Стан фонової задачі.</summary>
/// <param name="JobId">Ідентифікатор.</param>
/// <param name="State">Стан.</param>
/// <param name="Percent">Прогрес у відсотках.</param>
/// <param name="Message">Повідомлення прогресу.</param>
/// <param name="Error">Текст провалу.</param>
/// <param name="Attempt">Номер спроби від 1; <c>null</c> — ще не стартувала (BE-08).</param>
/// <param name="CorrelationId">Кореляція з логом і запитом-постановником (BE-08).</param>
/// <param name="MaxAttempts">
/// Скільки спроб задача має загалом: перша + автоматичні ретраї (BE-08);
/// <c>null</c> — стан не з журналу (<c>Unknown</c>/<c>Unavailable</c>).
/// </param>
/// <param name="CreatedAt">Перша постановка в чергу, UTC; <c>null</c> — розклад (BE-08).</param>
/// <param name="ErrorCode">Код каталогу помилок провалу (BE-08).</param>
/// <param name="DocumentId">Документ задачі; <c>null</c> — не документна (BE-08).</param>
/// <param name="ResultUrl">
/// Відносний шлях API до файлу результату (книга експорту) — лише для
/// <c>Succeeded</c> з файлом і читача з <c>Document.Export</c>; інакше <c>null</c> (UX-09).
/// </param>
/// <param name="CreatedByUserId">
/// Id автора; <c>null</c> — системна задача. Заповнює <c>GetJobStatusHandler</c>:
/// тіло бачить лише автор або власник <c>System.ViewHealth</c>.
/// </param>
/// <param name="FanOut">
/// Підсумок дочірніх задач, розкладених цією (P4); <c>null</c> — дочірніх немає.
/// Заповнює <c>GetJobStatusHandler</c>.
/// </param>
/// <param name="EffectiveState">
/// Похідний стан для оператора: <c>FannedOut</c> / <c>Succeeded</c> /
/// <c>SucceededWithErrors</c>; <c>null</c> — як <see cref="State"/>.
/// </param>
public sealed record JobStatus(
    string JobId,
    string State,
    int Percent,
    string? Message,
    string? Error,
    int? Attempt = null,
    string? CorrelationId = null,
    int? MaxAttempts = null,
    DateTime? CreatedAt = null,
    string? ErrorCode = null,
    long? DocumentId = null,
    string? ResultUrl = null,
    int? CreatedByUserId = null,
    FanOutStatus? FanOut = null,
    string? EffectiveState = null);

/// <summary>Ідентифікатор поточної задачі; його несе канал прогресу воркера.</summary>
/// <remarks>Потрібен задачі, що розкладає роботу на дочірні, щоб позначити їх своїм <c>JobId</c>.</remarks>
public interface IJobIdentity
{
    /// <summary>Ідентифікатор задачі, що виконується.</summary>
    public string JobId { get; }
}

/// <summary>
/// Підсумок дочірніх задач розкладу (P4 ФВ-9.8): скільки розкладено і що з ними зараз.
/// </summary>
/// <param name="Total">Скільки дочірніх задач позначено цим батьком.</param>
/// <param name="Queued">У черзі.</param>
/// <param name="Running">Виконуються.</param>
/// <param name="Succeeded">Виконано.</param>
/// <param name="Failed">Провалено або скасовано.</param>
/// <remarks>
/// ⛔ Батьківська задача завершується, коли лише РОЗКЛАДЕНО дочірні, — це не
/// «пораховано». Похідний стан рахується при читанні (схеми немає): батьківський
/// збережений стан лишається <c>Succeeded</c> і не займає слот пулу.
/// </remarks>
public sealed record FanOutStatus(int Total, int Queued, int Running, int Succeeded, int Failed)
{
    /// <summary>Розкладено, дочірні ще не завершились.</summary>
    public const string StateFannedOut = "FannedOut";

    /// <summary>Усі дочірні виконано.</summary>
    public const string StateSucceeded = "Succeeded";

    /// <summary>Усі дочірні завершились, але частина з помилками.</summary>
    public const string StateSucceededWithErrors = "SucceededWithErrors";

    /// <summary>Похідний стан батька за його збереженим станом.</summary>
    /// <param name="parentState">Збережений стан батька.</param>
    public string EffectiveStateOf(string parentState)
    {
        if (!string.Equals(parentState, "Succeeded", StringComparison.Ordinal))
        {
            return parentState;
        }

        if (Queued + Running > 0 || Succeeded + Failed < Total)
        {
            return StateFannedOut;
        }

        return Failed > 0 ? StateSucceededWithErrors : StateSucceeded;
    }
}

/// <summary>
/// Маркер задачі перерахунку.
/// </summary>
/// <remarks>
/// ⚠ Потрібен тому, що <see cref="IBackgroundJobScheduler.EnqueueAsync{TJob}"/>
/// обмежений <c>where TJob : IBackgroundJob</c>, а конкретні задачі живуть в
/// <c>Ecr.Infrastructure</c>, якого <c>Ecr.Application</c> не бачить і бачити
/// не має. Маркер дає use-case назвати задачу, не знаючи її реалізації.
/// </remarks>
public interface IRecalculationJob : IBackgroundJob;

/// <summary>
/// Маркер задачі каскадного перерахунку ФОРМУЛ ШАБЛОНУ.
/// </summary>
/// <remarks>
/// ⛔ Окремий маркер, а не той самий, що для методологій. Результати формул
/// шаблону лежать у <c>doc.CellValue</c> з <c>IsCalculated = 1</c>, результати
/// методологій — у <c>calc.CalculationResult</c> (<c>D-69</c>). До появи цього
/// маркера правка комірки ставила в чергу задачу МЕТОДОЛОГІЙ із тілом, якого
/// та не розуміє: розбір давав нулі, і задача не робила нічого (<c>A7-63</c>).
/// <para>
/// ⛔ O1 (I2 ФВ-9.8): ставиться через <see cref="IBackgroundJobScheduler.EnqueueCoalescedAsync{TJob}"/>
/// на ціль <see cref="FormulaRecalculationTarget.Of"/>, і злиття цієї задачі —
/// ОСОБЛИВЕ: масив <c>cells</c> payload нової постановки ДОПИСУЄТЬСЯ в задачу, що
/// чекає (<see cref="FormulaRecalculationTarget.MergedArrayPath"/>), а не
/// відкидається. Змінені комірки — насіння каскаду; загублена комірка означала б
/// непораховану формулу.
/// </para>
/// </remarks>
public interface IFormulaRecalculationJob : IBackgroundJob;

/// <summary>Ціль злиття інкрементних задач формул (O1, I2 ФВ-9.8).</summary>
public static class FormulaRecalculationTarget
{
    /// <summary>Масив payload, який злиття об'єднує, а не відкидає.</summary>
    public const string MergedArrayPath = "$.cells";

    /// <summary>Ціль «документ × період × автор».</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="createdByUserId">Автор постановки; <c>null</c> — системна (імпорт).</param>
    /// <returns>Ключ цілі.</returns>
    /// <remarks>
    /// ⚠ Префікс — той самий, що в <c>RecalculateDocumentHandler.TargetOf</c>
    /// (<c>doc{id}-p{period}</c>), із суфіксом <c>-formula</c>: тип задачі однаково
    /// входить у <c>TargetKey</c>, суфікс лише робить ціль читабельною в журналі.
    /// <para>
    /// ⛔ Автор — у цілі, і це межа доступу, а не косметика. PATCH віддає клієнтові
    /// <c>JobId</c> задачі, яку той опитує; стан ЧУЖОЇ задачі
    /// <c>GetJobStatusHandler</c> без <c>System.ViewHealth</c> не показує (Q-156).
    /// Злиття правок двох людей в одну задачу дало б другому <c>403</c> на його ж
    /// збереженні. Двоє редакторів одного документо-періоду — дві задачі, і лок
    /// документа серіалізує їх так само, як і раніше.
    /// </para>
    /// </remarks>
    public static string Of(long documentId, int periodKey, int? createdByUserId)
        => createdByUserId is { } user
            ? string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"doc{documentId}-p{periodKey}-formula-u{user}")
            : string.Create(
                System.Globalization.CultureInfo.InvariantCulture, $"doc{documentId}-p{periodKey}-formula");
}

/// <summary>Маркер задачі експорту документа у <c>.xlsx</c>.</summary>
/// <remarks>
/// Той самий прийом, що й <see cref="IRecalculationJob"/>: use-case називає
/// задачу, не знаючи, що її реалізація живе в <c>Ecr.Infrastructure</c> і
/// спирається на адаптер Excel.
/// </remarks>
public interface IExcelExportJob : IBackgroundJob;

/// <summary>
/// Маркер задачі застосування ВЕЛИКОГО імпорту з <c>.xlsx</c> (директива №11,
/// T10 #45).
/// </summary>
/// <remarks>
/// ⚠ Окремий від <see cref="IExcelExportJob"/>, хоч обидва — Excel: застосування
/// імпорту читає РАНІШЕ побудований diff (<c>previewToken</c>) і пише через
/// звичайний шлях <c>PatchCellsHandler</c>, тоді як експорт лише читає й
/// будує книгу. Спільний маркер змусив би задачу розбирати payload двох
/// різних форм.
/// </remarks>
public interface IExcelImportJob : IBackgroundJob;

/// <summary>
/// Маркер задачі перенесення зібраних точок у комірки (<c>D-118</c>).
/// </summary>
/// <remarks>
/// ⚠ Потрібен із тієї самої причини, що й решта маркерів: планувальник
/// приймає ТИП, а прикладний шар не бачить реалізацій з інфраструктури.
/// </remarks>
public interface IMaterializeCollectedDataJob : IBackgroundJob;

/// <summary>Маркер задачі побудови зрізу звітності.</summary>
public interface IReportSnapshotJob : IBackgroundJob;

/// <summary>Маркер задачі збору із зовнішнього джерела.</summary>
public interface ICollectionJob : IBackgroundJob;

/// <summary>
/// Маркер нічної перевірки узгодженості — щоб її можна було запустити НА
/// ВИМОГУ (<c>BE-30</c>, «Run check now»).
/// </summary>
/// <remarks>
/// ⚠ Потрібен із тієї самої причини, що й решта маркерів: реалізація живе в
/// <c>Ecr.Infrastructure</c>, якого прикладний шар не бачить.
/// <para>
/// ⚠ Розклад (<c>RecurringScheduleService</c>) ставить ТУ САМУ задачу за
/// конкретним типом, а не за цим маркером, і <c>JobCode</c> у
/// <c>itg.JobProgress</c> — це повне ім'я типу, яким задачу поставили. Тобто
/// назв у черзі ДВІ, і той, хто шукає перевірку серед незавершених задач, має
/// впізнавати обидві (<c>RunConsistencyCheckHandler.IsConsistencyCheckCode</c>).
/// </para>
/// </remarks>
public interface IConsistencyCheckJob : IBackgroundJob;

/// <summary>
/// Маркер пошуку осиротілих рядків — щоб його можна було поставити РАЗОВО
/// після ручного відкриття періоду (<c>ReopenPeriodHandler</c>).
/// </summary>
/// <remarks>
/// ⚠ Реалізація (<c>OrphanScanJob</c>) живе в <c>Ecr.Infrastructure</c>, якого
/// прикладний шар не бачить. Нічний розклад і системний Reopen у
/// <c>PeriodStateJob</c> ставлять ТУ САМУ задачу за конкретним типом.
/// </remarks>
public interface IOrphanScanJob : IBackgroundJob;
