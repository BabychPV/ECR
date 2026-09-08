using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Validation;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Останній збережений результат перевірки документа за період (ФВ-5.19).
/// </summary>
/// <remarks>
/// ⛔ Читання додане `W8` (директива №09 п.3, `S-19`). Підсумок ЗБЕРІГАВСЯ
/// давно — <c>ValidateDocumentHandler</c> кладе його в <c>wf.ValidationResult</c>
/// саме тому, що подання не має перевалідовувати документ, — але прочитати
/// його не міг ніхто: <c>IValidationResultStore.GetLatestAsync</c> не мав
/// жодного виклику у всій системі.
///
/// ⚠ Ціна відсутності видна на екрані: перелік зауважень жив рівно до
/// перезавантаження сторінки, і щоб побачити його знову, оператор мусив
/// ЗАПУСТИТИ перевірку заново — на великому документі це три секунди й
/// повний перерахунок правил заради списку, який уже було пораховано.
/// </remarks>
public sealed class GetValidationResultHandler(
    IValidationResultStore results,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на перегляд документа (`02-contracts.md` §9).</summary>
    public const string Permission = "Document.View";

    /// <summary>
    /// Останній результат; <c>null</c> — перевірку за цей період ще не
    /// запускали.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<ValidationMessage>?> HandleAsync(
        long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        // ⛔ Ті самі дві перевірки, що й у самої валідації (`A7-53`, `ФВ-6.13`):
        // список зауважень несе підписи рядків і колонок, тобто ЗМІСТ
        // документа. Читати збережений результат має право рівно той, хто мав
        // би право його порахувати.
        var profile = await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var read = await access.CanReadDocumentAsync(profile, documentId, ct).ConfigureAwait(false);
        if (!read.IsAllowed)
        {
            throw new Errors.AccessDeniedException(
                "ECR-AUTH-0403", $"Немає доступу до документа {documentId}: {read.Reason}.");
        }

        var summary = await results
            .GetLatestAsync(documentId, periodKey.Value, ct).ConfigureAwait(false);

        if (summary is null)
        {
            return null;
        }

        // ⚠ «Перевірку не запускали» і «перевірка не знайшла зауважень» —
        // РІЗНІ відповіді, і другу не можна показувати замість першої: зелений
        // напис «зауважень немає» під документом, який ніхто не перевіряв, —
        // це та сама неправда, що й порожній дашборд замість збою (`A7-04`).
        return JsonSerializer.Deserialize<List<ValidationMessage>>(summary.MessagesJson) ?? [];
    }
}
