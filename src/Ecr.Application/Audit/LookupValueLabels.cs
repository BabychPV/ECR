// src/Ecr.Application/Audit/LookupValueLabels.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Audit;

/// <summary>
/// ✎ DOCUX (огляд UI 27.11): журнал змін комірок (Audit trail → Cell changes, вкладка History інспектора) показував
/// довідникову комірку сирим id запису («— → 37» для Type of fuel = Diesel). Тепер — назва запису (<c>DisplayL10n</c>,
/// як у самій комірці), id лишається в <c>OldValue</c>/<c>NewValue</c> (CSV-експорт — сирий, як і доти).
/// </summary>
/// <remarks>
/// ⚠ Ціна: ОДНЕ пакетне звернення на сторінку журналу (<see cref="IRegistryStore.FindEntriesByIdsAsync"/>) і лише коли на
/// сторінці є довідникові колонки; без сховища (тести, вузькі збірки) — без підписів.
/// ⚠ Межі читання не послаблюються: підпис додається лише до вже відфільтрованих рядків журналу (колонку читач бачить), а
/// назва запису довідника — те саме, що показує сама комірка зрізу.
/// </remarks>
public static class LookupValueLabels
{
    /// <summary>Тип колонки довідника (<c>CellDataType.Lookup</c>).</summary>
    public const string LookupType = "Lookup";

    /// <summary>Id запису з тексту значення журналу; не число — <c>null</c>.</summary>
    public static long? EntryId(string? dataType, string? value)
        => string.Equals(dataType, LookupType, StringComparison.Ordinal)
           && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;

    /// <summary>Назви записів за Id — одним зверненням; нічого шукати чи сховища немає — порожньо.</summary>
    public static async Task<IReadOnlyDictionary<long, LocalizedText>> ResolveAsync(
        IRegistryStore? registries, IEnumerable<long?> ids, CancellationToken ct)
    {
        var wanted = ids.OfType<long>().Distinct().ToList();
        if (registries is null || wanted.Count == 0)
        {
            return new Dictionary<long, LocalizedText>();
        }

        var entries = await registries.FindEntriesByIdsAsync(wanted, ct).ConfigureAwait(false);
        return entries
            .GroupBy(e => e.Id)
            .ToDictionary(g => g.Key, g => g.First().DisplayL10n);
    }

    /// <summary>Сторінка журналу з назвами довідникових значень.</summary>
    public static async Task<IReadOnlyList<CellChangeView>> WithLabelsAsync(
        IReadOnlyList<CellChangeView> items, IRegistryStore? registries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        var labels = await ResolveAsync(
            registries,
            items.SelectMany(c => new[] { EntryId(c.ColumnDataType, c.OldValue), EntryId(c.ColumnDataType, c.NewValue) }),
            ct).ConfigureAwait(false);
        if (labels.Count == 0)
        {
            return items;
        }

        LocalizedText? Of(string? dataType, string? value)
            => EntryId(dataType, value) is { } id && labels.TryGetValue(id, out var label) ? label : null;

        return
        [
            .. items.Select(c => c with
            {
                OldValueLabelL10n = Of(c.ColumnDataType, c.OldValue),
                NewValueLabelL10n = Of(c.ColumnDataType, c.NewValue),
            }),
        ];
    }
}
