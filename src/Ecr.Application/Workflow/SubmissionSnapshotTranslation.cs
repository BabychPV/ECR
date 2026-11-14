using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Workflow;

/// <summary>
/// V-2/V-3 (аудит 2026-11): зріз подання адресує аркуш і колонки ВЕРСІЇ, на якій його зняли (перенос проєкту зрізів
/// не чіпає — це доказ). Читачі, що порівнюють зріз із живими даними чи межами читання, перекладають Id на версію
/// документа за кодами «аркуш → таблиця → колонка» — та сама ідентичність, що в <c>VersionMigrationPlanner</c>.
/// </summary>
public static class SubmissionSnapshotTranslation
{
    /// <summary>Аркуш версії <paramref name="to"/> з тим самим кодом; <c>null</c> — такого аркуша немає.</summary>
    /// <param name="sheetDefId">Аркуш у версії <paramref name="from"/>.</param>
    /// <param name="from">Версія, на якій знято зріз.</param>
    /// <param name="to">Версія документа (ціль перекладу).</param>
    public static int? Sheet(int sheetDefId, TemplateVersionSnapshot from, TemplateVersionSnapshot to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (from.TemplateVersionId == to.TemplateVersionId)
        {
            return sheetDefId;
        }

        var code = from.Sheets.FirstOrDefault(s => s.Id == sheetDefId)?.Code;
        return code is null ? null : to.Sheets.FirstOrDefault(s => !s.IsDeleted && s.Code == code)?.Id;
    }

    /// <summary>Комірки з колонками версії <paramref name="to"/>; колонка без відповідника лишається як є.</summary>
    /// <param name="cells">Комірки зрізу (колонки версії <paramref name="from"/>).</param>
    /// <param name="from">Версія, на якій знято зріз.</param>
    /// <param name="to">Версія документа (ціль перекладу).</param>
    public static IReadOnlyList<SubmissionPayloadCell> Cells(
        IReadOnlyList<SubmissionPayloadCell> cells, TemplateVersionSnapshot from, TemplateVersionSnapshot to)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (from.TemplateVersionId == to.TemplateVersionId || cells.Count == 0)
        {
            return cells;
        }

        var map = CodeMap(from, to);
        return [.. cells.Select(c => map.TryGetValue(c.Column, out var id) ? c with { Column = id } : c)];
    }

    /// <summary>
    /// ✎ B72: як <see cref="Cells"/>, а колонки без відповідника за кодами шукаються ще й за збереженими перейменуваннями
    /// (<c>cfg.ColumnRename</c>, R-2) — таблиця, перейменована мапою переносу (<c>tableRenames</c>), лишається тією самою.
    /// </summary>
    /// <param name="cells">Комірки зрізу (колонки версії <paramref name="from"/>).</param>
    /// <param name="from">Версія, на якій знято зріз.</param>
    /// <param name="to">Версія документа (ціль перекладу).</param>
    /// <param name="paths">Родовід колонок; <c>null</c> — лише коди (як <see cref="Cells"/>).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Окремої мапи таблиць у схемі немає: перейменування таблиці зберігається парами ВСІХ її колонок. Кандидати — колонки
    /// цільової версії, чийого шляху «аркуш → таблиця → колонка» у версії зрізу немає (нові або перейменовані); їхній
    /// родовід (<see cref="IColumnPathMapper.LineageAsync"/>, транзитивно через версії) дає Id версії зрізу. Усі колонки
    /// зрізу знайшлися за кодами (звичайний перенос) — жодного звернення до бази.
    /// </remarks>
    public static async Task<IReadOnlyList<SubmissionPayloadCell>> CellsAsync(
        IReadOnlyList<SubmissionPayloadCell> cells,
        TemplateVersionSnapshot from,
        TemplateVersionSnapshot to,
        IColumnPathMapper? paths,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (from.TemplateVersionId == to.TemplateVersionId || cells.Count == 0)
        {
            return cells;
        }

        var map = CodeMap(from, to);
        var unmapped = cells.Select(c => c.Column).Where(c => !map.ContainsKey(c)).ToHashSet();
        if (paths is not null && unmapped.Count > 0)
        {
            var sourcePaths = Paths(from, liveOnly: false).Keys.ToHashSet();
            var candidates = Paths(to, liveOnly: true)
                .Where(p => !sourcePaths.Contains(p.Key))
                .Select(p => p.Value)
                .Order()
                .ToList();
            if (candidates.Count > 0)
            {
                var lineage = await paths.LineageAsync(candidates, ct).ConfigureAwait(false);
                foreach (var candidate in candidates)
                {
                    foreach (var id in lineage.GetValueOrDefault(candidate) ?? [])
                    {
                        if (unmapped.Contains(id))
                        {
                            map.TryAdd(id, candidate);
                        }
                    }
                }
            }
        }

        return [.. cells.Select(c => map.TryGetValue(c.Column, out var id) ? c with { Column = id } : c)];
    }

    /// <summary>Колонка версії <paramref name="from"/> → колонка версії <paramref name="to"/> з тим самим шляхом кодів.</summary>
    private static Dictionary<int, int> CodeMap(TemplateVersionSnapshot from, TemplateVersionSnapshot to)
    {
        var target = Paths(to, liveOnly: true);
        var map = new Dictionary<int, int>();
        foreach (var sheet in from.Sheets)
        {
            foreach (var table in sheet.Tables)
            {
                foreach (var column in table.Columns)
                {
                    if (target.TryGetValue((sheet.Code, table.Code, column.Code), out var id))
                    {
                        map[column.Id] = id;
                    }
                }
            }
        }

        return map;
    }

    /// <summary>Шлях «аркуш → таблиця → колонка» кожної колонки версії; <paramref name="liveOnly"/> — без вилучених.</summary>
    private static Dictionary<(string Sheet, string Table, string Column), int> Paths(TemplateVersionSnapshot version, bool liveOnly)
    {
        var result = new Dictionary<(string Sheet, string Table, string Column), int>();
        foreach (var sheet in version.Sheets.Where(s => !liveOnly || !s.IsDeleted))
        {
            foreach (var table in sheet.Tables.Where(t => !liveOnly || !t.IsDeleted))
            {
                foreach (var column in table.Columns.Where(c => !liveOnly || !c.IsDeleted))
                {
                    result[(sheet.Code, table.Code, column.Code)] = column.Id;
                }
            }
        }

        return result;
    }
}
