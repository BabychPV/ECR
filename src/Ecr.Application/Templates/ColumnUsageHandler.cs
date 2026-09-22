// src/Ecr.Application/Templates/ColumnUsageHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;

namespace Ecr.Application.Templates;

/// <summary>«Де використовується» колонка шаблону (ФВ-8.14).</summary>
/// <remarks>
/// Право — <c>Template.View</c>, як у пошуку колонок: перелік нічого не змінює,
/// а питання «що зламається, якщо я її чіпну» ставить і той, хто лише дивиться.
/// </remarks>
public sealed class ColumnUsageHandler(
    IWhereUsedStore store,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Перші <see cref="UsageResponse.PageSize"/> посилань і загальна кількість.</summary>
    /// <param name="columnDefId">Колонка.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Колонки немає або її видалено — <c>ECR-TMPL-0404</c>.</exception>
    public async Task<UsageResponse> HandleAsync(int columnDefId, CancellationToken ct)
    {
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, SearchColumnDefsHandler.Permission, ct)
            .ConfigureAwait(false);

        if (!await store.ColumnExistsAsync(columnDefId, ct).ConfigureAwait(false))
        {
            throw new NotFoundException(
                "ECR-TMPL-0404",
                $"Колонки {columnDefId} не існує або її видалено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0404.column",
                    ["columnDefId"] = columnDefId.ToString(CultureInfo.InvariantCulture),
                });
        }

        return await store.FindColumnUsageAsync(columnDefId, UsageResponse.PageSize, ct).ConfigureAwait(false);
    }
}
