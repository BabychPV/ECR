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
        var rows = cells
            .GroupBy(c => c.Address.TableRowId)
            .Select(g => new RowDto(
                RowKey: keyById.TryGetValue(g.Key, out var k) ? k : g.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Ordinal: 0,
                RowKind: table.RowMode.ToString(),
                Label: null,
                RowVersion: keyById.TryGetValue(g.Key, out var vk) && versions.TryGetValue(vk, out var v) ? v : string.Empty,
                Cells: g.ToDictionary(
                    c => columnCodeById.TryGetValue(c.Address.ColumnDefId, out var code) ? code : c.Address.ColumnDefId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    c => Unwrap(c.Value)),
                IsOrphaned: orphans.TryGetValue(g.Key, out var orph) && orph))
            .ToList();

        // 5. Компактна мапа заборон: grid має одразу знати, що сіре і чому,
        //    без другого запиту.
        var permissions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (address, decision) in decisions)
        {
            if (decision.IsAllowed)
            {
                continue;
            }
            if (!keyById.TryGetValue(address.TableRowId, out var rowKey))
            {
                continue;
            }
            if (!columnCodeById.TryGetValue(address.ColumnDefId, out var code))
            {
                continue;
            }
            permissions[$"{rowKey}:{code}"] = decision.Reason.ToString();
        }

        return new TableSliceDto(tableInstanceId, instance.PeriodKey, columns, rows, permissions);
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
