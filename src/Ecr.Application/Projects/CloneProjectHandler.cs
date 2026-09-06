using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Projects;

/// <summary>
/// Клонує проєкт разом із налаштуваннями на наступний звітний рік.
/// </summary>
/// <remarks>
/// ⛔ **Дані документів не клонуються НІКОЛИ.** Новий проєкт — це новий
/// звітний рік із порожніми формами; перенесені числа перетворилися б на
/// «минулорічні дані, які всі забули оновити», і виявилося б це вже у
/// відправленому звіті.
///
/// Клонуються рівно налаштування: версія шаблону, періодичність, політика
/// періодів і пояс майданчика.
/// </remarks>
public sealed class CloneProjectHandler(
    IPeriodStore periods,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock,
    Security.IAccessDecisionService access)
{
    /// <summary>Створює проєкт-копію з новим кодом.</summary>
    /// <param name="sourceProjectId">Проєкт-джерело.</param>
    /// <param name="newCode">Код нового проєкту.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатор створеного проєкту.</returns>
    public async Task<int> HandleAsync(int sourceProjectId, string newCode, CancellationToken ct)
    {
        // ⛔ Право перевіряється ТУТ (`A7-53`). До цього ендпоінт мав лише
        // `[Authorize]`, тобто оголошене контрактом право не перевіряв ніхто.
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, "Project.Manage", ct)
            .ConfigureAwait(false);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит не може створювати проєкти.");

        // ⛔ `ECR-PRJ-0404`: клонується ПРОЄКТ, і його відсутність не має нічого
        // спільного з «період поза межами проєкту». Старий код до того ж казав
        // цифрами 422 при статусі 404 (`P-25`, рядок 4).
        var source = await periods.FindProjectAsync(sourceProjectId, ct).ConfigureAwait(false)
                     ?? throw new NotFoundException(
                         ErrorCodes.ProjectNotFound, $"Проєкт {sourceProjectId} не знайдено.");

        // Рік зсувається на один: клон робиться заради наступного звітного
        // періоду, і залишити ті самі дати означало б два проєкти з однаковими
        // PeriodKey — тобто конфлікт у партиційному ключі (R-A6).
        var clone = new Project(
            EcrCode.Create(newCode),
            source.NameL10n,
            source.PeriodStart.AddYears(1),
            source.PeriodEnd.AddYears(1),
            source.TemplateVersionId,
            source.PeriodKind,
            source.PeriodPolicyId,
            source.TimeZoneId);

        // ⚠ Періоди НЕ копіюються: їх будує PeriodCalendar за датами нового
        // проєкту. Скопійовані, вони принесли б із собою стани і межі старого
        // року — включно з Closed, який зробив би новий проєкт мертвим.
        await periods.AddProjectAsync(clone, ct).ConfigureAwait(false);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        await audit.WriteStructureChangeAsync(
            new StructureChangeRecord(
                clock.UtcNow,
                TemplateVersionId: clone.TemplateVersionId,
                EntityType: "Project",
                EntityId: clone.Id,
                ChangeClass: ChangeClass.Safe,
                Operation: "Clone",
                OldJson: JsonSerializer.Serialize(new { sourceProjectId, code = source.Code }),
                NewJson: JsonSerializer.Serialize(new { projectId = clone.Id, code = clone.Code }),
                ChangeReason: $"Клон проєкту {source.Code}",
                ChangedByUserId: userId,
                CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return clone.Id;
    }
}
