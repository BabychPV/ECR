// src/Ecr.Application/Ports/IJobProgressStore.cs

namespace Ecr.Application.Ports;

/// <summary>
/// Сховище прогресу фонових задач (<c>itg.JobProgress</c>).
/// </summary>
/// <remarks>
/// ⚠ Прогрес живе в БАЗІ, а не в пам'яті планувальника. Інстансів застосунку
/// кілька, і клієнт, що опитує стан задачі, потрапляє не обов'язково на той,
/// який її виконує: стан у пам'яті відповів би «немає такої» — і екран
/// прогресу показав би помилку на цілком успішній задачі.
/// </remarks>
public interface IJobProgressStore
{
    /// <summary>
    /// За скільки мовчання задача вважається покинутою.
    /// </summary>
    /// <remarks>
    /// ⚠ Судження, не факт із документа. П'ять хвилин — це десять пропущених
    /// бить поспіль (<see cref="HeartbeatInterval"/> = 30 с): разова затримка
    /// під навантаженням або пауза GC покриті з великим запасом, а оператор
    /// усе одно дізнається про справді покинуту задачу за хвилини, а не за
    /// місяці, як було до появи предиката взагалі.
    ///
    /// ⛔ Межа мусить бути ЗНАЧНО більшою за інтервал биття. Зблизити їх —
    /// повернути той самий дефект у м'якшій формі: живі задачі валилися б не
    /// при кожному перезапуску, а час від часу, і причину шукали б роками.
    /// </remarks>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>Як часто процес-власник підтверджує, що задача жива.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Межа стовпця <c>itg.JobProgress.Error</c> — <c>nvarchar(2000)</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ На відміну від <c>Message</c>, текст лягає сюди БЕЗ кодування, тож
    /// межу видно напряму. Але джерело те саме — <c>ex.Message</c> довільної
    /// довжини, — і пастка та сама: аварія, чиє повідомлення перелічує сотню
    /// сутностей, зробила б із запису провалу другий виняток.
    /// </remarks>
    public const int MaxErrorLength = 2000;

    /// <summary>Реєструє початок задачі.</summary>
    /// <summary>
    /// Фіксує ПОСТАНОВКУ задачі в чергу.
    /// </summary>
    /// <param name="jobId">Ідентифікатор задачі.</param>
    /// <param name="jobCode">Код задачі.</param>
    /// <param name="utcNow">Момент постановки в UTC.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Окремий крок від <see cref="StartAsync"/>, і не з педантизму. Між
    /// відповіддю <c>202</c> з <c>jobId</c> і стартом задачі минає час; без
    /// цього запису клієнт, який одразу опитує стан, отримує <c>404</c> на
    /// задачу, яку щойно прийняли, — і вважає, що вона загубилася.
    /// </remarks>
    /// <param name="createdByUserId">Хто поставив задачу; <c>null</c> — системна (Q-156).</param>
    /// <param name="correlationId">Кореляція запиту-постановника (BE-08).</param>
    /// <param name="documentId">Документ задачі; <c>null</c> — не документна (BE-08).</param>
    public Task QueueAsync(
        string jobId, string jobCode, DateTime utcNow, CancellationToken ct, int? createdByUserId = null,
        string? correlationId = null, long? documentId = null);

    /// <summary>Реєструє старт прогону.</summary>
    /// <param name="jobId">Ідентифікатор задачі.</param>
    /// <param name="jobCode">Код задачі.</param>
    /// <param name="utcNow">Момент старту в UTC.</param>
    /// <param name="ct">Скасування.</param>
    /// <param name="attempt">Номер спроби від 1 (BE-08).</param>
    /// <param name="correlationId">Кореляція прогону (BE-08); <c>null</c> — лишити наявну.</param>
    public Task StartAsync(
        string jobId, string jobCode, DateTime utcNow, CancellationToken ct, int attempt = 1,
        string? correlationId = null);

    /// <summary>Оновлює прогрес.</summary>
    public Task ReportAsync(string jobId, int percent, string? message, DateTime utcNow, CancellationToken ct);

    /// <summary>Фіксує завершення; <paramref name="errorCode"/> — код каталогу при провалі (BE-08).</summary>
    public Task FinishAsync(
        string jobId, string state, string? errorMessage, DateTime utcNow, CancellationToken ct,
        string? errorCode = null);

