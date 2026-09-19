using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Calculations;
using Microsoft.Extensions.Caching.Memory;

namespace Ecr.Application.Documents;

/// <summary>
/// Зріз таблиці для grid. **Найважчий регулярний запит системи**: бюджет
/// p95 1.5 с на 500×60, з яких 600 мс — вибірка з SQL (tz/08 §8.2).
/// </summary>
/// <param name="rowStore">Рядки екземпляра таблиці.</param>
/// <param name="cellStore">Значення комірок.</param>
/// <param name="metadata">Знімок структури версії шаблону.</param>
/// <param name="units">Довідник одиниць.</param>
/// <param name="access">Рішення доступу.</param>
/// <param name="methodologies">Конфігурація методологій.</param>
/// <param name="periods">Періоди документа.</param>
/// <param name="styles">Каталог стилів версії.</param>
/// <param name="memory">
/// Сховище кешу <c>RD-04</c>. У контейнері розв'язується завжди
/// (<c>Ecr.Infrastructure/DependencyInjection.cs:96</c>); значення за
/// замовчуванням існує лише заради тестів, що конструюють обробник вручну, і
/// вимикає кеш, а не підміняє його тихою заглушкою.
/// </param>
public sealed class GetTableSliceHandler(
    IRowStore rowStore,
    ICellStore cellStore,
    IMetadataCache metadata,
    IUnitCatalog units,
    IAccessDecisionService access,
    IMethodologyStore methodologies,
    IPeriodStore periods,
    IStyleCatalog styles,
    IMemoryCache? memory = null)
{
    private readonly MethodologyRequiredColumnsCache _required = new(memory);

    /// <summary>
    /// Кеш позначки «обов'язкове за методологією».
    /// </summary>
    /// <remarks>
    /// ⚠ Відкритий заради <c>IsEnabled</c>: тест мусить мати змогу довести, що
    /// в зібраному контейнері кеш справді ввімкнений, а не мовчки вимкнувся
    /// разом із прискоренням, заради якого існує.
    /// </remarks>
    public MethodologyRequiredColumnsCache RequiredColumnsCache => _required;

    /// <summary>Читає зріз.</summary>
    public async Task<TableSliceDto> HandleAsync(long documentId, long tableInstanceId,
                                                 AccessProfile profile, string language, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // ⛔ Право перевіряється ТУТ (`A7-53`). Контролер будував профіль і
        // передавав його далі, не питаючи нічого: `[Authorize]` пропускав
        // будь-кого, хто увійшов.
        if (!profile.Has("Document.View"))
        {
            throw new Errors.AccessDeniedException(
                "ECR-AUTH-0403", "Потрібне право Document.View.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.permission",
                    ["permission"] = "Document.View",
                });
        }

        // ⛔ І ГРАНТ на проєкт (`A7-55`, `ФВ-6.13`). Функціональне право
        // каже «цей користувач узагалі працює з документами»; грант каже, з
        // ЯКИМИ. Без другої перевірки ресурсна модель — включно з `IsDeny`
        // (`ФВ-6.6`) — не діяла на читанні зовсім: `CanReadDocumentAsync`
        // існувала і не мала жодного виклику.
        var read = await access.CanReadDocumentAsync(profile, documentId, ct).ConfigureAwait(false);
        if (!read.IsAllowed)
        {
            throw new Errors.AccessDeniedException(
                "ECR-AUTH-0403", $"Немає доступу до документа {documentId}: {read.Reason}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.noDocumentAccess",
                    ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["reason"] = read.Reason.ToString(),
                });
        }

        var instance = await rowStore.ResolveTableInstanceAsync(tableInstanceId, ct).ConfigureAwait(false);

        // ⛔ Належність екземпляра таблиці документові з МАРШРУТУ (Q-171,
        // аудит фази 2). Без цієї перевірки шлях у URL декоративний:
        // `CanReadDocumentAsync` вище перевіряє право на СВІЙ `documentId`,
        // а `tableInstanceId` читається БЕЗ звірки з ним — клієнт міг би
        // вказати чужий екземпляр і прочитати чужі дані, маючи право лише
        // на свій документ. `CreateRowHandler` і `CellsController.Patch`
        // цю звірку роблять; на читанні її не було ніколи.
        if (instance.DocumentId != documentId)
        {
            throw new Errors.NotFoundException(
                "ECR-DOC-0404",
                $"Екземпляр таблиці {tableInstanceId} не належить документу {documentId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.tableInstanceNotInDocument",
                    ["tableInstanceId"] = tableInstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        // 1. Метадані — зі знімка, без звернення до БД (D-16).
        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
        var table = snapshot.Sheets.SelectMany(sh => sh.Tables)
                        .FirstOrDefault(t => t.Id == instance.TableDefId)
                    ?? throw new Errors.NotFoundException(
                        "ECR-TMPL-0404", $"Таблиці {instance.TableDefId} немає в структурі версії.");

        // 2. Значення — ОДИН запит на весь зріз. N+1 тут коштує бюджету
        //    1.5 с на 500×60 (tz/08 §8.2).
        var cells = await cellStore.ReadSliceAsync(tableInstanceId, ct).ConfigureAwait(false);

        // 3. Права — ОДИН виклик на весь зріз, не по комірці.
        var decisions = await access.CanEditSliceAsync(profile, tableInstanceId, ct).ConfigureAwait(false);

        var rowIds = await rowStore.GetRowIdsAsync(tableInstanceId, new Domain.ValueObjects.PeriodKey(instance.PeriodKey), ct)
                                   .ConfigureAwait(false);
        var versions = await rowStore.GetRowVersionsAsync(tableInstanceId, new Domain.ValueObjects.PeriodKey(instance.PeriodKey), ct)
                                     .ConfigureAwait(false);
        var keyById = rowIds.ToDictionary(kv => kv.Value, kv => kv.Key);
        var orphans = await rowStore.GetOrphanFlagsAsync(tableInstanceId, new Domain.ValueObjects.PeriodKey(instance.PeriodKey), ct)
                                    .ConfigureAwait(false);

        // ⛔ Позначення одиниці РОЗВ'ЯЗУЄТЬСЯ, а не лишається порожнім.
        // Тут стояло `UnitSymbol: null` — і поле, оголошене в контракті,
        // ніколи не несло значення. Наслідок видно на кожному екрані:
        // оператор бачить «12» і не знає, кілограми це чи тонни, а вся
        // система побудована навколо того, що в кожного числа є одиниця
        // (`ФВ-16.1`). Тонни під виглядом кілограмів — помилка в тисячу
        // разів, і помічає її регулятор.
        //
        // ⚠ Довідник читається ОДИН раз на зріз і кешується сховищем:
        // запит на колонку зробив би шістдесят походів у базу там, де
        // бюджет усього зрізу — 1.5 с.
        var catalogue = await units.GetAsync(ct).ConfigureAwait(false);
        var symbolById = catalogue.Units.Values.ToDictionary(u => u.Id, u => u.Code);

        // ⛔ Директива «правила методологій на сторінці»: оператор мав
        // дізнаватися про обов'язковий вхід методології лише ПІСЛЯ спроби
        // зберегти (`PatchCellsHandler.EnforceRequiredInputsAsync`) — жодного
        // сигналу на сітці ДО того не було. Тут той самий факт (яка версія
        // методології зараз чинна для цієї таблиці й що вона вимагає)
        // рахується для позначки в заголовку, а не для gate.
        var requiredByMethodology = await RequiredByMethodologyColumnIdsAsync(
            table.Id, documentId, instance.PeriodKey, ct).ConfigureAwait(false);

        // ⛔ Директива registry-lookup / cell-style, PR B2: жива сітка досі не
        // показувала оформлення, задане автором шаблону (`ColumnDef.StyleId`)
        // — стиль долітав лише до Excel-експорту (`StyleMapper.cs`). Один
        // запит на ВЕСЬ знімок стилів версії (`IStyleCatalog.GetAsync`, уже
        // такий самий за формою, що й `units.GetAsync` вище), не по колонці:
        // шістдесят колонок не повинні коштувати шістдесяти походів у базу.
        var styleById = await styles.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);

        var columns = table.Columns
            .Where(c => !c.IsDeleted)
            .OrderBy(c => c.Ordinal)
            .Select(c => new ColumnDto(
                c.Id, c.Code, c.HeaderL10n.Get(language) ?? c.Code, c.DataType.ToString(),
                c.Ordinal, c.IsReadOnly, c.IsRequired, c.DisplayFormat, c.DefaultValue,
                c.LookupRegistryDefId, c.UnitId, SymbolOf(symbolById, c.UnitId),
                c.Precision, c.Scale, requiredByMethodology.Contains(c.Id), StyleOf(styleById, c.StyleId)))
            .ToList();

        var columnCodeById = table.Columns.ToDictionary(c => c.Id, c => c.Code);

        // 4. Незаповнені комірки в зрізі не існують узагалі — вони не
        //    матеріалізуються (ФВ-3.8), і клієнт бере DefaultValue колонки.
        //    ⚠ Явна порожнеча — інший стан: вона матеріалізована і мусить
        //    дійти до клієнта, інакше «свідомо порожньо» перетвориться на
        //    «не заповнювали» і підставиться дефолт (R-B4). Передається як
        //    присутній ключ зі значенням null.
        //
        // ⛔ Перелік рядків будується з САМИХ РЯДКІВ, а не з комірок
        // (директива №09 `W8` п.2, `S-13`). Групування по `cells` означало, що
        // рядок без жодної комірки в зрізі не існує — тобто щойно створений
        // документ віддавав ПОРОЖНЮ фіксовану таблицю, у яку нема куди
        // вводити перше число. Порожнеча була не станом даних, а наслідком
        // способу побудови відповіді.
        var cellsByRow = cells
            .GroupBy(c => c.Address.TableRowId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Підписи й порядок — з опису рядка шаблону (`RowDef`). Раніше тут
        // стояли `Ordinal: 0` і `Label: null` на КОЖНОМУ рядку: поля контракту
        // існували й не несли нічого, а фіксована таблиця приходила на екран
        // без назв рядків — тобто без того єдиного, за чим оператор упізнає,
        // куди він пише.
        var rowDefs = table.Rows
            .Where(r => !r.IsDeleted)
            .ToDictionary(r => r.RowKeyValue, StringComparer.Ordinal);

        var rows = rowIds
            .Select(pair => new
            {
                RowKey = pair.Key,
                RowId = pair.Value,
                Def = rowDefs.GetValueOrDefault(pair.Key),
            })
            .OrderBy(r => r.Def?.Ordinal ?? int.MaxValue)
            .ThenBy(r => r.RowId)
            .Select(r => new RowDto(
                RowKey: r.RowKey,
                Ordinal: r.Def?.Ordinal ?? 0,
                RowKind: r.Def is null ? table.RowMode.ToString() : r.Def.RowKind.ToString(),
                Label: r.Def?.LabelL10n.Get(language),
                RowVersion: versions.GetValueOrDefault(r.RowKey) ?? string.Empty,
                Cells: (cellsByRow.GetValueOrDefault(r.RowId) ?? [])
                    .ToDictionary(
                        c => columnCodeById.TryGetValue(c.Address.ColumnDefId, out var code) ? code : c.Address.ColumnDefId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        c => Unwrap(c.Value)),
                IsOrphaned: orphans.TryGetValue(r.RowId, out var orph) && orph))
            .ToList();

        // 5. Компактні мапи: grid має одразу знати, що сіре і чому (заборони),
        //    і що дозволене лише з підтвердженням (`ФВ-2.16`, `#43`), — без
        //    другого запиту в обох випадках.
        var permissions = new Dictionary<string, string>(StringComparer.Ordinal);
        var confirmations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (address, decision) in decisions)
        {
            if (!keyById.TryGetValue(address.TableRowId, out var rowKey))
            {
                continue;
            }
            if (!columnCodeById.TryGetValue(address.ColumnDefId, out var code))
            {
                continue;
            }

            var key = $"{rowKey}:{code}";

            if (!decision.IsAllowed)
            {
                permissions[key] = decision.Reason.ToString();
                continue;
            }

            // ⛔ До цього поля `RequiresConfirmation` ніхто не читав: рішення
            // рахувалося (`AccessDecisionService.Decide`), а сюди, у
            // відповідь клієнту, не потрапляло НІЧОГО — `AllowWithConfirmation`
            // і звичайний дозвіл були на виході з обробника нерозрізненні (`#43`).
            if (decision.RequiresConfirmation)
            {
                confirmations[key] = decision.Detail ?? string.Empty;
            }
        }

        return new TableSliceDto(
            tableInstanceId, instance.PeriodKey, columns, rows, permissions, confirmations);
    }

    /// <summary>
    /// <c>ColumnDefId</c> обов'язкових вхідних колонок усіх методологій,
    /// чинних ЗАРАЗ (на кінець періоду екземпляра) для цієї таблиці.
    /// </summary>
    /// <remarks>
    /// ⚠ Вибір чинної версії — те саме правило, що й
    /// <c>PatchCellsHandler.ResolveApplicableAsync</c> (найпізніший
    /// <c>EffectiveFrom</c> ≤ дата, потім старша версія, потім більший
    /// <c>Id</c> — <see cref="MethodologyVersionKey.Currency"/>), навмисно
    /// НЕ винесене в спільний метод: там вибір — частина ворожіння правил
    /// прив'язки по КОНКРЕТНОМУ рядку (gate перед записом), тут — підсумок
    /// по ВСІЙ таблиці для заголовка (жодного рядка ще може не бути).
    /// Спільний із обома предикат зіставлення рядка — <c>MethodologyRuleMatcher</c>
    /// — тут не потрібен: зірочка в заголовку не знає про рядки взагалі
    /// (див. <see cref="ColumnDto.IsRequiredByMethodology"/>).
    ///
    /// ⚠ Немає меж періоду (`FindPeriodBoundsAsync` повернув <c>null</c>) —
    /// порожній набір, а не відмова: сітку показати треба навіть тоді, коли
    /// не вдалося визначити межі періоду, просто без позначки методології.
    ///
    /// ⛔ <b>`RD-04`.</b> Тут було <b>три запити на кожну прив'язану
    /// методологію</b> — <c>3N</c> звернень у найгарячішому читанні системи.
    /// Тепер добір чинної версії і вміст версії йдуть через
    /// <see cref="MethodologyRequiredColumnsCache"/>, і в теплому стані число
    /// звернень <b>не залежить від <c>N</c></b>: лишається рівно одне —
    /// склад прив'язок, який кешувати не можна (він і є ключем кешу).
    /// </remarks>
    private async Task<HashSet<int>> RequiredByMethodologyColumnIdsAsync(
        int tableDefId, long documentId, int periodKey, CancellationToken ct)
    {
        // ⚠ Цей запит лишається на кожен зріз навмисно: увімкнення чи зняття
        // прив'язки (`cfg.CalculationBinding`) мусить бути видно негайно, і
        // саме склад прив'язок входить у ключ кешу нижче.
        var methodologyIds = await methodologies
            .GetMethodologyIdsBoundToTableAsync(tableDefId, ct).ConfigureAwait(false);

        if (methodologyIds.Count == 0)
        {
            return [];
        }

        var bounds = await periods.FindPeriodBoundsAsync(documentId, periodKey, ct).ConfigureAwait(false);
        if (bounds is null)
        {
            return [];
        }

        var versionIds = await _required.CurrentVersionIdsAsync(
            tableDefId, bounds.PeriodEnd, methodologyIds,
            token => CurrentVersionIdsAsync(methodologyIds, bounds.PeriodEnd, token), ct)
            .ConfigureAwait(false);

        var result = new HashSet<int>();

        foreach (var versionId in versionIds)
        {
            var columns = await _required.RequiredColumnIdsAsync(
                versionId, token => RequiredColumnIdsOfVersionAsync(versionId, token), ct)
                .ConfigureAwait(false);

            result.UnionWith(columns);
        }

        return result;
    }

    /// <summary>
    /// Версії методологій, чинні на <paramref name="periodEnd"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ Кешується <b>окремо</b> від вмісту версії і з коротким терміном
    /// життя: відповідь тут змінює публікація нової версії
    /// (<c>MethodologyVersion.Publish</c>) і виведення з обігу
    /// (<c>MethodologyVersion.Deprecate</c>), не змінивши жодного байта в уже
    /// опублікованій. Плутати ці дві гарантії — значить кешувати назавжди те,
    /// що назавжди кешувати не можна.
    /// </remarks>
    private async Task<IReadOnlyList<int>> CurrentVersionIdsAsync(
        IReadOnlyList<int> methodologyIds, DateOnly periodEnd, CancellationToken ct)
    {
        var ids = new List<int>(methodologyIds.Count);

        foreach (var methodologyId in methodologyIds)
        {
            var versions = await methodologies.GetPublishedVersionsAsync(methodologyId, ct).ConfigureAwait(false);

            var version = versions
                .Where(v => v.EffectiveFrom is not null && v.EffectiveFrom <= periodEnd)
                .OrderByDescending(
                    v => new MethodologyVersionKey(v.EffectiveFrom, v.Version, v.Id),
                    MethodologyVersionKey.Currency)
                .FirstOrDefault();

            if (version is not null)
            {
                ids.Add(version.Id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Обов'язкові вхідні колонки однієї <b>опублікованої</b> версії.
    /// </summary>
    /// <remarks>
    /// ⚠ Версія без ЖОДНОГО правила прив'язки ніколи не потрапляє в gate
    /// <c>PatchCellsHandler.ResolveApplicableAsync</c> (<c>rules.Count == 0</c>
    /// → методологія пропускається цілком, незалежно від вимог) — тобто її
    /// обов'язкові входи НІКОЛИ насправді не enforced. Позначати тут колонку
    /// зірочкою за вимогою, яка ніколи не спрацює, означало б брехати
    /// операторові. Той самий предикат «застосовність», без самого зіставлення
    /// рядка (воно тут не потрібне).
    ///
    /// ⛔ Порожній набір через «немає правил» кешується так само, як
    /// непорожній, і це навмисно: це теж незмінна властивість опублікованої
    /// версії, а не тимчасова відсутність відповіді.
    /// </remarks>
    private async Task<IReadOnlySet<int>> RequiredColumnIdsOfVersionAsync(
        int methodologyVersionId, CancellationToken ct)
    {
        var rules = await methodologies.GetRulesAsync(methodologyVersionId, ct).ConfigureAwait(false);
        if (rules.Count == 0)
        {
            return new HashSet<int>();
        }

        var requiredInputs = await methodologies
            .GetRequiredInputsAsync(methodologyVersionId, ct).ConfigureAwait(false);

        return requiredInputs.Select(i => i.ColumnDefId).ToHashSet();
    }

    /// <summary>Позначення одиниці колонки; <c>null</c> — колонка безрозмірна.</summary>
    /// <remarks>
    /// ⚠ Невідомий у довіднику ідентифікатор дає <c>null</c>, а не порожній
    /// рядок: «одиниці немає» і «одиниця є, але ми її не знайшли» — різні
    /// стани, і другий має бути видно як відсутність підпису, а не як
    /// безрозмірну величину.
    /// </remarks>
    private static string? SymbolOf(Dictionary<int, string> symbols, int? unitId)
        => unitId is { } id && symbols.TryGetValue(id, out var code) ? code : null;

    /// <summary>
    /// Оформлення колонки для клієнта (директива registry-lookup /
    /// cell-style, PR B2); <c>null</c> — колонка без стилю, або стиль
    /// вилучили (посилання лишилось, запису вже нема — той самий клас
    /// «м'якого» неспівпадіння, що й <see cref="SymbolOf"/> для одиниці).
    /// </summary>
    private static CellStyleDto? StyleOf(
        IReadOnlyDictionary<int, Domain.Entities.Configuration.StyleDef> styles, int? styleId)
    {
        if (styleId is not { } id || !styles.TryGetValue(id, out var style))
        {
            return null;
        }

        return new CellStyleDto(
            style.IsBold, style.IsItalic, style.ForegroundArgb, style.BackgroundArgb,
            style.HorizontalAlign, style.VerticalAlign, style.WrapText);
    }

    /// <summary>
    /// Розгортає типізоване значення в те, що піде клієнтові.
    /// </summary>
    /// <remarks>
    /// ⛔ Розгортання ОДНЕ на всі шляхи —
    /// <c>CellValueMapping.ToRuleValue</c> (аудит 2026-09-16, §3.2). Копія цієї
    /// логіки жила тут, а друга, коротша й зламана, — у
    /// <c>TableValidation.SliceContext</c>; розійшлися вони саме на Bool/Date, і
    /// розбіжність була видима лише як «"Перевірити" каже одне, подання —
    /// інше».
    /// </remarks>
    private static object? Unwrap(Domain.ValueObjects.CellValueData v)
        => Ecr.Expressions.Evaluation.CellValueMapping.ToRuleValue(v);
}
