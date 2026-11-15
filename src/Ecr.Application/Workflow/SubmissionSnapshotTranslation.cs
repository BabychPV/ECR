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
    /// <param name="documentId">✎ B127: документ зрізу — родовід лише з перейменувань його проєкту.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Окремої мапи таблиць у схемі немає: перейменування таблиці зберігається парами ВСІХ її колонок. Кандидати — колонки
    /// цільової версії, не зайняті жодною колонкою зрізу за кодами (нові або перейменовані); їхній
    /// родовід (<see cref="IColumnPathMapper.LineageAsync"/>, транзитивно через версії) дає Id версії зрізу. Усі колонки
    /// зрізу знайшлися за кодами (звичайний перенос) — жодного звернення до бази.
    /// </remarks>
    public static async Task<IReadOnlyList<SubmissionPayloadCell>> CellsAsync(
        IReadOnlyList<SubmissionPayloadCell> cells,
        TemplateVersionSnapshot from,
        TemplateVersionSnapshot to,
        IColumnPathMapper? paths,
        long? documentId,
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
        if (paths is not null && cells.Any(c => !map.ContainsKey(c.Column)))
        {
            // ✎ B72 (M10): кандидат — колонка цільової версії, яку НЕ зайняла жодна колонка зрізу за кодами (включно з
            // вилученою: CodeMap перебирає всі колонки `from`, як і до B72). Інакше вилучена колонка з тим самим шляхом
            // (за кодом) і перейменоване джерело (за родоводом) зійшлися б в одному кандидаті.
            var occupied = map.Values.ToHashSet();
            var candidates = Paths(to, liveOnly: true)
                .Select(p => p.Value)
                .Where(id => !occupied.Contains(id))
                .Order()
                .ToList();
            if (candidates.Count > 0)
            {
                // Джерела — УСІ колонки версії зрізу без відповідника за кодами (а не лише ті, що є в зрізі): однозначність
                // пари не залежить від того, які комірки випадково потрапили в зріз.
                var unmapped = from.Sheets.SelectMany(s => s.Tables).SelectMany(t => t.Columns)
                    .Select(c => c.Id)
                    .Where(id => !map.ContainsKey(id))
                    .ToHashSet();
                var lineage = await paths.LineageAsync(candidates, documentId, ct).ConfigureAwait(false);
                foreach (var (source, candidate) in UnambiguousPairs(candidates, lineage, unmapped))
                {
                    map[source] = candidate;
                }
            }
        }

        return [.. cells.Select(c => map.TryGetValue(c.Column, out var id) ? c with { Column = id } : c)];
    }

    /// <summary>
    /// ✎ B72 (D1): лише ОДНОЗНАЧНІ пари «джерело → кандидат»: у кандидата рівно одне джерело серед
    /// <paramref name="unmapped"/>, і це джерело — у родоводі рівно одного кандидата.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>cfg.ColumnRename</c> глобальний (без проєкту), а перенос — на проєкт: проєкт A міг перенести <c>a → z</c>, а
    /// проєкт B тієї самої пари версій — <c>b → z</c>. Тоді в <c>z</c> два джерела, і будь-який вибір — вигадка (або дві
    /// комірки зрізу на одній адресі, або значення відкинутої колонки під чужим кодом). Неоднозначне лишається
    /// неперекладеним — як до B72 (порівняння покаже це «зміною», а не хибними даними).
    /// </remarks>
    private static IEnumerable<(int Source, int Candidate)> UnambiguousPairs(
        IReadOnlyList<int> candidates,
        IReadOnlyDictionary<int, IReadOnlyList<int>> lineage,
        IReadOnlySet<int> unmapped)
    {
        var sourcesOf = candidates.Distinct().ToDictionary(
            c => c,
            c => (lineage.GetValueOrDefault(c) ?? []).Where(unmapped.Contains).Distinct().ToList());
        var candidatesOf = sourcesOf
            .SelectMany(p => p.Value.Select(s => (Source: s, Candidate: p.Key)))
            .GroupBy(p => p.Source)
            .ToDictionary(g => g.Key, g => g.Count());

        return sourcesOf
            .Where(p => p.Value.Count == 1 && candidatesOf[p.Value[0]] == 1)
            .Select(p => (p.Value[0], p.Key))
            .OrderBy(p => p.Item1);
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
