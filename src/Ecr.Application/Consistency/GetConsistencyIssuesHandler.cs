using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

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
    IConsistencyIssueReader issues, IAccessDecisionService access, ICurrentUser currentUser, IMetadataCache metadata)
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

        var result = await issues
            .ReadIssuesAsync(rule, openOnly, (byte?)severity, text, page, ct)
            .ConfigureAwait(false);

        return await WithVisibleWhereAsync(result, ct).ConfigureAwait(false);
    }

    /// <summary>Типи сутностей, місце яких можна безпечно вивести (див. <c>ResolveLocationsAsync</c>).</summary>
    private static readonly string[] LocatableTypes = ["doc.CellValue", "cfg.ColumnDef"];

    /// <summary>Право на структуру шаблону — для знахідок рівня <c>cfg.ColumnDef</c>.</summary>
    public const string TemplateViewPermission = "Template.View";

    /// <summary>Проєктне право перегляду документа — для знахідок рівня документа.</summary>
    public const string DocumentViewPermission = "Document.View";

    /// <summary>
    /// Додає <c>where</c> лише тим знахідкам, місце яких читач бачить.
    /// </summary>
    /// <remarks>
    /// ⛔ Журнал системний (<c>System.ViewHealth</c>), але <c>where</c> розкриває СТРУКТУРУ документів
    /// (код документа, аркуша, таблиці, ключ рядка). Тому: документ — лише коли профіль бачить його
    /// проєкт (<see cref="AccessProfile.SeesDocumentsOf"/>), має <c>Document.View</c> у ньому й бачить
    /// аркуш і таблицю в періоді рядка (<see cref="DocumentReadScope"/>, D-214, S6); структура шаблону
    /// (<c>cfg.ColumnDef</c>) — лише з <c>Template.View</c>. Інакше <c>where = null</c>, а сама
    /// знахідка лишається такою, якою була. Розклад — ОДИН пакетний запит на сторінку, знімки
    /// структури — з кешу метаданих, по одному на версію шаблону.
    /// </remarks>
    private async Task<ConsistencyIssuePage> WithVisibleWhereAsync(ConsistencyIssuePage result, CancellationToken ct)
    {
        var keys = result.Items
            .Where(i => i.EntityId is not null && i.EntityType is not null && LocatableTypes.Contains(i.EntityType))
            .Select(i => (EntityType: i.EntityType!, EntityId: i.EntityId!.Value))
            .Distinct()
            .ToList();

        if (keys.Count == 0 || currentUser.UserId is not { } userId)
        {
            return result;
        }

        var locations = await issues.ResolveLocationsAsync(keys, ct).ConfigureAwait(false);
        if (locations.Count == 0)
        {
            return result;
        }

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        var scopes = new Dictionary<(int Project, int Version, int Period), DocumentReadScope>();
        var snapshots = new Dictionary<int, TemplateVersionSnapshot>();
        var templateReader = PermissionCheck.IsGranted(profile, TemplateViewPermission);
        var items = new List<ConsistencyIssueView>(result.Items.Count);

        foreach (var item in result.Items)
        {
            if (item.EntityType is null
                || item.EntityId is not { } id
                || !locations.TryGetValue((item.EntityType, id), out var location))
            {
                items.Add(item);
                continue;
            }

            bool visible;
            if (location.ProjectId is not { } projectId || location.PeriodKey is not { } period)
            {
                visible = templateReader;
            }
            else if (!profile.SeesDocumentsOf(projectId)
                     || !PermissionCheck.IsGrantedIn(profile, DocumentViewPermission, projectId))
            {
                visible = false;
            }
            else
            {
                if (!scopes.TryGetValue((projectId, location.TemplateVersionId, period), out var scope))
                {
                    if (!snapshots.TryGetValue(location.TemplateVersionId, out var snapshot))
                    {
                        snapshot = await metadata.GetAsync(location.TemplateVersionId, ct).ConfigureAwait(false);
                        snapshots[location.TemplateVersionId] = snapshot;
                    }

                    scope = DocumentReadScope.For(profile, projectId, snapshot).InPeriod(new PeriodKey(period));
                    scopes[(projectId, location.TemplateVersionId, period)] = scope;
                }

                visible = scope.CanReadSheet(location.SheetDefId)
                          && scope.CanReadAt(location.TableDefId, location.Where.ColumnCode);
            }

            items.Add(visible ? item with { Where = location.Where } : item);
        }

        return result with { Items = items };
    }

    /// <summary>Найменша вага знахідки (інформація).</summary>
    public const int MinSeverity = 1;

    /// <summary>Найбільша вага знахідки (помилка).</summary>
    public const int MaxSeverity = 3;

    /// <summary>Межа довжини пошукового рядка.</summary>
    public const int MaxQueryLength = 100;
}
