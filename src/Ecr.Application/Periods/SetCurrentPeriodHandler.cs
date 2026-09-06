// src/Ecr.Application/Periods/SetCurrentPeriodHandler.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Periods;

/// <summary>
/// Поточний період проєкту: `Auto` або `Pinned` (ФВ-1.13, D-77).
/// </summary>
/// <remarks>
/// ⚠ Використовується **лише як значення за замовчуванням** — які період
/// підставити в документ, розклад чи параметр звіту. **На рішення про доступ
/// не впливає ніколи** (ФВ-1.14): інакше `Pinned` став би способом обійти
/// закриття періоду.
/// </remarks>
public sealed class SetCurrentPeriodHandler(
    IPeriodStore periods,
    IUnitOfWork uow,
    IAuditWriter audit,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на налаштування періодів (`02-contracts.md` §9).</summary>
    public const string Permission = "Period.Configure";

    /// <summary>Фіксує поточний період або повертає автоматичний режим.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="pinnedPeriodId">Період; <c>null</c> — режим <c>Auto</c>.</param>
    /// <param name="reason">Причина фіксації; обов'язкова при <c>Pinned</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task HandleAsync(int projectId, int? pinnedPeriodId, string? reason, CancellationToken ct)
    {
        await Templates.ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит не може змінювати поточний період.");

        // ⛔ `ECR-PRJ-0404`: старий `ECR-PRD-0422` обіцяв 422 цифрами і віддавав
        // 404 конвеєром — суперечність усередині одного коду (`P-25`, рядок 4).
        var project = await periods.FindProjectAsync(projectId, ct).ConfigureAwait(false)
                      ?? throw new NotFoundException(
                          ErrorCodes.ProjectNotFound, $"Проєкт {projectId} не знайдено.");

        var now = clock.UtcNow;
        var before = project.CurrentPeriodId;

        if (pinnedPeriodId is { } periodId)
        {
            // Причину вимагає домен; належність періоду проєкту — теж.
            project.PinCurrentPeriod(periodId, reason ?? string.Empty, userId, now);
        }
        else
        {
            project.UnpinCurrentPeriod(userId, now);
        }

        // ⛔ Жодних перевірок доступу на основі цього значення — ані тут, ані
        // деінде (ФВ-1.14). Тому в CellAccessContext поля CurrentPeriod немає
        // за побудовою: забути правило неможливо, бо його нічим виразити.
        await audit.WriteStructureChangeAsync(
            new StructureChangeRecord(
                now,
                TemplateVersionId: project.TemplateVersionId,
                EntityType: "Project.CurrentPeriod",
                EntityId: project.Id,
                ChangeClass: ChangeClass.Presentation,
                Operation: pinnedPeriodId is null ? "Unpin" : "Pin",
                OldJson: JsonSerializer.Serialize(new { currentPeriodId = before }),
                NewJson: JsonSerializer.Serialize(new { currentPeriodId = project.CurrentPeriodId }),
                ChangeReason: reason,
                ChangedByUserId: userId,
                CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
