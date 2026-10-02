// src/Ecr.Application/Integration/CollectionScheduleHandlers.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Sources;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Integration;

/// <summary>Розклад збору — рядок екрана конфігуратора (ФВ-14.3, <c>BE-21b</c>).</summary>
/// <param name="Id">Ідентифікатор розкладу.</param>
/// <param name="SourceEntityId">Сутність джерела, яку збирають за цим розкладом.</param>
/// <param name="SourceEntityCode">Код сутності в джерелі.</param>
/// <param name="SourceEntityName">Підпис сутності; <c>null</c> — каталог джерела його не дав.</param>
/// <param name="Cron">Вираз cron (формат Quartz, 6–7 полів).</param>
/// <param name="IsEnabled">Чи стоїть розклад у планувальнику.</param>
/// <param name="LastRunAt">Коли збір за цим розкладом відпрацював востаннє.</param>
/// <param name="LastError">
/// Чому розклад НЕ поставлено; <c>null</c> — поставлено. ⚠ Це стан ПОСТАНОВКИ,
/// а не збору: відмови самого збору живуть у <c>itg.CollectionRun</c>.
/// </param>
/// <param name="LastErrorAt">Коли постановка не вдалася.</param>
/// <param name="RowVersion">
/// Версія рядка в Base64 — її ж клієнт повертає заголовком <c>If-Match</c>.
/// </param>
/// <param name="DataSourceId">З'єднання, якому належить сутність джерела.</param>
/// <param name="DataSourceCode">Код цього з'єднання — значення фільтра <c>?dataSource=</c>.</param>
/// <param name="LookbackDays">
/// Вікно збору назад від моменту запуску, днів (ФВ-13.15): кожен прогін перечитує
/// саме стільки, і пропущені вікна закриваються повтором, а не станом.
/// </param>
/// <param name="DependsOnScheduleId">
/// Розклад того ж з'єднання, після успішного прогону якого цей запускається
/// (ФВ-13.15 «залежності»); <c>null</c> — залежності немає.
/// </param>
public sealed record CollectionScheduleView(
    int Id,
    int SourceEntityId,
    string SourceEntityCode,
    string? SourceEntityName,
    string Cron,
    bool IsEnabled,
    DateTime? LastRunAt,
    string? LastError,
    DateTime? LastErrorAt,
    string RowVersion,
    int DataSourceId,
    string DataSourceCode,
    int LookbackDays,
    int? DependsOnScheduleId = null);

