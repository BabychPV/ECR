// src/Ecr.Application/Documents/GetCalculationIssuesHandler.cs
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Поточні проблеми розрахунку документа за період і копію аркуша: рядки без правила методології (AN-69).
/// </summary>
/// <remarks>
/// ⚠ Право — <c>Document.View</c> (аркуш показує проблему поряд з даними), невидимий документ — той самий 404, що й
/// в інших читань документа. Рядки схованих таблиць (власна чи аркуша заборона) у відповіді ВІДСУТНІ: ключ рядка
/// схованої таблиці не повинен витікати.
/// </remarks>
public sealed class GetCalculationIssuesHandler(
    ICalculationIssueStore issues,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на перегляд документа (`02-contracts.md` §9).</summary>
    public const string Permission = "Document.View";

    /// <summary>Віддає поточні проблеми.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<CalculationIssuesDto> HandleAsync(
        long documentId, int periodKey, long? sheetCopyId, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⛔ B-08: невидимий документ — 404, а не 403.
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, Permission, ct).ConfigureAwait(false);

        var rows = await issues.ListAsync(documentId, periodKey, sheetCopyId, ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return new CalculationIssuesDto([]);
        }

        var key = new PeriodKey(periodKey);
        var readable = key.IsValid
            ? (await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false)).InPeriod(key)
            : await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);

        return new CalculationIssuesDto(
        [
            .. rows
                .Where(r => readable.CanReadTable(r.TableDefId))
                .Select(r => new CalculationIssueDto(
                    r.TableCode, r.SheetCopyId, r.RowNumber, r.RowKey, r.Kind, r.MethodologyCode, r.DetectedAt)),
        ]);
    }
}
