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

        var target = new Dictionary<(string Sheet, string Table, string Column), int>();
        foreach (var sheet in to.Sheets.Where(s => !s.IsDeleted))
        {
            foreach (var table in sheet.Tables.Where(t => !t.IsDeleted))
            {
                foreach (var column in table.Columns.Where(c => !c.IsDeleted))
                {
                    target[(sheet.Code, table.Code, column.Code)] = column.Id;
                }
            }
        }

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

        return [.. cells.Select(c => map.TryGetValue(c.Column, out var id) ? c with { Column = id } : c)];
    }
}
