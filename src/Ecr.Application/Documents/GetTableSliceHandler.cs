using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;

namespace Ecr.Application.Documents;

/// <summary>
/// Зріз таблиці для grid. **Найважчий регулярний запит системи**: бюджет
/// p95 1.5 с на 500×60, з яких 600 мс — вибірка з SQL (tz/08 §8.2).
/// </summary>
public sealed class GetTableSliceHandler(
    IRowStore rowStore,
    ICellStore cellStore,
    IMetadataCache metadata,
    IUnitCatalog units,
    IAccessDecisionService access)
{
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
                "ECR-AUTH-0403", "Потрібне право Document.View.");
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
                "ECR-AUTH-0403", $"Немає доступу до документа {documentId}: {read.Reason}.");
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
                $"Екземпляр таблиці {tableInstanceId} не належить документу {documentId}.");
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

        var columns = table.Columns
            .Where(c => !c.IsDeleted)
            .OrderBy(c => c.Ordinal)
            .Select(c => new ColumnDto(
                c.Id, c.Code, c.HeaderL10n.Get(language) ?? c.Code, c.DataType.ToString(),
                c.Ordinal, c.IsReadOnly, c.IsRequired, c.DisplayFormat, c.DefaultValue,
                c.LookupRegistryDefId, c.UnitId, SymbolOf(symbolById, c.UnitId),
                c.Precision, c.Scale))
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
    /// Розгортає типізоване значення в те, що піде клієнтові.
    /// </summary>
    /// <remarks>
    /// Порядок перевірок відповідає порядку полів у <c>CellValueData</c> і не
    /// має значення: заповнене поле рівно одне (R-B4).
    /// </remarks>
    private static object? Unwrap(Domain.ValueObjects.CellValueData v)
    {
        if (v.ValueNumeric is { } n)
        {
            return n;
        }
        if (v.ValueString is { } s)
        {
            return s;
        }
        if (v.ValueBool is { } b)
        {
            return b;
        }
        if (v.ValueDate is { } d)
        {
            return d;
        }
        if (v.ValueRegistryEntryId is { } r)
        {
            return r;
        }
        if (v.ValueUnitId is { } u)
        {
            return u;
        }
        return null;
    }
}
