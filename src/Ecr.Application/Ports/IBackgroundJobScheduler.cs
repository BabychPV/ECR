// src/Ecr.Application/Ports/IBackgroundJobScheduler.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Планувальник фонових задач. Порт існує, щоб заміна реалізації
/// (Quartz ↔ Hangfire) коштувала день, а не рефакторинг (D-09): допустимість
/// LGPL — відкрите питання до ІБ.
/// </summary>
public interface IBackgroundJobScheduler
{
    /// <summary>Ставить задачу в чергу негайно.</summary>
    public Task<string> EnqueueAsync<TJob>(object? payload, CancellationToken ct) where TJob : IBackgroundJob;

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
    public Task<string> EnqueueExclusiveAsync<TJob>(string targetKey, object? payload, CancellationToken ct)
        where TJob : IBackgroundJob;

    /// <summary>Планує задачу за cron-виразом.</summary>
    public Task ScheduleAsync<TJob>(string cronExpression, object? payload, CancellationToken ct) where TJob : IBackgroundJob;

    /// <summary>Скасовує задачу.</summary>
    public Task CancelAsync(string jobId, CancellationToken ct);

    /// <summary>Стан виконання для UI прогресу.</summary>
    public Task<JobStatus> GetStatusAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// Останні задачі, найновіші перші — для черги в інтерфейсі.
    /// </summary>
    /// <param name="limit">Скільки повернути.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ До цього методу задачу можна було переглянути, лише знаючи її GUID
    /// (<c>GET /jobs/{jobId}</c>): збій перерахунку був видимий десь, але не
    /// БУВ ЗНАЙДЕНИЙ, доки хтось не назве точний ідентифікатор
    /// (директива №09 §6.5, `S-25`; `ФВ-12.4`).
    /// </remarks>
    public Task<IReadOnlyList<JobSummary>> ListRecentAsync(int limit, CancellationToken ct);
}

/// <summary>Задача в переліку черги — легша за <see cref="JobStatus"/>.</summary>
/// <param name="JobId">Ідентифікатор.</param>
/// <param name="JobCode">Код задачі (тип).</param>
/// <param name="State">Стан.</param>
/// <param name="Percent">Прогрес у відсотках.</param>
/// <param name="UpdatedAt">Момент останнього оновлення в UTC.</param>
public sealed record JobSummary(string JobId, string JobCode, string State, int Percent, DateTime UpdatedAt);

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
public sealed record JobStatus(string JobId, string State, int Percent, string? Message, string? Error);

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
/// </remarks>
public interface IFormulaRecalculationJob : IBackgroundJob;

/// <summary>Маркер задачі експорту документа у <c>.xlsx</c>.</summary>
/// <remarks>
/// Той самий прийом, що й <see cref="IRecalculationJob"/>: use-case називає
/// задачу, не знаючи, що її реалізація живе в <c>Ecr.Infrastructure</c> і
/// спирається на адаптер Excel.
/// </remarks>
public interface IExcelExportJob : IBackgroundJob;

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
