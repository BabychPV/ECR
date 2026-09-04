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
    IAccessDecisionService access)
{
    /// <summary>Читає зріз.</summary>
    public async Task<TableSliceDto> HandleAsync(long documentId, long tableInstanceId,
                                                 AccessProfile profile, string language, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

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

        var columns = table.Columns
            .Where(c => !c.IsDeleted)
            .OrderBy(c => c.Ordinal)
            .Select(c => new ColumnDto(
                c.Id, c.Code, c.HeaderL10n.Get(language) ?? c.Code, c.DataType.ToString(),
                c.Ordinal, c.IsReadOnly, c.IsRequired, c.DisplayFormat, c.DefaultValue,
                c.LookupRegistryDefId, c.UnitId, UnitSymbol: null))
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
