// src/Ecr.Application/Documents/CalculationYear/GetCalculationYearHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Security;

namespace Ecr.Application.Documents.CalculationYear;

/// <summary>
/// <c>GET /api/v1/documents/{id}/calculation-results/year?year=YYYY</c> — усі розраховані числа документа за рік,
/// місяці поруч (CALC-RESULTS.md).
/// </summary>
/// <remarks>
/// ⚠ Право те саме, що в <see cref="GetCalculationResultsHandler"/> (<c>Calculation.View</c> + грант на проєкт;
/// невидимий документ — 404), нового права немає.
/// </remarks>
public sealed class GetCalculationYearHandler(
    CalculationYearViewBuilder builder,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право — те саме, що в <see cref="GetCalculationResultsHandler.Permission"/> (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Найменший і найбільший рік запиту — за межами це помилка адреси, а не «порожній рік».</summary>
    public const int MinYear = 2000, MaxYear = 2100;

    /// <summary>Будує огляд року.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="year">Рік.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<CalculationYearViewDto> HandleAsync(long documentId, int year, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);
        await DocumentVisibility
            .RequireVisibleAsync(access, profile, documentId, Permission, ct)
            .ConfigureAwait(false);
        RequireYear(year);

        return await builder.BuildAsync(profile, documentId, year, ct).ConfigureAwait(false);
    }

    /// <summary>Рік у межах <see cref="MinYear"/>…<see cref="MaxYear"/>; інакше <c>ECR-REQ-0422</c>.</summary>
    public static void RequireYear(int year)
    {
        if (year is < MinYear or > MaxYear)
        {
            throw new Errors.BusinessRuleException(
                Ecr.Domain.Errors.ErrorCodes.RequestInvalid,
                $"Рік {year} поза межами {MinYear}…{MaxYear}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.documentYearOutOfRange",
                    ["min"] = MinYear.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["max"] = MaxYear.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }
    }
}
