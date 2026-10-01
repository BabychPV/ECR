using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Entities.Notifications;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Переводить періоди між станами і оновлює <c>Project.CurrentPeriod</c>.
/// </summary>
/// <remarks>
/// Стан періоду — **збережене значення**, а не функція від <c>now()</c> у
/// запиті (ФВ-1.12). Інакше кожна перевірка доступу рахувала б offsets, а межа
/// «останнього дня» залежала б від того, о котрій виконано запит.
/// <para>
/// ⚠ Журнал у конструкторі необов'язковий лише для прямого конструювання в
/// тестах; у DI <c>ILogger&lt;T&gt;</c> зареєстрований завжди, тож у
/// застосунку пропуски й збої йдуть у журнал.
/// </para>
/// </remarks>
public sealed partial class PeriodStateJob(
    EcrDbContext db,
    PeriodStateCalculator calculator,
    IUnitOfWork uow,
    IClock clock,
    IMaterializationScheduler materialization,
    ILogger<PeriodStateJob>? logger = null,
    IAuditWriter? audit = null,
    IBackgroundJobScheduler? jobs = null,
    Notifications.NotificationDispatcher? notifications = null,
    IUiStringCatalog? catalog = null) : IBackgroundJob
{
    /// <summary>Ціль витісняючої постановки пошуку осиротілих після системного Reopen.</summary>
    /// <remarks>
    /// ⚠ Витісняюча, а не звичайна: прогін бере весь набір за курсором, тож
    /// друга постановка поспіль нічого не додала б — лише подвоїла б роботу.
    /// </remarks>
    public const string OrphanScanAfterReopenTarget = "period-reopen";

    /// <summary>Скільки періодів системно відкрито за цей прогін.</summary>
    private int _yearReopens;

    /// <summary>
    /// Автор системного Reopen «вікно року» в <c>aud.StructureChange</c>: не
    /// людина. Та сама умовність, що й у перерахунку (<c>RecalculationService.SystemUserId</c>).
    /// </summary>
    public const int SystemUserId = 0;

    /// <summary><c>CorrelationId</c> записів аудиту задачі — за ним їх видно в журналі.</summary>
    public const string AuditCorrelationId = "period-state:year-grace";

    // ⚠ Аудит необов'язковий у конструкторі лише для наявних прямих
    // конструювань у тестах; без нього — той самий `AuditWriter` на тому самому
    // контексті, тобто в тій самій транзакції. Мовчазного «без аудиту» немає.
    private readonly IAuditWriter _audit = audit ?? new AuditWriter(db);

    /// <summary>Код задачі в журналі обслуговування (<c>itg.MaintenanceRun</c>).</summary>
    public static string Code => "period-state";

    /// <summary>
    /// Скільки однакових знахідок не повторювати в <c>itg.MaintenanceRun</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Задача щогодинна, а пропущений зворотний перехід тримається, доки дати
    /// не наздоженуть збережений стан — днями. Рядок щогодини дав би 24
    /// однакові пункти на добу у зведенні <see cref="NotificationJob"/> (стеля
    /// — <see cref="NotificationJob.MaxDigestItems"/>) і витіснив би решту.
    /// Раз на добу — зведення бачить знахідку щодня, поки вона є. Журнал
    /// (<c>Warning</c>) пишеться щоразу.
    /// </remarks>
    public static readonly TimeSpan RepeatFindingsAfter = TimeSpan.FromHours(24);

    /// <summary>Скільки пунктів кожного виду лишається в <c>DetailsJson</c>.</summary>
    private const int MaxDetailItems = 50;

    /// <summary>Скільки символів тексту помилки лишається в <c>DetailsJson</c> (без стека, ФВ-6.11).</summary>
    private const int MaxErrorLength = 500;

    /// <summary>
    /// Кирилиця лишається читабельною: <c>DetailsJson</c> іде текстом у лист
    /// зведення (той самий вибір, що в <c>MaintenanceRunFailure</c>).
    /// </summary>
    private static readonly JsonSerializerOptions DetailsOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>Перехід, який задача має застосувати.</summary>
    /// <param name="Period">Період.</param>
    /// <param name="Target">Цільовий стан.</param>
    public readonly record struct Transition(Period Period, PeriodState Target);

    /// <summary>
    /// Обчислює переходи, нічого не змінюючи.
    /// </summary>
    /// <param name="periods">Періоди одного проєкту з уже обчисленими межами.</param>
    /// <param name="utcNow">Поточний момент.</param>
    /// <param name="siteTimeZone">Пояс майданчика (<c>D-68</c>).</param>
    /// <param name="calculator">Калькулятор станів.</param>
    /// <remarks>
    /// Винесено окремо від <see cref="ExecuteAsync"/> навмисно: рішення про
    /// стан періоду має бути перевіреним без бази, бо саме воно вирішує, чи
    /// можна редагувати документ.
    ///
    /// ⛔ Саме рішення живе тепер у домені (<see cref="PeriodStateCalculator.Plan"/>),
    /// а не тут: доки воно лежало в <c>Ecr.Infrastructure</c>, прикладний шар
    /// не мав до нього шляху, і активація проєкту не могла відкрити період
    /// сама — вона чекала наступного годинного прогону (директива №09 `W8`,
    /// `S-11`). Тут лишився перехідник до вже наявних викликів.
    /// </remarks>
    public static IReadOnlyList<Transition> Plan(
        IReadOnlyList<Period> periods,
        DateTime utcNow,
        TimeZoneInfo siteTimeZone,
        PeriodStateCalculator calculator)
    {
        ArgumentNullException.ThrowIfNull(calculator);

        return [.. calculator
            .Plan(periods, utcNow, siteTimeZone)
            .Select(t => new Transition(t.Period, t.Target))];
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var projects = await db.Projects
            .Where(p => p.Status == ProjectStatus.Active)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var utcNow = clock.UtcNow;
        _yearReopens = 0;
        var skipped = new List<SkippedEntry>();
        var failures = new List<FailedEntry>();

        for (var i = 0; i < projects.Count; i++)
        {
            var project = projects[i];

            // ⛔ Збій одного проєкту не зупиняє решту. Доти виняток на першому ж
            // «битому» проєкті (невідомий пояс, збій коміту, недозволений
            // перехід) обривав прогін, і періоди ВСІХ наступних проєктів
            // лишались у старому стані — щогодини, доки хтось не виправить чужу
            // проблему. Збій не ковтається: Error у журнал, рядок у
            // `itg.MaintenanceRun` (зведення адміністраторам), і наприкінці
            // прогону виняток іде нагору (`QuartzJobAdapter` → `Failed` у `/jobs`).
            try
            {
                var projectSkips = await ProcessProjectAsync(project, utcNow, ct).ConfigureAwait(false);

                foreach (var skip in projectSkips)
                {
                    LogBackwardTransitionSkipped(
                        _logger, project.Code, project.Id, skip.Period.PeriodKeyValue,
                        skip.Current, skip.Computed, SkippedPeriodTransition.Reason);

                    skipped.Add(new SkippedEntry(
                        project.Code,
                        skip.Period.PeriodKeyValue,
                        skip.Current.ToString(),
                        skip.Computed.ToString(),
                        SkippedPeriodTransition.Reason));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Discard(project);
                LogProjectFailed(_logger, project.Code, project.Id, ex);
                failures.Add(new FailedEntry(project.Code, ex));
            }

            await progress.ReportAsync(
                (i + 1) * 100 / Math.Max(1, projects.Count), project.Code, ct).ConfigureAwait(false);
        }

        await RecordFindingsAsync(skipped, failures, utcNow, ct).ConfigureAwait(false);

        await EnqueueOrphanScanAfterReopenAsync(ct).ConfigureAwait(false);

        switch (failures.Count)
        {
            case 0:
                return;

            // ⚠ Один збій — його ж виняток, без обгортки: тип вирішує, чи варто
            // `QuartzJobAdapter` повторювати прогін (`IsWorthRetrying`), і
            // обгортка зробила б «вердикт» (`DomainException`) транзієнтним.
            case 1:
                ExceptionDispatchInfo.Capture(failures[0].Error).Throw();
                return;

            default:
                throw new AggregateException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"PeriodStateJob: не оброблено проєктів — {failures.Count}: {string.Join(", ", failures.Select(f => f.ProjectCode))}."),
                    failures.Select(f => f.Error));
        }
    }

    /// <summary>
    /// Ставить разовий пошук осиротілих рядків, якщо прогін системно відкрив
    /// хоч один період (D-204, «вікно року»).
    /// </summary>
    /// <remarks>
    /// ⛔ Нічний <c>OrphanScanJob</c> обходить лише <c>Open</c>/<c>Grace</c>: поки
    /// рік був закритим, ознака <c>IsOrphaned</c> у ньому не оновлювалася, і
    /// щойно відкритий рік показує застарілі позначки — аж до ночі, а за
    /// бюджетом курсора й довше. Постановка скорочує це до хвилин.
    /// <para>
    /// ⚠ Сканер глобальний (курсор на весь набір), окремого проходу «лише цей
    /// період» порт <see cref="IOrphanScanner"/> не має — тож це прогін
    /// набору, що вже включає відкриті періоди.
    /// </para>
    /// <para>
    /// ⚠ Збій постановки прогону НЕ валить: стани періодів уже закомічено, а
    /// нічний прохід однаково дійде до цих рядків. Але й не мовчить — журнал.
    /// </para>
    /// </remarks>
    private async Task EnqueueOrphanScanAfterReopenAsync(CancellationToken ct)
    {
        if (_yearReopens == 0 || jobs is null)
        {
            return;
        }

        try
        {
            await jobs
                // ⚠ Маркер, а не клас: так само ставить ручний Reopen
                // (`ReopenPeriodHandler`), і лише спільний префікс ключа дає
                // витіснення між ними.
                .EnqueueExclusiveAsync<IOrphanScanJob>(OrphanScanAfterReopenTarget, payload: null, ct)
                .ConfigureAwait(false);

            LogOrphanScanEnqueued(_logger, _yearReopens);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogOrphanScanNotEnqueued(_logger, _yearReopens, ex);
        }
    }

    /// <summary>Переводить періоди одного проєкту; повертає пропущені зворотні переходи.</summary>
    /// <param name="project">Активний проєкт (відстежується контекстом).</param>
    /// <param name="utcNow">Момент прогону.</param>
    /// <param name="ct">Токен скасування.</param>
    private async Task<IReadOnlyList<SkippedPeriodTransition>> ProcessProjectAsync(
        Project project, DateTime utcNow, CancellationToken ct)
    {
        // Пояс майданчика, а не пояс сервера: період, що закривається
        // «31 числа о 23:59», має закритися о 23:59 там, де сидять люди
        // (D-68).
        var zone = ResolveZone(project.TimeZoneId);

        // ⛔ Читання під UPDLOCK і запис — В ОДНІЙ транзакції, з комітом
        // на КОЖНИЙ проєкт (аудит 2026-09-16, §6.1). До цього фіксу
        // транзакції не було зовсім: `UPDLOCK` поза явною транзакцією
        // звільняється щойно завершується сам `SELECT` — задовго до
        // `AdvanceTo` і задовго до `SaveChangesAsync`, який до цього
        // викликався один раз ПІСЛЯ циклу по всіх активних проєктах.
        // Тобто коментар обіцяв взаємовиключення, а блокування не
        // тримало нічого.
        //
        // Сценарій: задача читає період о T1 і вирішує закрити його. До
        // власного коміту користувач відкриває період через Reopen —
        // бачить ще Open, дозволяє, комітить. Задача потім комітить уже
        // обчислений перехід у Closed, тихо перекриваючи Reopen: конфлікту
        // немає, бо в `Period` немає RowVersion.
        //
        // ⚠ Коміт по одному проєкту, а не один фінальний: «довгі
        // транзакції заборонені» (D-29), а блокування, взяте на першому
        // проєкті, трималося б до кінця прогону по всіх.
        var toMaterialize = new List<int>();
        var opened = new List<Period>();
        var skipped = new List<SkippedPeriodTransition>();
        var reopened = 0;
        var attempt = 0;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // ⚠ Стратегія повторів може виконати замикання вдруге — переліки
            // збираються заново, а не дописуються.
            toMaterialize.Clear();
            opened.Clear();
            skipped.Clear();
            reopened = 0;

            // ⛔ Аудит 2026-09-28, B5. І ТРЕКЕР — теж заново. Попередня спроба
            // відкотилась у базі, але лишила періоди проєкту в трекері вже
            // зміненими (`Grace` після системного Reopen): `FromSql` нижче
            // повертає ВІДСТЕЖУВАНІ екземпляри як є (identity resolution не
            // перезаписує їх значеннями з бази), план бачить `Grace` і Reopen
            // не повторює — а `SaveChanges` зберігає період уже без рядка
            // аудиту, що відкотився разом із першою спробою. Якщо ж упав сам
            // коміт ПІСЛЯ `SaveChanges`, зміни вже прийняті трекером, і період
            // не зберігся б узагалі. Відв'язати — і прочитати з бази наново.
            DetachPeriods(project.Id);

            // Проєкт відстежується на весь прогін: на повторі його `CurrentPeriod`
            // з першої спроби теж міг лишитись прийнятим трекером, хоч у базі
            // відкотився. Перечитати лише на повторі — перша спроба бере свіжий.
            if (attempt++ > 0)
            {
                await db.Entry(project).ReloadAsync(innerCt).ConfigureAwait(false);
            }

            // ⚠ UPDLOCK: Reopen бере той самий рядок так само (ФВ-1.10a).
            // Тепер блокування справді тримається до кінця транзакції.
            var periods = await db.Periods
                .FromSql($"""
                    SELECT * FROM doc.Period WITH (UPDLOCK, ROWLOCK)
                     WHERE ProjectId = {project.Id}
                    """)
                .ToListAsync(innerCt)
                .ConfigureAwait(false);

            // ⚠ Зворотні переходи (межі змінились після зміни політики) план
            // не застосовує, а повертає окремо — їх показуємо, а не кидаємо.
            //
            // ⚠ ФВ-1.8: річне вікно проєкту — те саме, що передають рішення про
            // запис (`AccessDecisionService`) і активація: інакше задача закрила б
            // грудень, а запис його ще дозволяв би (або навпаки).
            var plan = calculator.PlanTransitions(
                periods, utcNow, zone,
                YearGraceWindow.For(project.PeriodEnd, project.YearGraceOffsetDays, zone));
            skipped.AddRange(plan.Skipped);

            foreach (var (period, target) in plan.Transitions)
            {
                var before = period.State;
                period.AdvanceTo(target, utcNow);

                // ⚠ Лише «щойно відкрито» (`Scheduled → Open`): прогін, що застав період
                // уже в `Grace`/`Closed` (простій задачі), нагадувати «заповніть» не має.
                if (before == PeriodState.Scheduled && period.State == PeriodState.Open)
                {
                    opened.Add(period);
                }

                // ⚠ Зокрема `Scheduled → … → Closed` за один прогін (задача
                // простояла весь Open+Grace): задача нічого не запише, але
                // лишить `SkippedPeriodClosed` у журналі покриття — інакше
                // точки лишились би сирими мовчки.
                if (PeriodMaterializationTrigger.Requires(before, period.State))
                {
                    toMaterialize.Add(period.PeriodKeyValue);
                }
            }

            // ⚠ ФВ-1.8, D-204: закриті періоди року у вікні року — системний
            // Reopen тим самим доменним `Period.Reopen`, що й ручний (ФВ-1.10):
            // `Closed → Grace`, `ReopenedUntil` = кінець вікна, причина. Далі
            // період — звичайний Grace: запис з IsLateEdit, перерахунок, а
            // наприкінці вікна калькулятор сам закриває його (`Grace → Closed`).
            //
            // ⚠ Аудит — у ЦІЙ транзакції (`AuditWriter` пише тим самим
            // підключенням), як у `ReopenPeriodHandler`: журнал не має
            // розходитися з тим, що він описує.
            foreach (var reopen in plan.YearReopens)
            {
                var period = reopen.Period;
                var before = period.State;
                period.Reopen(reopen.Until, reopen.Reason, utcNow);
                reopened++;

                await _audit.WriteStructureChangeAsync(
                    new StructureChangeRecord(
                        utcNow,
                        TemplateVersionId: project.TemplateVersionId,
                        EntityType: "Period",
                        EntityId: period.Id,
                        ChangeClass: ChangeClass.Guarded,
                        Operation: "Reopen",
                        OldJson: JsonSerializer.Serialize(new { state = before.ToString() }),
                        NewJson: JsonSerializer.Serialize(new { state = period.State.ToString(), until = reopen.Until }),
                        ChangeReason: reopen.Reason,
                        ChangedByUserId: SystemUserId,
                        CorrelationId: AuditCorrelationId),
                    innerCt).ConfigureAwait(false);

                // `Closed → Grace` — перевідкриття: матеріалізація підхоплює
                // точки, пропущені як `SkippedPeriodClosed` (той самий тригер,
                // що в ручного Reopen).
                if (PeriodMaterializationTrigger.Requires(before, period.State))
                {
                    toMaterialize.Add(period.PeriodKeyValue);
                }
            }

            // Pinned не чіпається: «пін» — рішення людини, і задача не має
            // його скасовувати (D-77).
            if (project.CurrentPeriodMode == CurrentPeriodMode.Auto)
            {
                project.SetCurrentPeriodAutomatically(
                    calculator.SelectCurrentPeriod(periods)?.Id, utcNow);
            }

            await db.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // Лише закомічені: відкочена транзакція нічого не відкривала.
        _yearReopens += reopened;

        // ⛔ Матеріалізація з переходу — ПІСЛЯ коміту, не в
        // транзакції: черга не транзакційна, і задача, поставлена до коміту,
        // бачила б `Scheduled` або пережила б відкат переходу. Постановка
        // саме звідси, а не зі збору: за вимкненого або рідкого розкладу збір
        // відкриття не побачить ніколи, і точки, зібрані до нього, лишились би
        // сирими назавжди.
        await materialization
            .EnqueueAfterTransitionAsync(project.Id, toMaterialize, ct)
            .ConfigureAwait(false);

        await NotifyOpenedAsync(project, opened, ct).ConfigureAwait(false);

        return skipped;
    }

    /// <summary>Подія <see cref="NotificationEventKind.PeriodOpened"/> на кожен щойно відкритий період.</summary>
    /// <param name="project">Проєкт.</param>
    /// <param name="opened">Періоди, що перейшли <c>Scheduled → Open</c> у закоміченій транзакції.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ ПІСЛЯ коміту і без права валити прогін: сповіщення не транзакційні, а недоступна пошта
    /// не мусить повертати періоди проєкту в `Scheduled` чи ламати решту проєктів. Збій — у журнал.
    /// <para>
    /// ⚠ Текст — мовою каталогу за замовчуванням (як зведення збоїв): адресати каналу — явний
    /// перелік (J-4), мови одержувача система не знає. Підстановки <c>{project}</c>/<c>{period}</c>;
    /// ключ дедуплікації — проєкт+період, тож повторний прогін не дублює лист.
    /// </para>
    /// </remarks>
    private async Task NotifyOpenedAsync(Project project, List<Period> opened, CancellationToken ct)
    {
        if (opened.Count == 0 || notifications is null || catalog is null)
        {
            return;
        }

        try
        {
            var strings = (await catalog
                .GetAsync(Application.Localization.UiStringResolver.DefaultLanguage, ct)
                .ConfigureAwait(false)).Strings;

            foreach (var period in opened)
            {
                var label = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{period.PeriodKeyValue} ({period.PeriodStart:yyyy-MM-dd} – {period.PeriodEnd:yyyy-MM-dd})");

                string Fill(string key)
                    => strings.GetValueOrDefault(key, key)
                        .Replace("{project}", project.Code, StringComparison.Ordinal)
                        .Replace("{period}", label, StringComparison.Ordinal);

                await notifications
                    .DispatchAsync(
                        new NotificationEvent(
                            NotificationEventKind.PeriodOpened,
                            NotificationSeverity.Info,
                            string.Create(CultureInfo.InvariantCulture, $"period-opened:{project.Code}:{period.PeriodKeyValue}"),
                            Fill("notifications.periodOpened.subject"),
                            Fill("notifications.periodOpened.body")),
                        ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogOpenedNotificationFailed(_logger, project.Code, project.Id, ex);
        }
    }

    /// <summary>
    /// Відкидає незбережені зміни проєкту, на якому прогін упав.
    /// </summary>
    /// <param name="project">Проєкт, що не пройшов.</param>
    /// <remarks>
    /// ⛔ Контекст спільний на весь прогін. Транзакція провалу відкотилась, але
    /// трекер лишив би періоди цього проєкту <c>Modified</c> (півзастосований
    /// <c>AdvanceTo</c>), і <c>SaveChangesAsync</c> НАСТУПНОГО проєкту записав
    /// би їх повз власну транзакцію й повз рішення про збій — або впав би на
    /// тому самому рядку, перетворивши один збій на збій усіх наступних.
    /// Не <c>ChangeTracker.Clear()</c>: решта проєктів прогону відстежується
    /// і має зберегти свій <c>CurrentPeriod</c>.
    /// </remarks>
    private void Discard(Project project)
    {
        DetachPeriods(project.Id);

        var projectEntry = db.Entry(project);
        if (projectEntry.State is EntityState.Modified)
        {
            projectEntry.CurrentValues.SetValues(projectEntry.OriginalValues);
            projectEntry.State = EntityState.Unchanged;
        }
    }

    /// <summary>Відв'язує від трекера всі періоди проєкту.</summary>
    /// <param name="projectId">Проєкт.</param>
    private void DetachPeriods(int projectId)
    {
        foreach (var entry in db.ChangeTracker.Entries<Period>()
                     .Where(e => e.Entity.ProjectId == projectId)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Пише знахідки прогону в <c>itg.MaintenanceRun</c> — туди, звідки їх
    /// бере зведення адміністраторам (<see cref="NotificationJob"/>).
    /// </summary>
    /// <remarks>
    /// ⚠ Рішення: наявний журнал обслуговування, а не нова таблиця. Рядок
    /// лише коли є що сказати: успішний прогін уже видно в <c>itg.JobProgress</c>
    /// (<c>QuartzJobAdapter</c>), а щогодинний «Succeeded» — шум. Пропуск
    /// зворотного переходу — <c>Degraded</c>, збій проєкту — <c>Failed</c>;
    /// обидва потрапляють у зведення (<c>Status != "Succeeded"</c>).
    /// <para>
    /// ⚠ Збій самого запису не ковтається: він стає ще одним збоєм прогону й
    /// іде нагору разом із рештою — інакше пропуск, який бачив лише журнал,
    /// залишився б непоміченим адміністратором.
    /// </para>
    /// </remarks>
    private async Task RecordFindingsAsync(
        List<SkippedEntry> skipped, List<FailedEntry> failures, DateTime utcNow, CancellationToken ct)
    {
        if (skipped.Count == 0 && failures.Count == 0)
        {
            return;
        }

        var status = failures.Count > 0 ? MaintenanceRunFailure.FailedStatus : "Degraded";
        var details = JsonSerializer.Serialize(
            new
            {
                skippedCount = skipped.Count,
                skipped = skipped
                    .OrderBy(s => s.Project, StringComparer.Ordinal)
                    .ThenBy(s => s.Period)
                    .Take(MaxDetailItems),
                failedCount = failures.Count,
                failed = failures
                    .Take(MaxDetailItems)
                    .Select(f => new { project = f.ProjectCode, error = Shorten(f.Error.Message) }),
            },
            DetailsOptions);

        MaintenanceRun? run = null;

        try
        {
            // ⚠ Та сама знахідка, уже записана за останню добу, — не повторюємо
            // (див. RepeatFindingsAfter). Порівняння точне: деталі детерміновані
            // (упорядковані, без часу), тож нова знахідка чи зниклий пропуск
            // дають новий рядок одразу.
            var repeatFrom = utcNow - RepeatFindingsAfter;
            var last = await db.MaintenanceRuns
                .AsNoTracking()
                .Where(r => r.JobCode == Code && r.StartedAt > repeatFrom && r.StartedAt <= utcNow)
                .OrderByDescending(r => r.StartedAt)
                .ThenByDescending(r => r.Id)
                .Select(r => new { r.Status, r.DetailsJson })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (last is not null && last.Status == status && last.DetailsJson == details)
            {
                return;
            }

            run = new MaintenanceRun(Code, utcNow);
            run.Complete(status, details, utcNow);
            db.MaintenanceRuns.Add(run);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Незаписаний рядок не лишається в трекері: контекст спільний зі
            // сховищем прогресу, і його наступне збереження впало б на ньому ж.
            if (run is not null)
            {
                db.Entry(run).State = EntityState.Detached;
            }

            LogFindingsNotRecorded(_logger, skipped.Count, failures.Count, ex);
            failures.Add(new FailedEntry(Code, ex));
        }
    }

    private static string Shorten(string message)
        => message.Length > MaxErrorLength ? string.Concat(message.AsSpan(0, MaxErrorLength), "…") : message;

    /// <summary>Правила поясу майданчика за збереженим ідентифікатором IANA.</summary>
    /// <remarks>
    /// ⛔ Мовчазного UTC тут НЕМАЄ і бути не може. Він був: порожній
    /// ідентифікатор повертав <c>TimeZoneInfo.Utc</c> — при тому, що сусідній
    /// коментар обіцяв протилежне. Для майданчика на <c>Asia/Atyrau</c> це
    /// зсунуло б кожну межу періоду на п'ять годин, і «31 числа о 23:59»
    /// закривалося б о 18:59 за місцем — тобто рівно посеред робочого дня,
    /// коли форми ще дозаповнюють. Помітили б це лише за скаргою «не встиг
    /// подати», і причину шукали б де завгодно, крім порожньої колонки.
    ///
    /// ⚠ Виняток тут — не аварія задачі, а єдиний спосіб дізнатися, що в базі
    /// лежить пояс, якого система не знає. Він зупиняє лише свій проєкт:
    /// решта обробляється, а виняток іде нагору наприкінці прогону.
    /// </remarks>
    private static TimeZoneInfo ResolveZone(string? timeZoneId)
        => SiteTimeZone.Create(timeZoneId).ToTimeZoneInfo();

    /// <summary>Пропущений зворотний перехід — пункт <c>DetailsJson</c>.</summary>
    private sealed record SkippedEntry(string Project, int Period, string From, string To, string Reason);

    /// <summary>Проєкт, на якому прогін упав.</summary>
    private sealed record FailedEntry(string ProjectCode, Exception Error);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "PeriodStateJob: період {PeriodKey} проєкту {ProjectCode} ({ProjectId}) — зворотний перехід {From} → {To} пропущено. {Reason}")]
    private static partial void LogBackwardTransitionSkipped(
        ILogger logger, string projectCode, int projectId, int periodKey, PeriodState from, PeriodState to, string reason);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "PeriodStateJob: проєкт {ProjectCode} ({ProjectId}) не оброблено; решта проєктів обробляється далі.")]
    private static partial void LogProjectFailed(ILogger logger, string projectCode, int projectId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "PeriodStateJob: не вдалося записати знахідки прогону в itg.MaintenanceRun (пропусків {Skipped}, збоїв {Failed}).")]
    private static partial void LogFindingsNotRecorded(ILogger logger, int skipped, int failed, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "PeriodStateJob: системно відкрито періодів — {Count}; поставлено разовий пошук осиротілих рядків.")]
    private static partial void LogOrphanScanEnqueued(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "PeriodStateJob: проєкт {ProjectCode} ({ProjectId}) — сповіщення «період відкрито» не розіслано; стани періодів уже збережено.")]
    private static partial void LogOpenedNotificationFailed(ILogger logger, string projectCode, int projectId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "PeriodStateJob: системно відкрито періодів — {Count}, але пошук осиротілих рядків НЕ поставлено; "
            + "позначки оновить нічний прохід.")]
    private static partial void LogOrphanScanNotEnqueued(ILogger logger, int count, Exception exception);
}