/// <summary>
/// Перелік розкладів збору. Право <c>Integration.EditSchedule</c>.
/// </summary>
/// <remarks>
/// ⛔ Право на ПЕРЕЛІК те саме, що й на правку, і це свідомо: розклад видно
/// рівно там, де його правлять, — на екрані конфігуратора інтеграції. Окреме
/// право «дивитися розклад» означало б третю сутність у каталозі прав заради
/// екрана, якого немає.
/// </remarks>
public sealed class ListCollectionSchedulesHandler(
    ICollectionScheduleStore store, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право на редагування розкладу збору (`02-contracts.md` §9).</summary>
    public const string Permission = "Integration.EditSchedule";

    /// <summary>Віддає розклади разом із кодом і назвою сутності джерела.</summary>
    /// <param name="dataSource">
    /// Код з'єднання: лише його розклади; порожній — усі. Невідомий код — порожній
    /// перелік, а не <c>404</c>: це фільтр, а не адресація.
    /// </param>
    /// <param name="ct">Скасування.</param>
    public async Task<IReadOnlyList<CollectionScheduleView>> HandleAsync(string? dataSource, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var code = string.IsNullOrWhiteSpace(dataSource) ? null : dataSource.Trim();
        var rows = await store.ListAsync(code, ct).ConfigureAwait(false);

        return [.. rows.Select(ToView)];
    }

    /// <summary>Версія рядка в тому вигляді, у якому вона їде клієнтові й назад.</summary>
    internal static string VersionOf(CollectionSchedule schedule)
        => Convert.ToBase64String(schedule.RowVersion);

    internal static CollectionScheduleView ToView(ScheduledSourceEntity row)
        => new(
            row.Schedule.Id,
            row.Schedule.SourceEntityId,
            row.SourceEntityCode,
            row.SourceEntityName,
            row.Schedule.CronExpression,
            row.Schedule.IsEnabled,
            row.Schedule.LastRunAt,
            row.Schedule.LastError,
            row.Schedule.LastErrorAt,
            VersionOf(row.Schedule),
            row.DataSourceId,
            row.DataSourceCode,
            row.Schedule.LookbackDays,
            row.Schedule.DependsOnScheduleId);

    internal static async Task<ScheduledSourceEntity> FindAsync(
        ICollectionScheduleStore store, int id, CancellationToken ct)
        => await store.FindAsync(id, ct).ConfigureAwait(false)
           ?? throw new NotFoundException(
               ErrorCodes.SourceEntityNotFound,
               $"Розкладу збору {id} не існує.",
               new Dictionary<string, object?>
               {
                   ["messageKey"] = "err.ECR-INT-0404.collectionSchedule",
                   ["id"] = id.ToString(CultureInfo.InvariantCulture),
               });

    /// <summary>
    /// Звіряє <c>If-Match</c> із версією рядка.
    /// </summary>
    /// <param name="schedule">Розклад у тому стані, у якому він зараз у базі.</param>
    /// <param name="ifMatch">Значення заголовка; <c>null</c> — заголовка не було.</param>
    /// <exception cref="ConcurrencyConflictException">
    /// Версія не та — <c>409 ECR-JOB-0409</c>: розклад змінили між читанням і
    /// записом.
    /// </exception>
    /// <remarks>
    /// ⛔ Власна перевірка, а не покладання на токен EF. Токен порівнює версію з
    /// тією, яку ЦЕЙ запит щойно прочитав, тобто ловить лише збіг у мілісекунди
    /// між читанням і <c>SaveChanges</c>. Втрата правки, заради якої на рядку
    /// з'явився <c>rowversion</c>, відбувається зовсім не там: двоє відкрили
    /// екран, один зберіг, другий зберігає поверх через хвилину — і для EF це
    /// цілком свіжий запис.
    ///
    /// ⚠ Заголовок обов'язковий: запит без нього — це запит того, хто розкладу
    /// не читав, і мовчки пропустити його означало б лишити «останній перемагає»
    /// для всіх клієнтів, які просто забули заголовок.
    /// </remarks>
    internal static void RequireCurrentVersion(CollectionSchedule schedule, string? ifMatch)
    {
        var expected = NormalizeETag(ifMatch)
                       ?? throw new BusinessRuleException(
                           ErrorCodes.RequestInvalid,
                           "Запит на зміну розкладу має нести заголовок If-Match зі значенням rowVersion.",
                           new Dictionary<string, object?>
                           {
                               ["messageKey"] = "err.ECR-REQ-0422.collectionScheduleIfMatch",
                           });

        if (!string.Equals(expected, VersionOf(schedule), StringComparison.Ordinal))
        {
            throw new ConcurrencyConflictException(
                ErrorCodes.JobStateConflict,
                $"Розклад {schedule.Id} змінили після того, як його прочитали.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-JOB-0409.collectionScheduleChanged",
                    ["rowVersion"] = VersionOf(schedule),
                });
        }
    }

    /// <summary>
    /// Перевіряє вираз cron ДО бази і повертає його обрізаним.
    /// </summary>
    /// <param name="scheduler">Планувальник — єдиний, хто знає синтаксис.</param>
    /// <param name="cron">Вираз із запиту.</param>
    /// <exception cref="BusinessRuleException">Порожній, задовгий або невалідний — 422.</exception>
    /// <remarks>
    /// ⚠ Довжина — ПЕРШОЮ: 101 символ «*» невалідний і як cron, і як значення
    /// стовпця, а користувачеві корисніша та відмова, яку він може виконати.
    ///
    /// ⛔ Спільна для створення і зміни навмисно. Дві копії цих двох перевірок
    /// розійшлися б першою ж правкою, і тоді створити розклад, який не можна
    /// зберегти правкою, було б можна.
    /// </remarks>
    internal static string RequireValidCron(IBackgroundJobScheduler scheduler, string? cron)
    {
        var text = (cron ?? string.Empty).Trim();

        if (text.Length is 0 or > CollectionSchedule.MaxCronLength)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Вираз cron має бути від 1 до {CollectionSchedule.MaxCronLength} символів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.collectionScheduleCronLength",
                    ["max"] = CollectionSchedule.MaxCronLength.ToString(CultureInfo.InvariantCulture),
                });
        }

        // ⛔ Саме тут, ДО бази. Прибрати цей рядок — і невалідний cron
        // збережеться, а відмова прийде вже від планувальника, тобто після того,
        // як запис у базі змінено.
        if (!scheduler.IsValidCron(text, out var cronError))
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Вираз cron «{text}» не приймається планувальником: {cronError}",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.collectionScheduleCron",
                    ["cron"] = text,
                    ["reason"] = cronError ?? string.Empty,
                });
        }

        return text;
    }

    /// <summary>Перевіряє вікно збору назад ДО бази (ФВ-13.15).</summary>
    /// <param name="lookbackDays">Днів; <c>null</c> — поле не прийшло, перевіряти нічого.</param>
    /// <exception cref="BusinessRuleException">Поза межами — 422.</exception>
    /// <remarks>
    /// ⛔ Саме тут, а не лише в домені: доменна межа кидає
    /// <c>ArgumentOutOfRangeException</c>, а той доїжджає до людини як <c>500</c>.
    /// </remarks>
    internal static void RequireValidLookback(int? lookbackDays)
    {
        if (lookbackDays is { } days
            && (days < CollectionSchedule.MinLookbackDays || days > CollectionSchedule.MaxLookbackDays))
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Вікно збору має бути від {CollectionSchedule.MinLookbackDays} до {CollectionSchedule.MaxLookbackDays} днів, а не {days}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.collectionScheduleLookback",
                    ["min"] = CollectionSchedule.MinLookbackDays.ToString(CultureInfo.InvariantCulture),
                    ["max"] = CollectionSchedule.MaxLookbackDays.ToString(CultureInfo.InvariantCulture),
                    ["value"] = days.ToString(CultureInfo.InvariantCulture),
                });
        }
    }

    /// <summary>
    /// Відмовляє в розкладі для власної форми ECR (ФВ-12.8, <c>D-106</c>).
    /// </summary>
    /// <param name="sourceKind">Хто master для даних сутності.</param>
    /// <param name="sourceEntityCode">Код сутності — для тексту відмови.</param>
    /// <exception cref="BusinessRuleException">Сутність — власна форма — 422.</exception>
    /// <remarks>
    /// ⛔ <see cref="RegistrySourceKind.Local"/> означає, що master даних — сам
    /// ECR (ФВ-8.9): дані приходять записом із наших форм і одразу запускають
    /// перерахунок. Розклад для такої сутності опитував би власну базу про те,
    /// що ми самі щойно в неї поклали.
    /// </remarks>
    internal static void RequireCollectableSource(RegistrySourceKind sourceKind, string sourceEntityCode)
    {
        if (sourceKind == RegistrySourceKind.Local)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Сутність «{sourceEntityCode}» — власна форма ECR: розклад збору для неї заборонено (ФВ-12.8).",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.scheduleForLocalEntity",
                    ["code"] = sourceEntityCode,
                });
        }
    }

    /// <summary>
    /// Доводить уже збережений розклад до планувальника; невдача лишається в
    /// рядку і у відповіді.
    /// </summary>
    /// <param name="applier">Постановка в планувальник.</param>
    /// <param name="uow">Одиниця роботи — нею фіксується позначка невдачі.</param>
    /// <param name="clock">Годинник.</param>
    /// <param name="schedule">Розклад у вже збереженому стані.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ Відповідь чесна: розклад уже збережено, а в планувальнику його немає —
    /// і саме це користувач має побачити. Мовчазне <c>200</c> означало б екран,
    /// на якому збір «увімкнено», хоча не відбудеться жодного разу.
    /// </remarks>
    internal static async Task ApplyOrFailAsync(
        CollectionScheduleApplier applier,
        IUnitOfWork uow,
        IClock clock,
        CollectionSchedule schedule,
        CancellationToken ct)
    {
        try
        {
            await applier.ApplyAsync(schedule, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Відмова планувальника — це відповідь запиту, а не аварія процесу.
        catch (Exception e) when (e is not OperationCanceledException)
#pragma warning restore CA1031
        {
            schedule.MarkInvalid(e.Message, clock.UtcNow);
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);

            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Розклад {schedule.Id} збережено, але планувальник його не прийняв: {e.Message}",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.collectionScheduleNotApplied",
                    ["reason"] = e.Message,
                });
        }
    }

    /// <summary>Значення <c>If-Match</c> без лапок і слабкої позначки; <c>null</c> — порожнє.</summary>
    /// <remarks>
    /// ⚠ HTTP вимагає ETag у лапках (<c>"…"</c>), а частина клієнтів шле голе
    /// значення. Приймаються обидві форми: відмовити через лапки означало б
    /// віддати 422 за правильно виконану вимогу.
    /// </remarks>
    internal static string? NormalizeETag(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var text = header.Trim();

        if (text.StartsWith("W/", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        text = text.Trim('"');

        return text.Length == 0 ? null : text;
    }
}

/// <summary>
/// Зміна cron і вмикання/вимикання розкладу. Право <c>Integration.EditSchedule</c>.
/// </summary>
/// <remarks>
/// ⚠ Порядок кроків не випадковий: перевірка cron → запис → постановка. Доки
/// правки розкладу не було, невалідний cron міг потрапити в базу лише імпортом,
/// і старт його просто пропускав. Тепер його вводить людина — і відмова мусить
/// прийти ДО бази, інакше екран показував би збережений розклад, за яким нічого
/// не збирається.
/// </remarks>
public sealed class SaveCollectionScheduleHandler(
    ICollectionScheduleStore store,
    IBackgroundJobScheduler scheduler,
    CollectionScheduleApplier applier,
    IAccessDecisionService access,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IClock clock,
    IAuditWriter audit)
{
    /// <summary>Операція в журналі структурних змін (<c>ФВ-12.10</c>).</summary>
    public const string AuditOperation = "SaveCollectionSchedule";

    /// <summary>Змінює cron і стан розкладу; повертає його новий вигляд.</summary>
    /// <param name="id">Розклад.</param>
    /// <param name="cron">Новий вираз cron (формат Quartz).</param>
    /// <param name="isEnabled">Чи має розклад стояти в планувальнику.</param>
    /// <param name="lookbackDays">Вікно збору назад, днів (ФВ-13.15); <c>null</c> — лишити наявне.</param>
    /// <param name="ifMatch">Заголовок <c>If-Match</c> зі значенням <c>rowVersion</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="BusinessRuleException">
    /// Cron порожній, задовгий або невалідний; вікно поза межами; увімкнення
    /// розкладу власної форми (ФВ-12.8) — 422.
    /// </exception>
    /// <exception cref="ConcurrencyConflictException">Розклад змінили паралельно — 409.</exception>
    /// <exception cref="NotFoundException">Розкладу немає — 404.</exception>
    /// <remarks>
    /// ⚠ Для власної форми відмовляє лише УВІМКНЕННЯ: розклад, заведений до
    /// правила, має лишатися можливим вимкнути й прибрати.
    /// </remarks>
    public Task<CollectionScheduleView> HandleAsync(
        int id, string cron, bool isEnabled, int? lookbackDays, string? ifMatch, CancellationToken ct)
        => HandleAsync(id, cron, isEnabled, lookbackDays, ScheduleDependencyChange.Unchanged, ifMatch, ct);

    /// <summary>Те саме, із зміною залежності від іншого розкладу (ФВ-13.15).</summary>
    /// <param name="id">Розклад.</param>
    /// <param name="cron">Новий вираз cron (формат Quartz).</param>
    /// <param name="isEnabled">Чи має розклад стояти в планувальнику.</param>
    /// <param name="lookbackDays">Вікно збору назад, днів; <c>null</c> — лишити наявне.</param>
    /// <param name="dependency">Зміна залежності: нова, зняти або лишити.</param>
    /// <param name="ifMatch">Заголовок <c>If-Match</c> зі значенням <c>rowVersion</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="BusinessRuleException">
    /// Залежність не існує, з іншого з'єднання або замикає цикл — 422.
    /// </exception>
    public async Task<CollectionScheduleView> HandleAsync(
        int id,
        string cron,
        bool isEnabled,
        int? lookbackDays,
        ScheduleDependencyChange dependency,
        string? ifMatch,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dependency);

        await PermissionCheck
            .RequireAsync(access, currentUser, ListCollectionSchedulesHandler.Permission, ct)
            .ConfigureAwait(false);

        var text = ListCollectionSchedulesHandler.RequireValidCron(scheduler, cron);
        ListCollectionSchedulesHandler.RequireValidLookback(lookbackDays);

        var row = await ListCollectionSchedulesHandler.FindAsync(store, id, ct).ConfigureAwait(false);
        ListCollectionSchedulesHandler.RequireCurrentVersion(row.Schedule, ifMatch);

        if (!dependency.Clear && dependency.DependsOnScheduleId is { } dependsOn)
        {
            await CollectionScheduleDependencyRules
                .RequireValidAsync(store, id, row.DataSourceId, dependsOn, ct).ConfigureAwait(false);
        }

        if (isEnabled)
        {
            ListCollectionSchedulesHandler.RequireCollectableSource(row.SourceKind, row.SourceEntityCode);
        }

        var before = IntegrationConfigAudit.Snapshot(row.Schedule);

        row.Schedule.Reschedule(text);

        if (lookbackDays is { } days)
        {
            row.Schedule.SetLookback(days);
        }

        if (dependency.Clear)
        {
            row.Schedule.SetDependency(null);
        }
        else if (dependency.DependsOnScheduleId is { } newDependency)
        {
            row.Schedule.SetDependency(newDependency);
        }

        if (isEnabled)
        {
            row.Schedule.Enable();
        }
        else
        {
            row.Schedule.Disable();
        }

        // ФВ-12.10: старий і новий розклад (cron, вмикання, вікно) — у журналі, в одній транзакції зі збереженням.
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            await IntegrationConfigAudit.WriteAsync(
                audit, clock, currentUser, IntegrationConfigAudit.ScheduleType, row.Schedule.Id, AuditOperation,
                before, IntegrationConfigAudit.Snapshot(row.Schedule),
                IntegrationConfigAudit.Reason("integrationAudit.scheduleChanged", ("id", row.Schedule.Id), ("entity", row.SourceEntityCode)), innerCt)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // ⚠ Постановка ПІСЛЯ збереження (`CollectionScheduleApplier`): до неї
        // розклади читалися з бази рівно раз, на старті, і правка не доходила до
        // планувальника до перезапуску.
        await ListCollectionSchedulesHandler
            .ApplyOrFailAsync(applier, uow, clock, row.Schedule, ct).ConfigureAwait(false);

        row.Schedule.ClearError();
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return ListCollectionSchedulesHandler.ToView(row);
    }
}

