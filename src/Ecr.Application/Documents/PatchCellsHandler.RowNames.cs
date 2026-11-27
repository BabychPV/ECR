using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

public sealed partial class PatchCellsHandler
{
    /// <summary>
    /// ✎ integ-row-label-messages: відмова форми запиту (дубль рядка чи комірки) ДО читання контексту — називає рядок
    /// номером і підписом (як на екрані), а не службовим ключем. Читання, права й видимість — ті самі, що в
    /// <c>LoadContextAsync</c> (нерозкриття: номери рядків недоступної таблиці не віддаються); один батчевий запит рядків.
    /// Рядка немає (новий, ще не збережений) — відмова без назв, текст каже «(added by a user)».
    /// </summary>
    private async Task<BusinessRuleException> NameRowsOfRequestRefusalAsync(
        BusinessRuleException refusal, PatchCellsRequest request, TableInstanceRef? resolvedInstance, CancellationToken ct)
    {
        if (RowKeyText.OpaqueKeysIn(refusal.Details) is not { Count: > 0 } keys)
        {
            return refusal;
        }

        var userId = ResolveUserId();
        var instance = resolvedInstance
                       ?? await rowStore.ResolveTableInstanceAsync(request.TableInstanceId, ct).ConfigureAwait(false);
        var profile = await EnsureDocumentReadableAsync(userId, instance.DocumentId, ct).ConfigureAwait(false);
        await DocumentVisibility
            .RequireTableVisibleAsync(
                access, profile, instance.DocumentId, instance.TableInstanceId, instance.TableDefId, instance.PeriodKey, ct)
            .ConfigureAwait(false);

        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
        var live = await rowStore.GetRowIdsAsync(instance.TableInstanceId, new PeriodKey(instance.PeriodKey), ct).ConfigureAwait(false);
        var names = RowDisplayNames.Of(
            live,
            snapshot.RowsByKey.Where(pair => pair.Key.TableDefId == instance.TableDefId).Select(pair => pair.Value),
            keys,
            currentUser.Language);

        return RowDisplayNames.WithNames(refusal, names);
    }

    /// <summary>Та сама назва рядка для відмови, що народилася вже з контекстом (нуль запитів: рядки вже прочитано).</summary>
    internal static BusinessRuleException NameRows(
        BusinessRuleException refusal, IReadOnlyDictionary<string, long> rowIds, IEnumerable<RowDef> tableRows, string? language)
    {
        if (RowKeyText.OpaqueKeysIn(refusal.Details) is not { Count: > 0 } keys)
        {
            return refusal;
        }

        return RowDisplayNames.WithNames(refusal, RowDisplayNames.Of(rowIds, tableRows, keys, language));
    }
}
