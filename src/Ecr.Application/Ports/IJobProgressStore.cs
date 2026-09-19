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
    public Task QueueAsync(
        string jobId, string jobCode, DateTime utcNow, CancellationToken ct, int? createdByUserId = null);

    public Task StartAsync(string jobId, string jobCode, DateTime utcNow, CancellationToken ct);

    /// <summary>Оновлює прогрес.</summary>
    public Task ReportAsync(string jobId, int percent, string? message, DateTime utcNow, CancellationToken ct);

    /// <summary>Фіксує завершення.</summary>
    public Task FinishAsync(
        string jobId, string state, string? errorMessage, DateTime utcNow, CancellationToken ct);

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
    /// </remarks>
    public Task HeartbeatAsync(string jobId, DateTime utcNow, CancellationToken ct);

    /// <summary>
    /// Позначає ПОКИНУТІ <c>Running</c>/<c>Queued</c> задачі <c>Failed</c>.
    /// </summary>
    /// <param name="reason">Причина, що йде в <c>Error</c>.</param>
    /// <param name="utcNow">Момент позначення в UTC.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Скільки записів позначено.</returns>
    /// <remarks>
    /// ⛔ Викликається один раз при СТАРТІ застосунку. Задача, яку процес
    /// виконував у момент падіння, лишається `Running` у базі назавжди — сам
    /// процес, який мав позначити її `Failed`, уже не існує. Без цього методу
    /// такий запис показує оператору задачу, що «виконується» місяцями.
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
