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
/// Момент постановки в чергу, а після старту — момент СТАРТУ задачі в UTC.
/// <para>
/// ⚠ Поле називається <c>StartedAt</c>, а не <c>CreatedAt</c>, бо саме це
/// зберігає стовпець: <c>JobProgress.Begin</c> перезаписує його в момент
/// запуску (<c>IntegrationLogs.cs</c>). Назва «створено» була б неправдою
/// для кожної задачі, що вже почала працювати, а окремого стовпця з
/// моментом постановки в <c>itg.JobProgress</c> немає.
/// </para>
/// </param>
/// <remarks>
/// ⚠ Поля <c>Message</c> тут НЕМАЄ, хоч воно й лежить у тому самому рядку
/// <c>itg.JobProgress</c>. Причина не в даних, а в резолві: повідомлення —
/// структурований конверт, який локалізується мовою ЧИТАЧА (<c>Q-326</c>), а
/// <c>JobProgressMessageResolver</c> приймає рядок по одному й на кожен
/// виклик відкриває з'єднання по ревізію каталогу
/// (<c>UiStringCatalogStore.LoadAsync</c>). Півсотні рядків переліку, який
/// клієнт опитує кожні три секунди, коштували б півсотні з'єднань на запит.
/// Щоб віддати повідомлення в переліку, резолверу потрібна форма, яка приймає
/// ВЖЕ завантажений каталог, — окремий PR.
/// </remarks>
public sealed record JobSummary(
    string JobId,
    string JobCode,
    string State,
    int Percent,
    DateTime UpdatedAt,
    DateTime StartedAt);

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