/// <summary>
/// Створення розкладу збору для сутності джерела. Право <c>Integration.EditSchedule</c>.
/// </summary>
/// <remarks>
/// ⛔ Доти розклад заводився ЛИШЕ скриптом: сутність джерела можна було додати з
/// інтерфейсу, а призначити їй збір — ні. Тобто конфігуратор інтеграції вмів
/// правити й видаляти те, чого не вмів створити.
///
/// ⚠ Порядок кроків той самий, що й у правці: перевірка cron → перевірка
/// сутності → запис → постановка. Дублікат ловиться ДО запису (<c>409</c>), і це
/// не косметика: другий розклад на ту саму сутність — це другий тригер Quartz із
/// тим самим payload, тобто подвійний збір, якого не видно ніде, крім кількості
/// прогонів.
///
/// ⛔ <c>If-Match</c> тут НЕ вимагається: створення нічого не перезаписує, а
/// вимагати версію рядка, якого ще немає, нема з чого. Захист від двох
/// одночасних створень дає саме перевірка дубліката.
/// </remarks>
public sealed class CreateCollectionScheduleHandler(
    ICollectionScheduleStore store,
    IBackgroundJobScheduler scheduler,
    CollectionScheduleApplier applier,
    IAccessDecisionService access,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IClock clock,
    IAuditWriter audit)
{
    /// <summary>Операція в журналі структурних змін (<c>ФВ-12.10</c>).</summary>
    public const string AuditOperation = "CreateCollectionSchedule";

    /// <summary>Заводить розклад і ставить його в планувальник.</summary>
    /// <param name="sourceEntityId">Сутність джерела, яку збиратимуть.</param>
    /// <param name="cron">Вираз cron (формат Quartz).</param>
    /// <param name="isEnabled">Чи має розклад одразу стояти в планувальнику.</param>
    /// <param name="lookbackDays">Вікно збору назад, днів (ФВ-13.15); <c>null</c> — типове.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="BusinessRuleException">
    /// Cron порожній, задовгий або невалідний; вікно поза межами; сутність —
    /// власна форма ECR (ФВ-12.8) — 422.
    /// </exception>
    /// <exception cref="NotFoundException">Сутності джерела немає — 404.</exception>
    /// <exception cref="ConcurrencyConflictException">Розклад у сутності вже є — 409.</exception>
    /// <remarks>
    /// ⛔ Власній формі відмовляє і ВИМКНЕНЕ створення: ФВ-12.8 забороняє
    /// заводити розклад, а не лише запускати його.
    /// </remarks>
    public Task<CollectionScheduleView> HandleAsync(
        int sourceEntityId, string cron, bool isEnabled, int? lookbackDays, CancellationToken ct)
        => HandleAsync(sourceEntityId, cron, isEnabled, lookbackDays, dependsOnScheduleId: null, ct);

    /// <summary>Те саме, із залежністю від іншого розкладу того ж з'єднання (ФВ-13.15).</summary>
    /// <param name="sourceEntityId">Сутність джерела, яку збиратимуть.</param>
    /// <param name="cron">Вираз cron (формат Quartz).</param>
    /// <param name="isEnabled">Чи має розклад одразу стояти в планувальнику.</param>
    /// <param name="lookbackDays">Вікно збору назад, днів; <c>null</c> — типове.</param>
    /// <param name="dependsOnScheduleId">Розклад-залежність; <c>null</c> — без залежності.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="BusinessRuleException">Залежність не існує або з іншого з'єднання — 422.</exception>
    public async Task<CollectionScheduleView> HandleAsync(
        int sourceEntityId,
        string cron,
        bool isEnabled,
        int? lookbackDays,
        int? dependsOnScheduleId,
        CancellationToken ct)
    {
        await PermissionCheck
            .RequireAsync(access, currentUser, ListCollectionSchedulesHandler.Permission, ct)
            .ConfigureAwait(false);

        var text = ListCollectionSchedulesHandler.RequireValidCron(scheduler, cron);
        ListCollectionSchedulesHandler.RequireValidLookback(lookbackDays);

        var entity = await store.FindSourceEntityAsync(sourceEntityId, ct).ConfigureAwait(false)
                     ?? throw new NotFoundException(
                         ErrorCodes.SourceEntityNotFound,
                         $"Сутності джерела {sourceEntityId} не існує.",
                         new Dictionary<string, object?>
                         {
                             ["messageKey"] = "err.ECR-INT-0404.sourceEntity",
                             ["id"] = sourceEntityId.ToString(CultureInfo.InvariantCulture),
                         });

        if (entity.ScheduleId is { } existing)
        {
            throw new ConcurrencyConflictException(
                ErrorCodes.JobStateConflict,
                $"Сутність джерела {sourceEntityId} уже має розклад {existing}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-JOB-0409.collectionScheduleExists",
                    ["scheduleId"] = existing.ToString(CultureInfo.InvariantCulture),
                });
        }

        ListCollectionSchedulesHandler.RequireCollectableSource(entity.SourceKind, entity.Code);

        if (dependsOnScheduleId is { } dependsOn)
        {
            await CollectionScheduleDependencyRules
                .RequireValidAsync(store, selfId: null, entity.DataSourceId, dependsOn, ct).ConfigureAwait(false);
        }

        var schedule = new CollectionSchedule(sourceEntityId, text);
        schedule.SetDependency(dependsOnScheduleId);

        if (lookbackDays is { } days)
        {
            schedule.SetLookback(days);
        }

        if (!isEnabled)
        {
            schedule.Disable();
        }

        store.Add(schedule);

        // ФВ-12.10: новий розклад — у журналі структурних змін, в одній транзакції зі збереженням.
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            await IntegrationConfigAudit.WriteAsync(
                audit, clock, currentUser, IntegrationConfigAudit.ScheduleType, schedule.Id, AuditOperation,
                oldJson: null, newJson: IntegrationConfigAudit.Snapshot(schedule),
                reason: IntegrationConfigAudit.Reason("integrationAudit.scheduleCreated", ("id", schedule.Id), ("entity", entity.Code)), innerCt)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await ListCollectionSchedulesHandler
            .ApplyOrFailAsync(applier, uow, clock, schedule, ct).ConfigureAwait(false);

        return ListCollectionSchedulesHandler.ToView(
            new ScheduledSourceEntity(
                schedule, entity.Code, entity.Name, entity.DataSourceId, entity.DataSourceCode, entity.SourceKind));
    }
}

