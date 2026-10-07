using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;

namespace Ecr.Application.Consistency;

/// <summary>
/// Загальні лічильники журналу знахідок за вагою. Право <c>System.ViewHealth</c>.
/// </summary>
/// <remarks>
/// ⛔ Те саме право і та сама перевірка ПЕРЕД читанням, що й у
/// <see cref="GetConsistencyIssuesHandler"/>: лічильники — теж відомості про
/// стан системи, а відмова без права має бути відмовою, а не нулями («знахідок
/// немає» і «тобі не показують» не мають виглядати однаково).
///
/// ⚠ Клієнт не може чесно порахувати їх із курсорної сторінки: сторінка — це
/// частина журналу, а смуга показників на екрані має казати про весь.
/// </remarks>
public sealed class GetConsistencySummaryHandler(
    IConsistencyIssueReader issues, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Повертає лічильники.</summary>
    /// <param name="openOnly">Лише ще не закриті знахідки.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<ConsistencySummary> HandleAsync(bool openOnly, CancellationToken ct)
    {
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, GetConsistencyIssuesHandler.Permission, ct)
            .ConfigureAwait(false);

        return await issues.ReadSummaryAsync(openOnly, ct).ConfigureAwait(false);
    }
}