    /// <summary>
    /// Повертає раніше провалену задачу в стан «у черзі» (D-134, №11 T10 #40).
    /// </summary>
    /// <param name="jobId">Ідентифікатор задачі.</param>
    /// <param name="utcNow">Момент ручного перезапуску в UTC.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns><c>false</c> — запису прогресу немає (задачі ніколи не існувало).</returns>
    /// <remarks>
    /// ⚠ Без <c>jobCode</c>: запис уже існує (це саме РЕ-старт), і повторний
    /// <see cref="QueueAsync"/> тут означав би тягнути код задачі окремим
    /// запитом заради значення, яке вже лежить у тому самому рядку.
    /// </remarks>
    public Task<bool> RestartAsync(string jobId, DateTime utcNow, CancellationToken ct);

    /// <summary>Стан задачі; <c>null</c> — такої немає.</summary>
    public Task<JobStatus?> FindAsync(string jobId, CancellationToken ct);

    /// <summary>Хто поставив задачу; <c>null</c> — системна, або такої немає (Q-156).</summary>
    public Task<int?> GetCreatedByUserIdAsync(string jobId, CancellationToken ct);

    /// <summary>Останні задачі, найновіші перші.</summary>
    /// <param name="filter">
    /// Звуження переліку (BE-08). <see cref="JobListFilter.CreatedByUserId"/>
    /// приходить лише з <c>ICurrentUser</c> — див. зауваження в самому типі.
    /// </param>
    /// <param name="limit">Скільки повернути.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Фільтр застосовується в ЗАПИТІ, а не після нього. Відбір у пам'яті
    /// означав би «прочитати 50 останніх задач системи й лишити свої»: власник
    /// однієї задачі бачив би порожній перелік рівно тоді, коли система
    /// зайнята, — тобто тоді, коли він і дивиться.
    /// </remarks>
    public Task<IReadOnlyList<JobSummary>> ListRecentAsync(
        JobListFilter filter, int limit, CancellationToken ct);

    /// <summary>
    /// Підтверджує, що задача досі виконується цим процесом.
    /// </summary>
    /// <param name="jobId">Ідентифікатор задачі.</param>
    /// <param name="utcNow">Момент биття в UTC.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Б'є процес-ВЛАСНИК (<c>QuartzJobAdapter</c>) поки задача виконується.
    /// Без цього довгий імпорт, який годину не повідомляє відсотків, виглядав
    /// би для <see cref="FailStaleAsync"/> покинутим — і виправлення дефекту
    /// зачистки просто замінило б один хибний провал іншим.
    ///
    /// ⚠ Биття НЕ є прогресом: <c>Percent</c>, <c>Message</c> і
    /// <c>UpdatedAt</c> лишаються недоторканими.
    ///
    /// ⚠ Повертає <c>false</c>, коли рядок уже НЕ активний (не
    /// <c>Running</c>/<c>Queued</c>): його закрили ззовні — скасуванням з
    /// іншого інстанса (<see cref="CancelActiveAsync"/>) чи прибиранням. Власник
    /// за цією відповіддю перевіряє, чи не попросили його зупинитися.
    /// </remarks>
    public Task<bool> HeartbeatAsync(string jobId, DateTime utcNow, CancellationToken ct);

    /// <summary>
    /// Биття за задачі, які цей процес ТРИМАЄ в черзі, але ще не виконує.
    /// </summary>
    /// <param name="jobIds">Ідентифікатори задач із локального планувальника.</param>
    /// <param name="utcNow">Момент биття в UTC.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Скільки активних рядків підтверджено.</returns>
    /// <remarks>
    /// ⛔ Без цього періодичне прибирання (<see cref="FailStaleAsync"/>) валило б
    /// ЖИВІ задачі, що просто довго стоять у черзі (пул Quartz зайнятий
    /// збором) або чекають ретраю: у черзі биття не б'є ніхто, крім постановки.
    /// Черга in-memory, тож рядок, який не підтверджує жоден процес, справді
    /// покинутий — його черга згоріла разом із процесом.
    /// </remarks>
    public Task<int> KeepAliveAsync(IReadOnlyCollection<string> jobIds, DateTime utcNow, CancellationToken ct);

