using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Infrastructure.Security;

/// <summary>Знімок-обʼєднання багатоверсійного проєкту (RPT-STABLE Part B) — один на набір ЕКЗЕМПЛЯРІВ знімків.</summary>
/// <remarks>
/// ⛔ P-6 (аудит 2026-11-10): <c>MetadataCache</c> віддає той самий екземпляр знімка, доки ревізія не змінилась; нова
/// ревізія — новий екземпляр, тож кеш за посиланнями не потребує інвалідації: обʼєднання зі старим екземпляром просто
/// перестає збігатися і будується наново. Обʼєднання лише читається (це шлях прав — S6, D-443): записувати в його
/// словники не можна, вони спільні між запитами.
/// </remarks>
internal static class ProjectReadSnapshots
{
    private sealed record Merged(TemplateVersionSnapshot[] Others, TemplateVersionSnapshot Snapshot);

    private static readonly ConditionalWeakTable<TemplateVersionSnapshot, ConcurrentDictionary<string, Merged>> Cache = new();

    /// <summary>Поточна версія проєкту разом з іншими його версіями: аркуші всіх, колонки й рядки — поточна перемагає.</summary>
    /// <param name="current">Знімок поточної версії проєкту.</param>
    /// <param name="others">Знімки решти версій проєкту в сталому порядку (за Id версії).</param>
    public static TemplateVersionSnapshot Merge(TemplateVersionSnapshot current, IReadOnlyList<TemplateVersionSnapshot> others)
    {
        var key = string.Join(',', others.Select(o => o.TemplateVersionId));
        var byOthers = Cache.GetValue(current, _ => new ConcurrentDictionary<string, Merged>(StringComparer.Ordinal));
        if (byOthers.TryGetValue(key, out var known)
            && known.Others.Length == others.Count
            && known.Others.Zip(others).All(p => ReferenceEquals(p.First, p.Second)))
        {
            return known.Snapshot;
        }

        var sheets = new List<SheetDef>(current.Sheets);
        var columns = new Dictionary<int, ColumnDef>(current.ColumnsById);
        var rows = new Dictionary<(int TableDefId, string RowKey), RowDef>(current.RowsByKey);
        foreach (var other in others)
        {
            sheets.AddRange(other.Sheets);
            foreach (var column in other.ColumnsById)
            {
                columns.TryAdd(column.Key, column.Value);
            }

            foreach (var row in other.RowsByKey)
            {
                rows.TryAdd(row.Key, row.Value);
            }
        }

        var merged = current with { Sheets = sheets, ColumnsById = columns, RowsByKey = rows };
        byOthers[key] = new Merged([.. others], merged);
        return merged;
    }
}
