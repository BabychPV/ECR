// src/Ecr.Application/Templates/SetTemplateLayoutHandler.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;

namespace Ecr.Application.Templates;

/// <summary>Розкладка версії шаблону у відповіді <c>PUT …/layout</c>.</summary>
/// <param name="TemplateVersionId">Версія.</param>
/// <param name="Layout">Чинна розкладка.</param>
public sealed record TemplateLayoutDto(int TemplateVersionId, TemplateLayout Layout);

/// <summary>
/// D-474: задає розкладку версії-чернетки — <see cref="TemplateLayout.PerMonthCopies"/> (копія документа на
/// кожен місяць) або <see cref="TemplateLayout.YearWithMonthColumns"/> (один документ на рік, місяці — колонками).
/// </summary>
/// <remarks>
/// ⛔ Розкладка визначає, якому періоду належить кожна комірка, тож міняється лише в чернетці й лише доки
/// у версії немає документів; в опублікованій — тільки новою версією. Після коміту оновлюється реєстр
/// розкладок (<see cref="ITemplateLayoutRegistry"/>): маршрутизація читає його, а не базу.
/// </remarks>
public sealed class SetTemplateLayoutHandler(
    ITemplateVersionStore store,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser,
    ITemplateLayoutRegistry? layouts = null)
{
    /// <summary>Право на редагування структури версії.</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Задає розкладку.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="layout">Нова розкладка.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Чинна розкладка версії.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="ConcurrencyConflictException">У версії вже є документи (<c>ECR-SCHM-0409</c>).</exception>
    public async Task<TemplateLayoutDto> HandleAsync(int templateVersionId, TemplateLayout layout, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                ErrorCodes.Unauthorized,
                "Сесія не містить користувача.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        if (!Enum.IsDefined(layout))
        {
            throw new BusinessRuleException(
                ErrorCodes.TemplateInvalid,
                "Невідома розкладка версії шаблону.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.templateLayoutUnknown",
                    ["versionId"] = templateVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);
        version.EnsureStructurallyMutable();

        var old = version.Layout;
        if (old == layout)
        {
            return new TemplateLayoutDto(templateVersionId, old);
        }

        if (await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false))
        {
            throw new ConcurrencyConflictException(
                ErrorCodes.BreakingChange,
                "Розкладку не можна змінити: у версії вже є документи. Клонуйте версію.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SCHM-0409.templateLayoutBreaking",
                    ["versionId"] = templateVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        version.TouchDraft(userId, clock.UtcNow);

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await DraftVersionLock.EnsureDraftUnderLockAsync(store, version, innerCt).ConfigureAwait(false);

            version.SetLayout(layout);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow, templateVersionId, nameof(TemplateVersion), templateVersionId,
                    ChangeClass.Safe, "Update",
                    Describe(old), Describe(layout), ChangeReason: null,
                    ChangedByUserId: userId, CorrelationId: null),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);

        // ⚠ Цей процес бачить нову розкладку одразу; інші (Worker) — за наступним оновленням реєстру.
        if (layouts is not null)
        {
            await layouts.RefreshAsync(ct).ConfigureAwait(false);
        }

        return new TemplateLayoutDto(templateVersionId, layout);
    }

    private static string Describe(TemplateLayout layout)
        => System.Text.Json.JsonSerializer.Serialize(new { Layout = layout.ToString() });
}
