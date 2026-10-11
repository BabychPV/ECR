// src/Ecr.Application/Documents/GetCalculationRunStatusHandler.cs
using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;

namespace Ecr.Application.Documents;

/// <summary>
/// Стан актуального прогону документа за період для аркуша: рядки, яким не підійшло жодне правило (D14).
/// </summary>
/// <remarks>
/// ⛔ Це ІНФОРМАЦІЯ, а не помилка: у Land рядок зі статусом «не Running» виключається з розрахунку навмисно.
/// Доти така діагностика жила лише в повідомленні фонової задачі й зникала разом із ним; тепер вона
/// зберігається в профілі прогону й видна на аркуші поки прогін актуальний.
///
/// ⚠ Право те саме, що й у <see cref="GetCalculationResultsHandler"/> (<c>Calculation.View</c> + грант на проєкт).
/// Читач зі звуженою видимістю (схований аркуш/колонка/період) діагностики не отримує — як і свіжість там:
/// ключі рядків схованих аркушів не повинні витікати.
///
/// ⚠ Профіль проєктного прогону містить рядки багатьох документів; відповідь віддає лише рядки ЦЬОГО документа
/// (<see cref="ModuleProfile.ReadUnmatched"/>), а сам профіль не віддається.
/// </remarks>
public sealed class GetCalculationRunStatusHandler(
    ICalculationResultStore results,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Читає стан прогону.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<CalculationRunStatusDto> HandleAsync(long documentId, int periodKey, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, GetCalculationResultsHandler.Permission, ct)
            .ConfigureAwait(false);

        // ⛔ B-08: невидимий документ — 404, як і для результатів.
        await DocumentVisibility
            .RequireVisibleAsync(access, profile, documentId, GetCalculationResultsHandler.Permission, ct)
            .ConfigureAwait(false);

        var run = await results.ReadCurrentRunProfileAsync(documentId, periodKey, ct).ConfigureAwait(false);
        if (run is null)
        {
            return new CalculationRunStatusDto(null, null, null);
        }

        if (DocumentSheetVisibility.HasRestrictions(profile))
        {
            return new CalculationRunStatusDto(run.RunId, run.FinishedAt, null);
        }

        var unmatched = ModuleProfile.ReadUnmatched(run.ModulesProfileJson, documentId);

        return new CalculationRunStatusDto(
            run.RunId,
            run.FinishedAt,
            unmatched is null
                ? null
                : new UnmatchedRowsDto(
                    unmatched.Count,
                    [.. unmatched.Rows.Select(r => new UnmatchedRowItemDto(r.TableInstanceId, r.RowNumber, r.RowKey))]));
    }
}