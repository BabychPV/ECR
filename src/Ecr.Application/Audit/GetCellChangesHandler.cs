using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Audit;

/// <summary>
/// Історія змін комірок. Право <c>Security.ViewAudit</c> — або
/// <c>Document.View</c>, коли запит адресує РІВНО ОДНУ комірку (D15-16).
/// </summary>
/// <remarks>
/// ⛔ НФ-8.9.6 / D-247: порт віддає і гарячий журнал, і архів аудиту (рядок з архіву —
/// <see cref="CellChangeView.FromArchive"/>). Обробник про архів НЕ знає навмисно: право, вікно, грант на
/// документ і межі читання (S6, R-11, D-214) йдуть у <see cref="CellChangeFilter"/> і діють на обидві таблиці
/// однаково — окремого «архівного» шляху з власною, слабшою перевіркою тут немає й бути не повинно.
/// </remarks>
public sealed class GetCellChangesHandler(
    IAuditReader audit,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    TimeProvider? clock = null,
    IMonthColumnRouter? monthRouter = null,
    IMetadataCache? metadata = null,
    IColumnPathMapper? columns = null)
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

        // ⛔ R-11: видимі колонки йдуть у запит — TOP, курсор і nextCursor рахуються вже по видимих.
        // V-4: разом з їхніми Id у версіях шаблону до переносу проєкту.
        IReadOnlySet<int>? visibleSet;
        (filter, visibleSet) = await WithVisibleLineageAsync(filter, readable, ct).ConfigureAwait(false);

        // Q-17: історія комірки, якою володіє інший екземпляр (не-місячна колонка — якір року), читається там,
        // куди PATCH її пише (`RouteForeignOwnersAsync`). Лише звужує: межі читання вище вже застосовані.
        (filter, empty) = await RouteToOwnerPeriodAsync(filter, readable, ct).ConfigureAwait(false);
        if (empty)
        {
            return new PagedResult<CellChangeView>([], null, firstPage ? 0 : null);
        }

        // Q-17 (LAND-YEAR G2): журнал документа за період (перелік і CSV-експорт) — разом зі змінами колонок, що лежать
        // у періоді-власнику. Лише звужує: видимі колонки вище + межа D-214 власника.
        filter = await AddOwnerPeriodColumnsAsync(filter, readable, ct).ConfigureAwait(false);

        var result = await audit.ReadCellChangesAsync(filter, page, ct).ConfigureAwait(false);
        int? total = null;
        if (firstPage)
        {
            var counts = await audit.CountCellChangesByColumnAsync(filter, TodayStart(), ct).ConfigureAwait(false);
            total = (int)Math.Min(
                counts.Where(c => visibleSet is null || visibleSet.Contains(c.ColumnDefId)).Sum(c => c.Total), int.MaxValue);
        }

        // ⚠ Страхувальний другий рубіж: основний відсів уже в SQL (VisibleColumnIds), тож тут нічого не
        // відкидається; фільтр лишено на випадок читача порту, що фільтр не застосував.
        // V-4: той самий набір, що пішов у SQL (видимі + їхній родовід), а не лише колонки поточної версії.
        var shown = visibleSet is null
            ? result.Items
            : [.. result.Items.Where(c => visibleSet.Contains(c.ColumnDefId))];

        return result with { Items = shown, TotalCount = total };
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

        IReadOnlySet<int>? visibleSet;
        (filter, visibleSet) = await WithVisibleLineageAsync(filter, readable, ct).ConfigureAwait(false);

        (filter, empty) = await RouteToOwnerPeriodAsync(filter, readable, ct).ConfigureAwait(false);
        if (empty)
        {
            return new CellChangeSummaryView(0, 0, 0, 0);
        }

        filter = await AddOwnerPeriodColumnsAsync(filter, readable, ct).ConfigureAwait(false);

        var counts = await audit.CountCellChangesByColumnAsync(filter, TodayStart(), ct).ConfigureAwait(false);
        var seen = counts.Where(c => visibleSet is null || visibleSet.Contains(c.ColumnDefId)).ToList();

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
    /// R-11 + V-4: межі читання в запит — видимі колонки поточної версії документа РАЗОМ з Id тих самих колонок у
    /// версіях шаблону до переносу проєкту (<see cref="IColumnPathMapper.LineageAsync"/>), бо <c>aud.CellChange</c>
    /// незмінний і лишається на старих Id.
    /// </summary>
    /// <returns>Фільтр із межами і той самий набір для страхувального відсіву; <c>null</c> — меж немає (наскрізний журнал).</returns>
    /// <remarks>
    /// <para>
    /// ⛔ Родовід розширюється ЛИШЕ від колонок, які читач БАЧИТЬ у поточній версії: колонка під Deny свого
    /// родоводу не дає, тож її правки до переносу так само приховані, як і після. Колонка з тим самим кодом в іншій
    /// таблиці чи аркуші в родовід не входить (шлях «аркуш → таблиця → колонка», а не код).
    /// </para>
    /// <para>
    /// Історія ОДНІЄЇ комірки (інспектор, найчастіший запит): родовід лише її колонки (її видимість уже перевірена в
    /// <see cref="AuthorizeAsync"/>), а не всіх видимих колонок документа; <see cref="CellChangeFilter.ColumnLineage"/>
    /// перетворює рівність <c>ColumnDefId</c> на <c>IN</c> по родоводу.
    /// </para>
    /// </remarks>
    private async Task<(CellChangeFilter Filter, IReadOnlySet<int>? Visible)> WithVisibleLineageAsync(
        CellChangeFilter filter, DocumentReadScope? readable, CancellationToken ct)
    {
        if (readable is null)
        {
            // Наскрізний журнал без документа (Security.ViewAudit, Q-177): меж читання немає, адреси комірки теж.
            return (filter, null);
        }

        if (columns is null)
        {
            var plain = readable.VisibleColumnIds();
            return (filter with { VisibleColumnIds = plain }, plain.ToHashSet());
        }

        if (filter.IsSingleCell)
        {
            var cell = filter.ColumnDefId!.Value;
            var lineage = (await columns.LineageAsync([cell], ct).ConfigureAwait(false)).GetValueOrDefault(cell) ?? [cell];
            return (filter with { VisibleColumnIds = lineage, ColumnLineage = lineage }, lineage.ToHashSet());
        }

        var visible = readable.VisibleColumnIds();
        if (visible.Count == 0)
        {
            return (filter with { VisibleColumnIds = visible }, new HashSet<int>());
        }

        var lineages = await columns.LineageAsync(visible, ct).ConfigureAwait(false);
        IReadOnlyList<int> widened = [.. visible.Concat(lineages.Values.SelectMany(v => v)).Distinct().Order()];
        return (filter with { VisibleColumnIds = widened }, widened.ToHashSet());
    }

    /// <summary>
    /// Q-17: історія ОДНІЄЇ комірки таблиці «місяці в колонках», переглянутої в періоді, що не володіє колонкою, —
    /// з періоду ВЛАСНИКА (введений не-місячний стовпець — якір року; місячний <c>m</c> — період <c>m</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ Дефект 12.11 (doc 96, T3_00, LTD-A01): правку не-місячної колонки з жовтня PATCH пише в екземпляр-якір
    /// (<c>PatchCellsHandler.RouteForeignOwnersAsync</c>), тож <c>aud.CellChange.PeriodKey = 202601</c>, а History
    /// питала 202610 і казала «історії немає». Власника дає ТОЙ САМИЙ <see cref="IMonthColumnRouter.OwnerPeriodAsync"/>,
    /// що й запис: другого правила власності тут немає. Рядок власника — той самий <c>RowKey</c> (журнал зберігає
    /// ключ, а не <c>TableRow.Id</c>), тож змінюється лише період фільтра.
    /// </para>
    /// <para>
    /// ⛔ Лише ЗВУЖУЄ доступ: межі читання логічної адреси (документ, Deny колонки) уже застосовані викликачем, а тут
    /// додається межа періоду власника (D-214), як у зрізі й вивантаженні (<c>OwnerScopeMask</c>): чий період читач не
    /// бачить — порожньо, а не 403 (не виказує, що щось приховано).
    /// </para>
    /// <para>
    /// Маршрутизація вимкнена, не одна комірка, без періоду, таблиця поза Q-17 або власник — сам період перегляду: фільтр
    /// без змін і без звернень до бази.
    /// </para>
    /// </remarks>
    private async Task<(CellChangeFilter Filter, bool Empty)> RouteToOwnerPeriodAsync(
        CellChangeFilter filter, DocumentReadScope? readable, CancellationToken ct)
    {
        if (monthRouter is not { Enabled: true } || metadata is null || !filter.IsSingleCell
            || filter.PeriodKey is not { } viewedKey || filter.DocumentId is not { } documentId
            || filter.ColumnDefId is not { } columnId)
        {
            return (filter, false);
        }

        var viewed = new PeriodKey(viewedKey);
        if (!viewed.IsValid)
        {
            return (filter, false);
        }

        var document = await monthRouter.GetDocumentAsync(documentId, ct).ConfigureAwait(false);
        if (document is null)
        {
            return (filter, false);
        }

        var snapshot = await metadata.GetAsync(document.TemplateVersionId, ct).ConfigureAwait(false);
        if (!snapshot.ColumnsById.TryGetValue(columnId, out var column))
        {
            return (filter, false);
        }

        var table = snapshot.Sheets.SelectMany(s => s.Tables).FirstOrDefault(t => t.Id == column.TableDefId);
        if (table is null || !monthRouter.AppliesToTable(table))
        {
            return (filter, false);
        }

        var owner = await monthRouter.OwnerPeriodAsync(document, table, column, viewed, ct).ConfigureAwait(false);
        if (owner == viewed)
        {
            return (filter, false);
        }

        if (readable is not null && !readable.InPeriod(owner).CanReadColumn(columnId))
        {
            return (filter, true);
        }

        return (filter with { PeriodKey = owner.Value }, false);
    }

    /// <summary>
    /// Q-17 (LAND-YEAR G2): журнал ДОКУМЕНТА за період перегляду (не одна комірка: перелік /audit і CSV-експорт) —
    /// плюс зміни колонок таблиць «місяці в колонках», які PATCH пише в період-ВЛАСНИК.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ Дефект того ж класу, що History комірки (44b9f4ddc): експорт 202610 фільтрував <c>aud.CellChange.PeriodKey =
    /// 202610</c>, а правка не-місячної колонки з жовтня лежить у якорі 202601 — у файлі її не було. Фільтр періоду
    /// стає «202610 АБО (власник, колонка)» тим самим запитом (<see cref="CellChangeFilter.OwnerPeriodColumns"/>).
    /// </para>
    /// <para>
    /// ⛔ Лише ЗВУЖУЄ: колонки — з уже застосованих видимих (<see cref="CellChangeFilter.VisibleColumnIds"/>), плюс межа
    /// D-214 періоду-власника. Подія експорту пише ЗАПИТАНИЙ період: обробник експорту бере його з власного фільтра,
    /// а не з цього, розширеного.
    /// </para>
    /// <para>
    /// V-4: колонки кожної пари — разом з їхнім родоводом версій шаблону (<see cref="IColumnPathMapper.LineageAsync"/>),
    /// звуженим до тих самих видимих; один додатковий запит, лише коли пари є.
    /// </para>
    /// <para>
    /// Одна комірка (її веде <see cref="RouteToOwnerPeriodAsync"/>), без документа, без періоду, маршрутизація вимкнена
    /// або чужих колонок немає — фільтр без змін і без звернень до бази (за вимкненого роутера).
    /// </para>
    /// </remarks>
    private async Task<CellChangeFilter> AddOwnerPeriodColumnsAsync(
        CellChangeFilter filter, DocumentReadScope? readable, CancellationToken ct)
    {
        if (filter.IsSingleCell || readable is null || filter.PeriodKey is not { } viewedKey
            || filter.DocumentId is not { } documentId || filter.VisibleColumnIds is not { } visible)
        {
            return filter;
        }

        var owners = await OwnerPeriodColumns
            .CollectAsync(
                monthRouter, metadata, readable, documentId, new PeriodKey(viewedKey), visible, ct,
                onlyColumnId: filter.ColumnDefId)
            .ConfigureAwait(false);

        if (owners is null)
        {
            return filter;
        }

        // V-4 × Q-17: правки колонки-власника до переносу проєкту лежать у тому самому періоді-власнику, але на Id старої
        // версії шаблону. Пара (власник, колонка) несе й родовід колонки — так само, як `VisibleColumnIds` вище; родовід
        // розширюється лише від колонок, що вже пройшли межі логічної адреси й D-214 власника, і не виходить за видимі.
        if (columns is not null)
        {
            var allowed = visible as IReadOnlySet<int> ?? visible.ToHashSet();
            var lineages = await columns
                .LineageAsync([.. owners.SelectMany(o => o.ColumnDefIds).Distinct()], ct)
                .ConfigureAwait(false);
            owners =
            [
                .. owners.Select(o => o with
                {
                    ColumnDefIds =
                    [
                        .. o.ColumnDefIds
                            .SelectMany(c => lineages.GetValueOrDefault(c) ?? [c])
                            .Where(allowed.Contains)
                            .Distinct()
                            .Order(),
                    ],
                }),
            ];
        }

        return filter with { OwnerPeriodColumns = owners };
    }

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
