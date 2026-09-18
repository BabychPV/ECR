// src/Ecr.Application/Periods/ReopenPeriodHandler.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Periods;

/// <summary>
/// Адміністративне відкриття закритого періоду (ФВ-1.10). Право
/// <c>Period.Reopen</c> — небезпечне, у складені ролі не входить (ФВ-6.12).
/// </summary>
public sealed class ReopenPeriodHandler(
    IPeriodStore periods,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
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
                         "ECR-AUTH-0401", "Анонімний запит не може відкривати періоди.");

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        if (!profile.Has(Permission))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {Permission}.",
                new Dictionary<string, object?> { ["permission"] = Permission });
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
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
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
                             ErrorCodes.PeriodNotFound, $"Період {periodId} не знайдено.");

            var project = await periods.FindProjectAsync(period.ProjectId, innerCt).ConfigureAwait(false)
                          ?? throw new NotFoundException(
                              ErrorCodes.ProjectNotFound, $"Проєкт періоду {periodId} не знайдено.");

            // Архівований проєкт — кінцевий стан: відкривати в ньому нема чого,
            // дані вже поїхали в архівні партиції (ФВ-1.10).
            if (project.Status == ProjectStatus.Archived)
            {
                throw new BusinessRuleException(
                    "ECR-PRD-0409", $"Проєкт {project.Code} архівований: періоди в ньому не відкриваються.");
            }

            var now = clock.UtcNow;
            var deadline = until ?? EndOfSiteDay(now, project.TimeZoneId);

            // Closed → Grace, а не → Open: правки після закриття лишаються
            // ПІЗНІМИ і мають позначатися IsLateEdit (D-70). Відкриття «як було»
            // стерло б різницю між роботою в строк і після нього.
            period.Reopen(deadline, reason, now);

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
    }

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
