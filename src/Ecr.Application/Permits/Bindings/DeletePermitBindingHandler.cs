// src/Ecr.Application/Permits/Bindings/DeletePermitBindingHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;

namespace Ecr.Application.Permits.Bindings;

/// <summary>
/// Прибирає прив'язку таблиці аркуша до дозволу з версії-чернетки (Permits, PM-09).
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Каскаду від прив'язки до колонок у схемі немає (усі FK - Restrict), тому колонки видаляються явно разом із
/// прив'язкою - в одному збереженні, в одній транзакції з аудитом.
/// </para>
/// <para>
/// ⛔ У версії з документами видалення - <c>Breaking</c> (<c>ФВ-7.4</c>): рядки їхніх таблиць створено з дозволу, і
/// прибрати прив'язку означало б залишити їх без джерела. Це відмова, а не попередження.
/// </para>
/// </remarks>
public sealed class DeletePermitBindingHandler(
    IRepository<TemplateVersion, int> versions,
    IPermitBindingStore bindings,
    ITemplateVersionStore store,
    ChangeClassifier classifier,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування прив'язок (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Видаляє прив'язку.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="code">Код прив'язки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії або прив'язки немає.</exception>
    /// <exception cref="ConcurrencyConflictException">На версії є документи (<c>ECR-SCHM-0409</c>).</exception>
    public async Task HandleAsync(int templateVersionId, string code, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                ErrorCodes.Unauthorized,
                "Сесія не містить користувача.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        var version = await versions.FindAsync(templateVersionId, ct).ConfigureAwait(false)
            ?? throw PermitBindingErrors.VersionNotFound(templateVersionId);

        version.EnsureStructurallyMutable();
        version.TouchDraft(userId, clock.UtcNow);

        var binding = await bindings.FindForUpdateAsync(templateVersionId, code, ct).ConfigureAwait(false)
            ?? throw PermitBindingErrors.NotFound(code, templateVersionId);
        var columns = await bindings.ListColumnsForUpdateAsync(binding.Id, ct).ConfigureAwait(false);

        var hasDocuments = await store.HasDocumentsAsync(templateVersionId, ct).ConfigureAwait(false);
        var change = classifier.ClassifyDeletion(nameof(PermitSheetBinding), hasDocuments);
        if (change == Domain.Enums.ChangeClass.Breaking)
        {
            throw PermitBindingErrors.Breaking(binding.Code);
        }

        var oldJson = SavePermitBindingHandler.Describe(binding, columns);

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // ⛔ C5: блок рядка версії першим, «ще чернетка» - під ним.
            await DraftVersionLock.EnsureDraftUnderLockAsync(store, version, innerCt).ConfigureAwait(false);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow, templateVersionId, nameof(PermitSheetBinding), binding.Id,
                    change, "Delete", oldJson, NewJson: null, ChangeReason: null,
                    ChangedByUserId: userId, CorrelationId: null),
                innerCt).ConfigureAwait(false);

            bindings.RemoveColumns(columns);
            bindings.Remove(binding);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);
    }
}
