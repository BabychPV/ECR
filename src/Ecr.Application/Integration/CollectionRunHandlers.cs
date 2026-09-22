using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Errors;

namespace Ecr.Application.Integration;

/// <summary>
/// Журнал прогонів збору (ФВ-5.23). Право <c>Integration.View</c> або <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// Та сама межа читання, що в переліку з'єднань (<see cref="ListDataSourcesHandler"/>):
/// хто запускає збір, мусить бачити, чим він закінчився.
/// </remarks>
public sealed class ListCollectionRunsHandler(
    ICollectionRunReader runs, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Стеля сторінки журналу — як у журналі доставок.</summary>
    public const int MaxLimit = 200;

    /// <summary>
    /// Стани, які пишуть <c>CollectionRun</c> (<c>Running</c>) і <c>CollectionRunner</c>
    /// (<c>Succeeded</c>/<c>Degraded</c>/<c>Failed</c>).
    /// </summary>
    public static readonly string[] KnownStates = ["Running", "Succeeded", "Degraded", "Failed"];

    /// <summary>Права, що відкривають журнал; перше називається у відмові.</summary>
    private static readonly string[] Permissions = [ListDataSourcesHandler.Permission, SaveDataSourceHandler.Permission];

    /// <summary>Сторінка прогонів, новіші першими.</summary>
    /// <exception cref="BusinessRuleException">Невідомий стан, <c>from</c> ≥ <c>to</c>, розмір поза межами.</exception>
    public async Task<PagedResult<CollectionRunView>> HandleAsync(
        CollectionRunFilter filter, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        await PermissionCheck.RequireAnyAsync(access, currentUser, Permissions, ct).ConfigureAwait(false);

        if (page.Limit is < 1 or > MaxLimit)
        {
            var max = MaxLimit.ToString(CultureInfo.InvariantCulture);
            throw Invalid("err.ECR-REQ-0422.pageSizeOutOfRange", $"Розмір сторінки поза межами 1..{max}.", "max", max);
        }

        // ⛔ Невідомий стан — 422, а не порожній перелік: друкарська помилка у
        // фільтрі читалася б як «збоїв не було».
        var state = string.IsNullOrWhiteSpace(filter.Status)
            ? null
            : KnownStates.FirstOrDefault(s => string.Equals(s, filter.Status.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? throw Invalid(
                  "err.ECR-REQ-0422.collectionRunState", $"Стану прогону «{filter.Status}» не існує.", "state", filter.Status);

        if (filter.FromUtc is { } from && filter.ToUtc is { } to && from >= to)
        {
            throw Invalid("err.ECR-REQ-0422.collectionRunRange", "Початок проміжку має бути раніше за кінець.", "from", from);
        }

        return await runs.ListAsync(filter with { Status = state }, page, ct).ConfigureAwait(false);
    }

    private static BusinessRuleException Invalid(string messageKey, string message, string field, object? value)
        => new(
            ErrorCodes.RequestInvalid,
            message,
            new Dictionary<string, object?> { ["messageKey"] = messageKey, [field] = value });
}

/// <summary>Деталь прогону збору. Право — як у журналу.</summary>
public sealed class GetCollectionRunHandler(
    ICollectionRunReader runs, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Прогін із текстом помилки й покриттям.</summary>
    /// <exception cref="NotFoundException">Прогону немає.</exception>
    public async Task<CollectionRunDetail> HandleAsync(long id, CancellationToken ct)
    {
        await PermissionCheck.RequireAnyAsync(
            access, currentUser, [ListDataSourcesHandler.Permission, SaveDataSourceHandler.Permission], ct)
            .ConfigureAwait(false);

        return await runs.FindAsync(id, ct).ConfigureAwait(false)
               ?? throw new NotFoundException(
                   ErrorCodes.SourceEntityNotFound,
                   $"Прогону збору {id} не існує.",
                   new Dictionary<string, object?>
                   {
                       ["messageKey"] = "err.ECR-INT-0404.collectionRun",
                       ["id"] = id.ToString(CultureInfo.InvariantCulture),
                   });
    }
}