    /// <summary>
    /// Позначає задачу <c>Cancelled</c>, якщо вона ще активна (U3).
    /// </summary>
    /// <param name="jobId">Ідентифікатор задачі.</param>
    /// <param name="utcNow">Момент скасування в UTC.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns><c>true</c> — рядок був <c>Running</c>/<c>Queued</c> і став <c>Cancelled</c>.</returns>
    /// <remarks>
    /// ⚠ Умовний запис: завершена задача (<c>Succeeded</c>/<c>Failed</c>) своїм
    /// станом лишається — скасування запізнилось, і правда за результатом.
    /// Цей самий запис — сигнал іншому інстансу: його биття
    /// (<see cref="HeartbeatAsync"/>) бачить неактивний рядок і скасовує
    /// задачу у себе, а постановка в черзі не стартує взагалі.
    /// </remarks>
    public Task<bool> CancelActiveAsync(string jobId, DateTime utcNow, CancellationToken ct);

    /// <summary>
    /// Скільки активних задач без биття довше за <see cref="StaleAfter"/> (U16).
    /// </summary>
    /// <param name="utcNow">Поточний момент у UTC.</param>
    /// <param name="ct">Скасування.</param>
    public Task<StaleJobsSummary> SummarizeStaleAsync(DateTime utcNow, CancellationToken ct);

    /// <summary>
    /// Видаляє ЗАВЕРШЕНІ записи, старші за <paramref name="olderThan"/> (аудит P2).
    /// </summary>
    /// <param name="olderThan">Межа: завершені до цього моменту видаляються.</param>
    /// <param name="batch">Скільки рядків за один виклик.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Скільки видалено.</returns>
    public Task<int> PurgeFinishedAsync(DateTime olderThan, int batch, CancellationToken ct);

    /// <summary>
    /// Скільки тримати завершені записи прогресу (аудит P2).
    /// </summary>
    /// <remarks>
    /// ⚠ Судження, не вимога: 30 діб покривають звітний місяць і розбір
    /// інциденту за ним. Стрічку <c>/jobs</c> опитують кожні 3–30 с, а кожна
    /// правка комірки ставить задачу перерахунку — без межі таблиця росте на
    /// кількість правок за день назавжди.
    /// </remarks>
    public static readonly TimeSpan RetainFinishedFor = TimeSpan.FromDays(30);

    /// <summary>
    /// Позначає ПОКИНУТІ <c>Running</c>/<c>Queued</c> задачі <c>Failed</c>.
    /// </summary>
    /// <param name="reason">Причина, що йде в <c>Error</c>.</param>
    /// <param name="utcNow">Момент позначення в UTC.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Скільки записів позначено.</returns>
    /// <remarks>
    /// ⛔ Викликається при СТАРТІ застосунку і ПЕРІОДИЧНО (U4, прибирання в
    /// <c>RecurringScheduleService</c>). Задача, яку процес
    /// виконував у момент падіння, лишається `Running` у базі назавжди — сам
    /// процес, який мав позначити її `Failed`, уже не існує. Без цього методу
    /// такий запис показує оператору задачу, що «виконується» місяцями.
    /// Лише на старті — мало: перезапуск коротший за <see cref="StaleAfter"/>
    /// лишав покинуті рядки живими до НАСТУПНОГО довгого перезапуску.
    ///
    /// ⛔ Попередня версія не мала ЖОДНОГО предиката застарілості: вона
    /// валила кожен рядок `Running`/`Queued`, який бачила. Інстансів у
    /// розгортанні кілька (ціль — 100 одночасних користувачів), тож
    /// перезапуск інстанса B убивав перерахунки, експорти й імпорти, які в
    /// цю мить виконував інстанс A, і користувач бачив провал задачі, що
    /// насправді доробила успішно. Тепер покинутою вважається лише задача,
    /// чиє биття (<see cref="HeartbeatAsync"/>) застигло довше за
    /// <see cref="StaleAfter"/>.
    /// </remarks>
    public Task<int> FailStaleAsync(string reason, DateTime utcNow, CancellationToken ct);
}

/// <summary>Активні задачі без биття довше за <see cref="IJobProgressStore.StaleAfter"/>.</summary>
/// <param name="Count">Скільки їх.</param>
/// <param name="OldestHeartbeatAt">Найстаріше биття; <c>null</c> — таких немає або биття не було ніколи.</param>
public sealed record StaleJobsSummary(int Count, DateTime? OldestHeartbeatAt);
