// src/Ecr.Application/Integration/CollectionScheduleHandlers.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
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
    string RowVersion);

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
    /// <param name="ct">Скасування.</param>
    public async Task<IReadOnlyList<CollectionScheduleView>> HandleAsync(CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var rows = await store.ListAsync(ct).ConfigureAwait(false);

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
            VersionOf(row.Schedule));

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
    private static string? NormalizeETag(string? header)
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
    IClock clock)
{
    /// <summary>Змінює cron і стан розкладу; повертає його новий вигляд.</summary>
    /// <param name="id">Розклад.</param>
    /// <param name="cron">Новий вираз cron (формат Quartz).</param>
    /// <param name="isEnabled">Чи має розклад стояти в планувальнику.</param>
    /// <param name="ifMatch">Заголовок <c>If-Match</c> зі значенням <c>rowVersion</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="BusinessRuleException">Cron порожній, задовгий або невалідний — 422.</exception>
    /// <exception cref="ConcurrencyConflictException">Розклад змінили паралельно — 409.</exception>
    /// <exception cref="NotFoundException">Розкладу немає — 404.</exception>
    public async Task<CollectionScheduleView> HandleAsync(
        int id, string cron, bool isEnabled, string? ifMatch, CancellationToken ct)
    {
        await PermissionCheck
            .RequireAsync(access, currentUser, ListCollectionSchedulesHandler.Permission, ct)
            .ConfigureAwait(false);

        var text = ListCollectionSchedulesHandler.RequireValidCron(scheduler, cron);

        var row = await ListCollectionSchedulesHandler.FindAsync(store, id, ct).ConfigureAwait(false);
        ListCollectionSchedulesHandler.RequireCurrentVersion(row.Schedule, ifMatch);

        row.Schedule.Reschedule(text);

        if (isEnabled)
        {
            row.Schedule.Enable();
        }
        else
        {
            row.Schedule.Disable();
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

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
    IClock clock)
{
    /// <summary>Заводить розклад і ставить його в планувальник.</summary>
    /// <param name="sourceEntityId">Сутність джерела, яку збиратимуть.</param>
    /// <param name="cron">Вираз cron (формат Quartz).</param>
    /// <param name="isEnabled">Чи має розклад одразу стояти в планувальнику.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="BusinessRuleException">Cron порожній, задовгий або невалідний — 422.</exception>
    /// <exception cref="NotFoundException">Сутності джерела немає — 404.</exception>
    /// <exception cref="ConcurrencyConflictException">Розклад у сутності вже є — 409.</exception>
    public async Task<CollectionScheduleView> HandleAsync(
        int sourceEntityId, string cron, bool isEnabled, CancellationToken ct)
    {
        await PermissionCheck
            .RequireAsync(access, currentUser, ListCollectionSchedulesHandler.Permission, ct)
            .ConfigureAwait(false);

        var text = ListCollectionSchedulesHandler.RequireValidCron(scheduler, cron);

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

        var schedule = new CollectionSchedule(sourceEntityId, text);

        if (!isEnabled)
        {
            schedule.Disable();
        }

        store.Add(schedule);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        await ListCollectionSchedulesHandler
            .ApplyOrFailAsync(applier, uow, clock, schedule, ct).ConfigureAwait(false);

        return ListCollectionSchedulesHandler.ToView(
            new ScheduledSourceEntity(schedule, entity.Code, entity.Name));
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
    ICurrentUser currentUser)
{
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

        store.Remove(row.Schedule);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
