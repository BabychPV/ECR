using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Errors;

namespace Ecr.Application.Audit;

/// <summary>
/// Історія змін комірок. Право <c>Security.ViewAudit</c> — або
/// <c>Document.View</c>, коли запит адресує РІВНО ОДНУ комірку (D15-16).
/// </summary>
public sealed class GetCellChangesHandler(
    IAuditReader audit, IAccessDecisionService access, ICurrentUser currentUser, TimeProvider? clock = null)
{
    /// <summary>Право, без якого ЗАГАЛЬНИЙ журнал не віддається.</summary>
    public const string Permission = "Security.ViewAudit";

    /// <summary>
    /// Право, якого достатньо для історії ОДНІЄЇ комірки (D15-16).
    /// </summary>
    /// <remarks>
    /// ⛔ Два рівні доступу в одній дії — рішення, а не недогляд.
    /// <c>Security.ViewAudit</c> — централізоване комплаєнс-право (Q-177):
    /// воно дає наскрізний журнал по ВСІХ проєктах, і роздавати його кожному
    /// операторові заради вкладки History в інспекторі комірки означало б
    /// віддати разом із нею весь журнал системи. Натомість питання «що було з
    /// ЦИМ числом» — це питання про документ, який людина й так бачить, тож
    /// поріг для нього — <c>Document.View</c> плюс той самий грант на проєкт,
    /// що й для читання зрізу.
    ///
    /// ⚠ Межа проходить за <see cref="CellChangeFilter.IsSingleCell"/>, тобто
    /// за ПОВНОЮ адресою комірки. Послабити її до «є documentId» означало б
    /// віддати власникові <c>Document.View</c> увесь журнал документа — це вже
    /// не історія комірки, а журнал аудиту з іншим порогом.
    /// </remarks>
    public const string CellHistoryPermission = "Document.View";

    /// <summary>
    /// Максимальна ширина вікна.
    /// </summary>
    /// <remarks>
    /// ⚠ Обмеження згори, а не лише знизу. Вікно «за десять років» формально
    /// задане, але читає всі партиції — і одна така кнопка в UI кладе базу
    /// в найгірший момент. 92 дні — квартал із запасом: більше за реальний
    /// запит аудитора, менше за одну партицію-рік.
    /// </remarks>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(92);

    /// <summary>
    /// Максимальна ширина вікна для історії ОДНІЄЇ комірки — 13 місяців.
    /// </summary>
    /// <remarks>
    /// ⚠ Ширше за <see cref="MaxWindow"/> НАВМИСНО і без суперечності з нею.
    /// Стеля в 92 дні захищає від запиту «весь журнал за рік», який читає
    /// мільйони рядків; запит за однією коміркою обмежений не вікном, а
    /// адресою: комірку правлять одиниці разів на рік. 13 місяців — рівно те,
    /// що питає людина («а торік у цьому місяці?»), 92 дні на це не вистачає.
    ///
    /// ⚠ 396 днів, а не «13 × 30»: місяці різної довжини, і рік плюс місяць у
    /// найдовшому розкладі — 396 діб.
    /// </remarks>
    public static readonly TimeSpan MaxCellWindow = TimeSpan.FromDays(396);

    /// <summary>Максимальна довжина пошукового рядка <c>q</c> (UI-38, C3).</summary>
    public const int MaxQueryLength = 100;

    /// <summary>Повертає сторінку змін.</summary>
    /// <param name="filter">Вікно й звуження журналу.</param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ <c>TotalCount</c> (UI-38, C4) — кількість змін, які читач ВИДИТЬ: у вікні, за фільтром і за межами
    /// читання (S6, R-11). Сума по прихованих колонках, таблицях і аркушах у число не входить так само, як
    /// їхні рядки не входять у сторінку: інакше різниця між загальним числом і довжиною видимого була б
    /// оракулом «там щось приховано».
    ///
    /// ⚠ Лише на ПЕРШІЙ сторінці (<c>Cursor == null</c>): на наступних <c>TotalCount</c> — <c>null</c>, агрегат
    /// по вікну не рахується повторно. Число не змінюється між сторінками, тож клієнт бере його з першої.
    /// </remarks>
    public async Task<PagedResult<CellChangeView>> HandleAsync(
        CellChangeFilter filter, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        filter = Normalize(filter);

        // UI-38 (P3): сума по вікну лічиться ЛИШЕ на першій сторінці (курсора немає); далі `TotalCount` — `null`,
        // а порт підрахунку не викликається: кожна наступна сторінка не повторює агрегат по всьому вікну.
        var firstPage = page.Cursor is null;
        var (readable, empty) = await AuthorizeAsync(filter, page, ct).ConfigureAwait(false);
        if (empty)
        {
            return new PagedResult<CellChangeView>([], null, firstPage ? 0 : null);
        }

        var result = await audit.ReadCellChangesAsync(filter, page, ct).ConfigureAwait(false);
        int? total = null;
        if (firstPage)
        {
            var counts = await audit.CountCellChangesByColumnAsync(filter, TodayStart(), ct).ConfigureAwait(false);
            total = (int)Math.Min(
                counts.Where(c => readable is null || readable.CanReadColumn(c.ColumnDefId)).Sum(c => c.Total), int.MaxValue);
        }

        // ⚠ Журнал документа без колонки (лише `Security.ViewAudit`): рядки заборонених колонок відкидаються
        // ПІСЛЯ читання сторінки, тож сторінка може бути коротшою за ліміт. Курсор лишається правильним: він
        // іде за журналом, не за відфільтрованим переліком.
        var visible = readable is null
            ? result.Items
            : [.. result.Items.Where(c => readable.CanReadColumn(c.ColumnDefId))];

        return result with { Items = visible, TotalCount = total };
    }

    /// <summary>Підсумок журналу за вікном (UI-38, C2): ті самі права, вікно й видимість, що й у <see cref="HandleAsync"/>.</summary>
    /// <param name="filter">Вікно й звуження журналу.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Рахується лише видиме читачу (S6, R-11): лічильники приходять із порту в розрізі колонки, і суми
    /// з прихованих колонок, таблиць і аркушів у відповідь не потрапляють.
    /// </remarks>
    public async Task<CellChangeSummaryView> SummaryAsync(CellChangeFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        filter = Normalize(filter);
        var (readable, empty) = await AuthorizeAsync(filter, page: null, ct).ConfigureAwait(false);
        if (empty)
        {
            return new CellChangeSummaryView(0, 0, 0, 0);
        }

        var counts = await audit.CountCellChangesByColumnAsync(filter, TodayStart(), ct).ConfigureAwait(false);
        var seen = counts.Where(c => readable is null || readable.CanReadColumn(c.ColumnDefId)).ToList();

        return new CellChangeSummaryView(
            seen.Sum(c => c.Total), seen.Sum(c => c.Today), seen.Sum(c => c.ByImport), seen.Sum(c => c.ByRecalculation));
    }

    /// <summary>
    /// Пошуковий рядок: обрізані пробіли, порожній — це не фільтр (очищене поле), довший за межу — обрізається.
    /// </summary>
    /// <remarks>
    /// ⚠ Обрізання, а не відмова: нового коду помилки заради поля пошуку не заводимо, а 100 знаків із запасом
    /// перекривають бізнес-ключ (до 64), ключ рядка (до 100) і код колонки.
    /// </remarks>
    private static CellChangeFilter Normalize(CellChangeFilter filter)
    {
        var query = filter.Query?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return filter.Query is null ? filter : filter with { Query = null };
        }

        return query.Length > MaxQueryLength ? filter with { Query = query[..MaxQueryLength] } : filter with { Query = query };
    }

    private DateTime TodayStart() => (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.Date;

    /// <summary>
    /// Право, вікно й доступ до документа; повертає межі читання документа (<c>null</c> — наскрізний
    /// журнал без <c>documentId</c>) і ознаку «порожньо» (колонка адреси прихована).
    /// </summary>
    private async Task<(DocumentReadScope? Readable, bool Empty)> AuthorizeAsync(
        CellChangeFilter filter, CursorRequest? page, CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        // ⛔ ДВА РІВНІ ДОСТУПУ в одній дії (D15-16). Без цієї гілки вкладка
        // History в інспекторі комірки була б порожньою для всіх, крім
        // аудиторів: `Security.ViewAudit` має мізерна частка ролей.
        var single = filter.IsSingleCell;
        if (!PermissionCheck.IsGranted(profile, Permission) && !(single && profile.HasInAnyProject(CellHistoryPermission)))
        {
            // ⚠ Називається право, якого бракує САМЕ ДЛЯ ЦЬОГО запиту: сказати
            // власникові `Document.View` «потрібне Security.ViewAudit» на
            // запиті історії комірки означало б відправити адміністратора
            // видавати комплаєнс-право заради вкладки в інспекторі.
            var required = single ? CellHistoryPermission : Permission;

            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {required}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.permission",
                    ["permission"] = required,
                });
        }

        // ⛔ `RowKey` унікальний у межах екземпляра таблиці, а не системи:
        // «R1» є в кожному документі. Без `documentId` такий запит зібрав би
        // рядки з чужих документів і виглядав би як відповідь — тому це
        // відмова, а не мовчазне ігнорування фільтра.
        if (filter.IsCellAddressWithoutDocument)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                "Адреса комірки (rowKey/columnDefId) без documentId не є адресою.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.auditCellNeedsDocument" });
        }

        var from = filter.From;
        var to = filter.To;

        // ⛔ Родина REQ, а не CELL: суб'єкт відмови — ПАРАМЕТР ЗАПИТУ, а не
        // комірка документа. З `ECR-CELL-0422` відмова журналу аудиту
        // приходила клієнтові в обробник помилок сітки, якої на цьому екрані
        // немає взагалі (`P-25`, рядок 1).
        if (to <= from)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid, "Кінець вікна аудиту має бути пізнішим за початок.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.auditWindowOrder" });
        }

        var maxWindow = single ? MaxCellWindow : MaxWindow;
        if (to - from > maxWindow)
        {
            var maxDays = maxWindow.TotalDays.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Вікно аудиту ширше за {maxDays} днів: запит пішов би по всіх партиціях.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.auditWindowTooWide",
                    ["maxDays"] = maxDays,
                });
        }

        if (page is { IsValid: false })
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid, $"Розмір сторінки поза межами 1..{CursorRequest.MaxLimit}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.pageSizeOutOfRange",
                    ["max"] = CursorRequest.MaxLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        // ⛔ Грант на проєкт (Q-177, аудит фази 2) — лише коли `documentId`
        // ЗАДАНО. Без нього запит іде по ВСІХ документах/проєктах — і це
        // НАВМИСНО, підтверджено рішенням людини: `Security.ViewAudit` —
        // централізоване/комплаєнс-право поза межами проєктів, не «бачить
        // лише свої проєкти». Звузити цей випадок до гранта означало б
        // зламати саме призначення права. Доведено сценарієм
        // `DataEntryScenarios.Журнал_аудиту_без_documentId_наскрізний_за_призначенням`:
        // `stranger` без ЖОДНОГО гранта, лише з `Security.ViewAudit`,
        // отримує `200` на запит без `documentId`.
        //
        // Тут — вужчий і однозначний випадок: КОНКРЕТНИЙ `documentId` мусить
        // належати проєкту, на який запитувач має грант, так само як для
        // читання самого документа.
        //
        // ⚠ Для історії ОДНІЄЇ комірки ця сама перевірка — не додаткова, а
        // ЄДИНА ресурсна: право `Document.View` каже «ця людина взагалі
        // працює з документами», грант каже — з якими.
        if (filter.DocumentId is { } id)
        {
            var read = await access.CanReadDocumentAsync(profile, id, ct).ConfigureAwait(false);
            if (!read.IsAllowed)
            {
                throw new AccessDeniedException(
                    "ECR-AUTH-0403", $"Немає доступу до документа {id}: {read.Reason}.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-AUTH-0403.noDocumentAccess",
                        ["documentId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["reason"] = read.Reason.ToString(),
                    });
            }

            // ⛔ ФВ-6.14: історія комірки за `Document.View` (а не за глобальним
            // `Security.ViewAudit`) — лише в проєкті, де це право є.
            //
            // ⚠ ПОРЯДОК: спершу право (немає — 403, журнал не читається
            // взагалі), лише ПОТІМ межі читання S6. Навпаки — і запит без права
            // на прихованій колонці отримав би «порожньо» замість 403, тобто
            // відповідь залежала б від заборони, а не від права.
            if (!PermissionCheck.IsGranted(profile, Permission)
                && await access.DocumentProjectIdAsync(id, ct).ConfigureAwait(false) is { } projectId
                && !PermissionCheck.IsGrantedIn(profile, CellHistoryPermission, projectId))
            {
                throw new AccessDeniedException(
                    "ECR-AUTH-0403", $"Потрібне право {CellHistoryPermission}.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-AUTH-0403.permission",
                        ["permission"] = CellHistoryPermission,
                    });
            }

            // ⛔ S6 (ФВ-6.6): історія комірки віддає СТАРІ й НОВІ значення —
            // ті самі числа, що й зріз, тож і межа та сама. Колонка під
            // забороною (своєю, таблиці чи аркуша) — порожня історія, як у
            // комірки, яку ніхто не правив: 404 чи 403 тут сказали б, що
            // колонка існує й щось приховано.
            var readable = await access.ReadScopeAsync(profile, id, ct).ConfigureAwait(false);

            return filter.ColumnDefId is { } column && !readable.CanReadColumn(column)
                ? (null, true)
                : (readable, false);
        }

        // ⚠ Без `documentId` — наскрізний журнал для `Security.ViewAudit` поза
        // межами проєктів (рішення людини, Q-177); межі читання S6 тут не
        // застосовуються так само, як не застосовується грант на проєкт.
        return (null, false);
    }
}
