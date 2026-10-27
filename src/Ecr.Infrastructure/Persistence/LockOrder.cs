using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// AN-139: єдиний порядок захоплення блокувань на шляху запису комірок — період ↑, рядок ↑, колонка ↑.
/// </summary>
/// <remarks>
/// ⛔ Два паралельні батчі, що перетинаються рядками, мають брати блокування <c>doc.TableRow</c> і
/// <c>doc.CellValue</c> в ОДНАКОВОМУ порядку, інакше складаються у взаємне блокування
/// (спостерігалось ~1/640 на <c>MERGE doc.CellValue</c> / <c>PK_TableRow</c>). Порядок входу набору
/// (словник клієнта, порядок екземплярів у пакеті) цією властивістю не володіє.
///
/// ⚠ <see cref="Trace"/> — шов для детермінованого тесту порядку: <see cref="Sink"/> порожній
/// у проді, у тесті збирає послідовність звернень (крок, період, ключі) поточного потоку виконання.
/// </remarks>
internal static class LockOrder
{
    private static readonly AsyncLocal<List<string>?> SinkHolder = new();

    /// <summary>Приймач послідовності звернень; <c>null</c> — трасування вимкнено (прод).</summary>
    internal static List<string>? Sink
    {
        get => SinkHolder.Value;
        set => SinkHolder.Value = value;
    }

    /// <summary>Записує крок, лише якщо трасування увімкнено (рядок не будується інакше).</summary>
    internal static void Trace(Func<string> step) => SinkHolder.Value?.Add(step());

    /// <summary>Комірки для видалення всіх наборів — за зростанням (період, рядок, колонка).</summary>
    internal static IReadOnlyList<CellAddress> Deletes(IReadOnlyList<CellChangeSet> sets)
        => [.. sets.SelectMany(s => s.Deletes)
            .OrderBy(a => a.PeriodKey.Value).ThenBy(a => a.TableRowId).ThenBy(a => a.ColumnDefId)];

    /// <summary>Комірки для запису всіх наборів — за зростанням (період, рядок, колонка).</summary>
    internal static IReadOnlyList<CellRecord> Upserts(IReadOnlyList<CellChangeSet> sets)
        => [.. sets.SelectMany(s => s.Upserts)
            .OrderBy(r => r.Address.PeriodKey.Value).ThenBy(r => r.Address.TableRowId).ThenBy(r => r.Address.ColumnDefId)];

    /// <summary>Групи за періодом — за зростанням ключа; група без періоду йде останньою.</summary>
    internal static IEnumerable<(int? PeriodKey, List<T> Items)> Periods<T>(Dictionary<int, List<T>> groups, List<T> unfiltered)
        => groups.OrderBy(g => g.Key).Select(g => ((int?)g.Key, g.Value)).Append((null, unfiltered));
}