/// <summary>
/// Видалення розкладу збору. Право <c>Integration.EditSchedule</c>.
/// </summary>
public sealed class DeleteCollectionScheduleHandler(
    ICollectionScheduleStore store,
    IBackgroundJobScheduler scheduler,
    IAccessDecisionService access,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IClock clock,
    IAuditWriter audit)
{
    /// <summary>Операція в журналі структурних змін (<c>ФВ-12.10</c>).</summary>
    public const string AuditOperation = "DeleteCollectionSchedule";

    /// <summary>Знімає розклад із планувальника і прибирає рядок.</summary>
    /// <param name="id">Розклад.</param>
    /// <param name="ifMatch">Заголовок <c>If-Match</c> зі значенням <c>rowVersion</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ <c>UnscheduleAsync</c> тут обов'язковий і не дублює нічого. Рядок у
    /// <c>ext.CollectionSchedule</c> читає ЛИШЕ старт застосунку; тригер Quartz
    /// живе окремо. Видалити рядок і не зняти тригер означає збір за розкладом,
    /// якого вже немає, — аж до наступного перезапуску, і жодного місця, де цей
    /// розклад було б видно.
    /// </remarks>
    public async Task HandleAsync(int id, string? ifMatch, CancellationToken ct)
    {
        await PermissionCheck
            .RequireAsync(access, currentUser, ListCollectionSchedulesHandler.Permission, ct)
            .ConfigureAwait(false);

        var row = await ListCollectionSchedulesHandler.FindAsync(store, id, ct).ConfigureAwait(false);
        ListCollectionSchedulesHandler.RequireCurrentVersion(row.Schedule, ifMatch);

        await scheduler
            .UnscheduleAsync<ICollectionJob>(
                CollectionScheduleApplier.PayloadOf(row.Schedule.SourceEntityId), ct)
            .ConfigureAwait(false);

        // ФВ-12.10: що саме було видалено (старий розклад) — у журналі, в одній транзакції з видаленням.
        var removed = IntegrationConfigAudit.Snapshot(row.Schedule);

        // ФВ-13.15: ключ-самопосилання без каскаду — хто залежав від цього розкладу, перестає залежати.
        foreach (var dependent in await store.FindDependentsAsync(id, ct).ConfigureAwait(false))
        {
            dependent.SetDependency(null);
        }

        store.Remove(row.Schedule);

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            await IntegrationConfigAudit.WriteAsync(
                audit, clock, currentUser, IntegrationConfigAudit.ScheduleType, id, AuditOperation,
                removed, newJson: null,
                IntegrationConfigAudit.Reason("integrationAudit.scheduleDeleted", ("id", id), ("entity", row.SourceEntityCode)), innerCt)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
