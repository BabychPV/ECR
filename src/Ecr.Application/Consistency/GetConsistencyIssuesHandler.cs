using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Errors;

namespace Ecr.Application.Consistency;

/// <summary>
/// Журнал знахідок нічної перевірки узгодженості. Право <c>System.ViewHealth</c>.
/// </summary>
/// <remarks>
/// ⛔ Знахідки писалися в <c>aud.ConsistencyIssue</c> з першого дня
/// <c>ConsistencyCheckJob</c>, і з продукту їх не читав НІХТО: ні ендпоінт, ні
/// екран. Видимою була лише кількість — лічильник
/// <c>ecr.consistency.issues</c> із міткою <c>kind</c>
/// (<c>EcrMetrics</c>/<c>ConsistencyMetricsAdapter</c>). Тобто адміністратор
/// дізнавався «є 12 знахідок <c>BROKEN_FK</c>» і не мав жодного способу
/// дізнатися, ЯКІ САМЕ рядки зачеплені, окрім запиту до бази руками.
///
/// ⚠ Право — <c>System.ViewHealth</c>, те саме, що й <c>GET /api/v1/jobs</c> і
/// перевірки здоров'я, а не <c>Security.ViewAudit</c>. Рішення (судження, не
/// факт замовника): таблиця лежить у схемі <c>aud</c>, але відповідає на інше
/// питання. Аудит відповідає «хто змінив це число» — це комплаєнс-право
/// аудитора; тут же — деталізація того самого сигналу, що вже видно на
/// <c>/admin/health</c> і в прогоні обслуговування <c>consistency-check</c>.
/// Нового права не заводжу: придатне вже є, а зайве право в каталозі — це ще
/// один рядок, який хтось мусить комусь видати, щоб робоче місце запрацювало.
/// </remarks>
public sealed class GetConsistencyIssuesHandler(
    IConsistencyIssueReader issues, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право, без якого журнал не віддається.</summary>
    public const string Permission = "System.ViewHealth";

    /// <summary>Повертає сторінку знахідок.</summary>
    /// <param name="ruleCode">Фільтр за кодом правила; <c>null</c> — усі.</param>
    /// <param name="openOnly">Лише ще не закриті знахідки.</param>
    /// <param name="severity">Вага 1..3; <c>null</c> — будь-яка. Інше — <c>422</c>.</param>
    /// <param name="query">Пошуковий рядок (текст, код правила, номер сутності); порожній — без пошуку.</param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<ConsistencyIssuePage> HandleAsync(
        string? ruleCode, bool openOnly, int? severity, string? query, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        // ⛔ Перевірка права ПЕРЕД будь-яким читанням: відмова має бути
        // відмовою, а не порожнім переліком. Порожній перелік без права
        // означав би, що «знахідок немає» і «тобі не показують» виглядають
        // однаково — а це найгірша з відповідей про стан даних.
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⛔ Родина REQ, а не якась доменна: суб'єкт відмови — ПАРАМЕТР
        // ЗАПИТУ, а не сутність предметної області (той самий вибір, що в
        // `GetCellChangesHandler`, `P-25` рядок 1).
        if (!page.IsValid)
        {
            // ⚠ `messageKey` + сира підстановка окремим полем, а не готове
            // українське речення: мови продукту — `en`/`ru`/`kz`, і `Detail`
            // біля локалізованого `Title` інакше приїхав би українською
            // (`Q-341`). Речення лишається запасним — на випадок, коли ключа
            // в каталозі мови ще немає.
            var max = CursorRequest.MaxLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);

            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Розмір сторінки поза межами 1..{max}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.pageSizeOutOfRange",
                    ["max"] = max,
                });
        }

        // ⚠ Порожній/пробільний код правила — це «фільтра немає», а не
        // «шукати правило з порожнім кодом»: поле фільтра на екрані, яке
        // щойно очистили, надсилає саме порожній рядок.
        var rule = string.IsNullOrWhiteSpace(ruleCode) ? null : ruleCode.Trim();

        // ⚠ Невідома вага — відмова, а не мовчазне «усі»: порожній екран на
        // друкарську помилку читався б як «таких знахідок немає» (той самий
        // вибір, що в `jobState`).
        if (severity is < MinSeverity or > MaxSeverity)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Немає ваги знахідки {severity}: допустимо 1..3.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.consistencySeverity",
                    ["severity"] = severity.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        // ⚠ Пошуковий рядок обрізається до межі, а не відхиляється: поле
        // пошуку не має падати від вставленого довгого тексту. Порожній —
        // «пошуку немає».
        var text = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        if (text is { Length: > MaxQueryLength })
        {
            text = text[..MaxQueryLength];
        }

        return await issues
            .ReadIssuesAsync(rule, openOnly, (byte?)severity, text, page, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Найменша вага знахідки (інформація).</summary>
    public const int MinSeverity = 1;

    /// <summary>Найбільша вага знахідки (помилка).</summary>
    public const int MaxSeverity = 3;

    /// <summary>Межа довжини пошукового рядка.</summary>
    public const int MaxQueryLength = 100;
}
