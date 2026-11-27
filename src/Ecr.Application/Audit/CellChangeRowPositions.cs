// src/Ecr.Application/Audit/CellChangeRowPositions.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Audit;

/// <summary>
/// ✎ DOCUX (b19, живий прохід): номер рядка журналу змін комірок на екрані — «Row N», той самий, що «row N» рядка формули
/// (<c>rowDisplayName</c>: індекс у зрізі + 1) і «N (назва)» відмов сервера (<see cref="Documents.RowDisplayNames"/>).
/// </summary>
/// <remarks>
/// ⛔ Доти номером був <c>doc.TableRow.Ordinal</c>, а в рядка, доданого людиною, він 0 — Audit trail і History казали
/// «Row 0 · Type of fuel» про рядок 20 на екрані. Тепер позиція — у ПОРЯДКУ ЗРІЗУ (<c>GetTableSliceHandler</c>): рядки
/// шаблону за <c>RowDef.Ordinal</c>, далі додані людиною за <c>TableRow.Id</c>, серед живих рядків екземпляра.
/// <para>
/// ⚠ Ціна: на сторінку журналу — по одному читанню ключів рядків на РІЗНИЙ екземпляр сторінки (зазвичай один-кілька) плюс
/// знімок метаданих з кешу. Рядка вже немає (видалено, період в архіві без рядків) чи екземпляр не знайдено — номера
/// немає (<c>null</c>), клієнт показує ключ чи «рядок, доданий користувачем», але ніколи «Row 0».
/// </para>
/// </remarks>
public static class CellChangeRowPositions
{
    /// <summary>Позиції (від 1) рядків екземпляра в порядку зрізу: ключ → номер.</summary>
    /// <param name="liveRows">Живі рядки екземпляра: ключ → <c>TableRow.Id</c>.</param>
    /// <param name="templateRows">Рядки шаблону таблиці.</param>
    public static IReadOnlyDictionary<string, int> Of(
        IEnumerable<KeyValuePair<string, long>> liveRows, IEnumerable<RowDef> templateRows)
    {
        ArgumentNullException.ThrowIfNull(liveRows);
        ArgumentNullException.ThrowIfNull(templateRows);

        var defs = templateRows.Where(r => !r.IsDeleted)
            .GroupBy(r => r.RowKeyValue, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Ordinal, StringComparer.Ordinal);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var position = 0;
        foreach (var row in liveRows
                     .OrderBy(r => defs.TryGetValue(r.Key, out var ordinal) ? ordinal : int.MaxValue)
                     .ThenBy(r => r.Value))
        {
            result[row.Key] = ++position;
        }

        return result;
    }

    /// <summary>Сторінка журналу з номерами рядків на екрані; без сховищ — без номерів (а не з неправдивими).</summary>
    public static async Task<IReadOnlyList<CellChangeView>> WithScreenPositionsAsync(
        IReadOnlyList<CellChangeView> items, IRowStore? rows, IMetadataCache? metadata, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (rows is null || metadata is null)
        {
            return items.Any(c => c.RowOrdinal is not null) ? [.. items.Select(c => c with { RowOrdinal = null })] : items;
        }

        var positions = new Dictionary<long, IReadOnlyDictionary<string, int>>();
        foreach (var instanceId in items.Select(c => c.TableInstanceId).OfType<long>().Distinct())
        {
            positions[instanceId] = await PositionsOfAsync(instanceId, rows, metadata, ct).ConfigureAwait(false);
        }

        return
        [
            .. items.Select(c => c with
            {
                RowOrdinal = c.TableInstanceId is { } id
                             && positions.TryGetValue(id, out var map)
                             && map.TryGetValue(c.RowKey, out var n)
                    ? n
                    : null,
            }),
        ];
    }

    private static async Task<IReadOnlyDictionary<string, int>> PositionsOfAsync(
        long tableInstanceId, IRowStore rows, IMetadataCache metadata, CancellationToken ct)
    {
        TableInstanceRef instance;
        try
        {
            instance = await rows.ResolveTableInstanceAsync(tableInstanceId, ct).ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            return new Dictionary<string, int>();
        }

        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
        var table = snapshot.Sheets.SelectMany(s => s.Tables).FirstOrDefault(t => t.Id == instance.TableDefId);
        var live = await rows.GetRowIdsAsync(tableInstanceId, new PeriodKey(instance.PeriodKey), ct).ConfigureAwait(false);

        return Of(live, table?.Rows ?? []);
    }
}
