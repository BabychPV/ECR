// src/Ecr.Application/Periods/ReopenPeriodHandler.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Microsoft.Extensions.Logging;

namespace Ecr.Application.Periods;

/// <summary>
/// Адміністративне відкриття закритого періоду (ФВ-1.10). Право
/// <c>Period.Reopen</c> — небезпечне, у складені ролі не входить (ФВ-6.12).
/// </summary>
public sealed partial class ReopenPeriodHandler(
    IPeriodStore periods,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock,
    IMaterializationScheduler? materialization = null,
    IBackgroundJobScheduler? jobs = null,
    ILogger<ReopenPeriodHandler>? logger = null)
{
    /// <summary>Ціль витісняючої постановки пошуку осиротілих після ручного Reopen.</summary>
    /// <remarks>
    /// ⚠ Та сама, що в системного Reopen (<c>PeriodStateJob</c>), і той самий
    /// маркер: сканер один на весь набір, тож кілька відкриттів поспіль дають
    /// один прогін, а не чергу однакових.
    /// </remarks>
    public const string OrphanScanTarget = "period-reopen";

    // ⚠ `materialization` необов'язковий лише заради наявних прямих
    // конструювань обробника в тестах; у застосунку порт зареєстровано
    // (`Ecr.Infrastructure.DependencyInjection`), і контейнер його передає.

    /// <summary>Право, без якого відкриття періоду неможливе.</summary>
    public const string Permission = "Period.Reopen";

    /// <summary>Відкриває закритий період до вказаного моменту.</summary>
    /// <param name="periodId">Період.</param>
    /// <param name="reason">Причина; обов'язкова.</param>
    /// <param name="until">До якого моменту; <c>null</c> — до кінця доби майданчика.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task HandleAsync(int periodId, string reason, DateTime? until, CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит не може відкривати періоди.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        if (!profile.Has(Permission))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {Permission}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.permission",
                    ["permission"] = Permission,
                });
        }

        // ⛔ `DAT-06`. Блокування періоду, перевірка стану, зміна і запис в
        // аудит — В ОДНІЙ транзакції, за зразком `ReopenDocumentHandler`
        // (аудит 2026-09-16, §6.1). Доти `LockAsync` викликався ПОЗА будь-якою
        // явною транзакцією, і `UPDLOCK` у ньому не давав нічого: поза
        // транзакцією він звільняється щойно завершується сам `SELECT` —
        // задовго до `Reopen` і до `SaveChangesAsync`. Коментар нижче обіцяв
        // взаємовиключення з `PeriodStateJob`, а блокування не трималося.
        //
        // ⛔ Друге, не менше: `IAuditWriter` пише сирим `INSERT` по тому
        // самому підключенню і БЕЗ транзакції комітить одразу
        // (`AuditWriter.CreateCommand`). Тобто запис «період відкрито» лягав
        // у `aud.StructureChange` окремим комітом ПЕРЕД `SaveChangesAsync` —
        // і падіння збереження лишало в журналі доказ події, якої не сталося.
        // Журнал, що розходиться з тим, що він описує, доказом не є.
        //
        // ⚠ `ExecuteInTransactionAsync` приєднується до вже відкритої
        // зовнішньої транзакції (`UnitOfWork.cs:174-178`), тож це обгортка,
        // а не перехоплення чужого коміту.
        var projectId = 0;
        var periodKey = 0;
        var requiresMaterialization = false;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // ⚠ Стратегія повторів може виконати замикання вдруге.
            requiresMaterialization = false;

            // ⚠ Період береться з UPDLOCK і перечитується В ТРАНЗАКЦІЇ: інакше
            // PeriodStateJob може закрити його посеред операції, і відкриття
            // застосується до стану, якого вже немає (ФВ-1.10a).
            // ⛔ Два різні суб'єкти — два різні коди (`P-25`, рядок 4). Обидва
            // рядки писали `ECR-PRD-0422`, у якого цифри кажуть 422, а конвеєр
            // віддає 404, і при цьому не розрізняли, ЩО саме не знайдено. Для
            // адміністратора, який відкриває період, це різниця між «помилився в
            // номері періоду» і «проєкт видалили».
            var period = await periods.LockAsync(periodId, innerCt).ConfigureAwait(false)
                         ?? throw new NotFoundException(
                             ErrorCodes.PeriodNotFound, $"Період {periodId} не знайдено.",
                             new Dictionary<string, object?>
                             {
                                 ["messageKey"] = "err.ECR-PRD-0404.period",
                                 ["periodId"] = periodId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                             });

            var project = await periods.FindProjectAsync(period.ProjectId, innerCt).ConfigureAwait(false)
                          ?? throw new NotFoundException(
                              ErrorCodes.ProjectNotFound, $"Проєкт періоду {periodId} не знайдено.",
                              new Dictionary<string, object?>
                              {
                                  ["messageKey"] = "err.ECR-PRJ-0404.projectOfPeriod",
                                  ["periodId"] = periodId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                              });

            // ⛔ Право `Period.Reopen` — функціональне, воно не каже, ЧИЇ періоди
            // можна відкривати. Без гранта на проєкт власник права відкривав би
            // періоди проєктів, яких навіть не бачить (UX-прохід 2026-09-24,
            // рішення людини). Рівень — Manage, як і в `SetCurrentPeriodHandler`:
            // це структурна зміна проєкту, не правка даних. Перевірка стоїть ДО
            // архівної: інакше відмова «проєкт архівований» розповідала б про
            // чужий проєкт тому, хто його не бачить.
            if (profile.LevelFor(ResourceKind.Project, project.Id) < GrantLevel.Manage)
            {
                throw new AccessDeniedException(
                    "ECR-AUTH-0403", $"Немає гранта Manage на проєкт {project.Id}.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-AUTH-0403.noProjectManageGrant",
                        ["projectId"] = project.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    });
            }

            // Архівований проєкт — кінцевий стан: відкривати в ньому нема чого,
            // дані вже поїхали в архівні партиції (ФВ-1.10).
            if (project.Status == ProjectStatus.Archived)
            {
                throw new BusinessRuleException(
                    "ECR-PRD-0409", $"Проєкт {project.Code} архівований: періоди в ньому не відкриваються.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-PRD-0409.projectArchived",
                        ["projectCode"] = project.Code,
                    });
            }

            var now = clock.UtcNow;
            var deadline = until ?? EndOfSiteDay(now, project.TimeZoneId);

            // Closed → Grace, а не → Open: правки після закриття лишаються
            // ПІЗНІМИ і мають позначатися IsLateEdit (D-70). Відкриття «як було»
            // стерло б різницю між роботою в строк і після нього.
            var before = period.State;
            period.Reopen(deadline, reason, now);

            projectId = period.ProjectId;
            periodKey = period.PeriodKeyValue;
            requiresMaterialization = PeriodMaterializationTrigger.Requires(before, period.State);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    now,
                    TemplateVersionId: project.TemplateVersionId,
                    EntityType: "Period",
                    EntityId: period.Id,
                    ChangeClass: ChangeClass.Guarded,
                    Operation: "Reopen",
                    OldJson: JsonSerializer.Serialize(new { state = nameof(PeriodState.Closed) }),
                    NewJson: JsonSerializer.Serialize(new { state = period.State.ToString(), until = deadline }),
                    ChangeReason: reason,
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // ⛔ Матеріалізація — ПІСЛЯ коміту (черга не транзакційна): точки,
        // пропущені поки період був `Closed` (`SkippedPeriodClosed`), інакше
        // чекали б збору з вікном, що перетинає період, — а для давно минулого
        // періоду такого вікна не буде ніколи.
        if (requiresMaterialization && materialization is not null)
        {
            await materialization.EnqueueAfterTransitionAsync(projectId, [periodKey], ct).ConfigureAwait(false);
        }

        await EnqueueOrphanScanAsync(periodId, ct).ConfigureAwait(false);
    }

    /// <summary>Разовий пошук осиротілих рядків після відкриття — ПІСЛЯ коміту.</summary>
    /// <remarks>
    /// ⛔ Нічний <c>OrphanScanJob</c> обходить лише <c>Open</c>/<c>Grace</c>: поки
    /// період був <c>Closed</c>, ознака <c>IsOrphaned</c> у ньому не оновлювалася,
    /// і щойно відкритий період показує застарілі позначки до ночі (а за
    /// бюджетом курсора — й довше).
    /// <para>
    /// ⚠ Після коміту: черга не транзакційна, і прогін, поставлений до коміту,
    /// бачив би ще закритий період. Збій постановки НЕ валить відповідь —
    /// відкриття вже закомічено й записано в аудит, і 500 на успішну дію
    /// збрехав би людині; нічний прохід однаково дійде. Але й не мовчить — журнал.
    /// </para>
    /// </remarks>
    private async Task EnqueueOrphanScanAsync(int periodId, CancellationToken ct)
    {
        if (jobs is null)
        {
            return;
        }

        try
        {
            await jobs
                .EnqueueExclusiveAsync<IOrphanScanJob>(OrphanScanTarget, payload: null, ct, currentUser.UserId)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (logger is not null)
            {
                LogOrphanScanNotEnqueued(logger, periodId, ex);
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Період {PeriodId} відкрито, але пошук осиротілих рядків НЕ поставлено; позначки оновить нічний прохід.")]
    private static partial void LogOrphanScanNotEnqueued(ILogger logger, int periodId, Exception exception);

    /// <summary>Кінець поточної доби в поясі майданчика, у UTC (D-68).</summary>
    /// <remarks>
    /// «До кінця дня» для людини закінчується опівночі ЇЇ часу. У UTC це вже
    /// інша доба, і вікно правок обірвалося б на кілька годин раніше — рівно
    /// тоді, коли ним користуються.
    /// </remarks>
    private static DateTime EndOfSiteDay(DateTime utcNow, string timeZoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
        var midnight = DateTime.SpecifyKind(local.Date.AddDays(1), DateTimeKind.Unspecified);

        return TimeZoneInfo.ConvertTimeToUtc(midnight, zone);
    }
}
