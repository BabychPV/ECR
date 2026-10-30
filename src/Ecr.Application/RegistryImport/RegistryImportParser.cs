using System.Globalization;
using System.Text.RegularExpressions;
using Ecr.Application.Errors;

namespace Ecr.Application.RegistryImport;

/// <summary>
/// Reads a worksheet into parents and children by a <see cref="RegistryImportSpec"/>: header block detection and
/// mapping, group rows, totals skipping, fill-down, per-field parsers. Pure: no workbook, no database.
/// </summary>
public static partial class RegistryImportParser
{
    /// <summary>Findings kept per severity; the rest are only counted.</summary>
    public const int MaxIssues = 2000;

    private const int MaxHeaderRows = 6;
    private const int MinHeaderCells = 5;
    private const int NumberRowScanLimit = 80;

    /// <summary>Reads the grid.</summary>
    /// <param name="grid">The worksheet.</param>
    /// <param name="spec">A validated spec.</param>
    /// <returns>Parents with children and findings. A missing required column ends the parse early (no rows are read).</returns>
    /// <exception cref="BusinessRuleException"><c>ECR-IMP-0422</c> <c>regHeaderNotFound</c>.</exception>
    public static ParsedImport Parse(IImportGrid grid, RegistryImportSpec spec)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(spec);

        var log = new IssueLog();
        var (headerFrom, headerTo, dataFrom) = LocateHeader(grid, spec);
        var headers = ReadHeaders(grid, headerFrom, headerTo);
        var columns = MapColumns(spec, headers, log);

        var header = BuildReport(grid, spec, headers, columns, headerFrom, headerTo, dataFrom);

        if (log.HasErrors)
        {
            return new ParsedImport(header, [], [], log.Issues, new ImportStats(0, 0, 0, 0, 0), log.Dropped);
        }

        return new RowReader(grid, spec, columns, log, header).Run();
    }

    // ---------- header ----------

    private static (int From, int To, int DataFrom) LocateHeader(IImportGrid grid, RegistryImportSpec spec)
    {
        if (spec.Header.FromRow is not null || spec.Header.ToRow is not null)
        {
            var from = spec.Header.FromRow ?? spec.Header.ToRow!.Value;
            var to = spec.Header.ToRow ?? spec.Header.FromRow!.Value;

            if (from < 1 || to < from || to > grid.RowCount)
            {
                throw HeaderNotFound();
            }

            var auto = spec.DataStartRow ?? (to + 1 + (IsNumberRow(grid, to + 1) ? 1 : 0));
            return (from, to, auto);
        }

        var limit = Math.Min(grid.RowCount, NumberRowScanLimit);
        for (var r = 1; r <= limit; r++)
        {
            if (!IsNumberRow(grid, r))
            {
                continue;
            }

            var to = r - 1;
            if (to < 1)
            {
                break;
            }

            var from = to;
            for (var h = Math.Max(1, to - MaxHeaderRows + 1); h <= to; h++)
            {
                if (CountFilled(grid, h) >= MinHeaderCells)
                {
                    from = h;
                    break;
                }
            }

            return (from, to, spec.DataStartRow ?? (r + 1));
        }

        throw HeaderNotFound();
    }

    private static BusinessRuleException HeaderNotFound()
        => new(
            "ECR-IMP-0422",
            "The header block was not found in the file.",
            new Dictionary<string, object?> { ["messageKey"] = RegistryImportKeys.HeaderNotFound });

    /// <summary>A row of column numbers (<c>1 2 3 ... 7a ... 16a</c>) that closes the header block in forms like ERA table 3.3.</summary>
    private static bool IsNumberRow(IImportGrid grid, int row)
    {
        if (row < 1 || row > grid.RowCount)
        {
            return false;
        }

        var filled = 0;
        var numeric = 0;

        for (var c = 1; c <= grid.ColumnCount; c++)
        {
            var text = ImportText.Clean(ImportText.AsText(grid.Get(row, c).Value));
            if (text.Length == 0)
            {
                continue;
            }

            filled++;
            if (ColumnNumber().IsMatch(text))
            {
                numeric++;
            }
        }

        return filled >= MinHeaderCells && numeric * 5 >= filled * 4;
    }

    private static int CountFilled(IImportGrid grid, int row)
    {
        var n = 0;
        for (var c = 1; c <= grid.ColumnCount; c++)
        {
            if (!ImportText.IsEmpty(grid.Get(row, c).Value))
            {
                n++;
            }
        }

        return n;
    }

    private sealed record ColumnHeader(int Column, string Combined, string Folded, string LastRow);

    private static ColumnHeader[] ReadHeaders(IImportGrid grid, int from, int to)
    {
        var result = new ColumnHeader[grid.ColumnCount];

        for (var c = 1; c <= grid.ColumnCount; c++)
        {
            var parts = new List<string>();
            var seen = new HashSet<(int, int)>();
            var last = "";

            for (var r = from; r <= to; r++)
            {
                var merge = grid.MergeAt(r, c);
                var anchor = merge is null ? (r, c) : (merge.FirstRow, merge.FirstColumn);
                var text = ImportText.Clean(ImportText.AsText(grid.Get(anchor.Item1, anchor.Item2).Value));

                if (r == to)
                {
                    last = text;
                }

                if (text.Length > 0 && seen.Add(anchor))
                {
                    parts.Add(text);
                }
            }

            var combined = string.Join(' ', parts);
            result[c - 1] = new ColumnHeader(c, combined, ImportText.Fold(combined), last);
        }

        return result;
    }

    private static bool PatternMatches(string folded, string pattern)
    {
        foreach (var variant in pattern.Split('|'))
        {
            var parts = WildcardSplit().Split(variant)
                .Select(ImportText.Fold)
                .Where(p => p.Length > 0)
                .ToArray();

            if (parts.Length == 0)
            {
                continue;
            }

            var at = 0;
            var ok = true;
            foreach (var part in parts)
            {
                var i = folded.IndexOf(part, at, StringComparison.Ordinal);
                if (i < 0)
                {
                    ok = false;
                    break;
                }

                at = i + part.Length;
            }

            if (ok)
            {
                return true;
            }
        }

        return false;
    }

    private static bool QualifierMatches(ColumnHeader header, IReadOnlyList<string> qualifiers)
    {
        if (qualifiers.Count == 0)
        {
            return true;
        }

        var last = ImportText.Fold(header.LastRow);
        return qualifiers.SelectMany(q => q.Split('|')).Any(q => ImportText.Fold(q) == last);
    }

    private static Dictionary<string, int> MapColumns(RegistryImportSpec spec, ColumnHeader[] headers, IssueLog log)
    {
        var mapped = new Dictionary<string, int>(StringComparer.Ordinal);
        var claimedBy = new Dictionary<int, string>();

        foreach (var field in spec.Fields)
        {
            int? column = null;

            if (field.Column is { } letter)
            {
                column = ColumnLetters.ToIndex(letter);
                if (column > headers.Length)
                {
                    column = null;
                }
            }
            else
            {
                var hits = headers
                    .Where(h => field.Patterns.Any(p => PatternMatches(h.Folded, p)) && QualifierMatches(h, field.UnitQualifier))
                    .Select(h => h.Column)
                    .ToList();

                if (hits.Count > 1)
                {
                    log.Add(new ImportIssue(
                        ImportSeverity.Error, RegistryImportKeys.ColumnAmbiguous, null, null, field.Name,
                        new Dictionary<string, string>
                        {
                            ["field"] = field.Name,
                            ["columns"] = string.Join(", ", hits.Select(ColumnLetters.ToLetters)),
                        }));
                    continue;
                }

                column = hits.Count == 1 ? hits[0] : null;
            }

            if (column is null)
            {
                log.Add(new ImportIssue(
                    field.Required ? ImportSeverity.Error : ImportSeverity.Warning,
                    RegistryImportKeys.ColumnMissing, null, null, field.Name,
                    new Dictionary<string, string> { ["field"] = field.Name }));
                continue;
            }

            if (claimedBy.TryGetValue(column.Value, out var other))
            {
                log.Add(new ImportIssue(
                    ImportSeverity.Error, RegistryImportKeys.ColumnClaimed, null, ColumnLetters.ToLetters(column.Value), field.Name,
                    new Dictionary<string, string>
                    {
                        ["field"] = field.Name,
                        ["other"] = other,
                        ["column"] = ColumnLetters.ToLetters(column.Value),
                    }));
                continue;
            }

            claimedBy[column.Value] = field.Name;
            mapped[field.Name] = column.Value;
        }

        return mapped;
    }

    private static ImportHeaderReport BuildReport(
        IImportGrid grid, RegistryImportSpec spec, ColumnHeader[] headers, Dictionary<string, int> columns,
        int from, int to, int dataFrom)
    {
        var mapped = spec.Fields
            .Where(f => columns.ContainsKey(f.Name))
            .Select(f => new MappedColumn(
                f.Name, f.Level, columns[f.Name], ColumnLetters.ToLetters(columns[f.Name]),
                headers[columns[f.Name] - 1].Combined))
            .ToList();

        var used = columns.Values.ToHashSet();
        var unmapped = new List<UnmappedColumn>();

        foreach (var h in headers.Where(h => h.Combined.Length > 0 && !used.Contains(h.Column)))
        {
            var hasData = false;
            for (var r = dataFrom; r <= grid.RowCount && !hasData; r++)
            {
                hasData = !ImportText.IsEmpty(grid.Get(r, h.Column).Value);
            }

            unmapped.Add(new UnmappedColumn(h.Column, ColumnLetters.ToLetters(h.Column), h.Combined, hasData));
        }

        return new ImportHeaderReport(from, to, dataFrom, mapped, unmapped);
    }

    // ---------- rows ----------

    private sealed class IssueLog
    {
        private int _errors;
        private int _warnings;

        public List<ImportIssue> Issues { get; } = [];

        public int Dropped { get; private set; }

        public bool HasErrors { get; private set; }

        public void Add(ImportIssue issue)
        {
            if (issue.Severity == ImportSeverity.Error)
            {
                HasErrors = true;
                if (_errors++ >= MaxIssues)
                {
                    Dropped++;
                    return;
                }
            }
            else if (_warnings++ >= MaxIssues)
            {
                Dropped++;
                return;
            }

            Issues.Add(issue);
        }
    }

    private sealed class RowReader
    {
        private readonly IImportGrid _grid;
        private readonly RegistryImportSpec _spec;
        private readonly Dictionary<string, int> _columns;
        private readonly IssueLog _log;
        private readonly ImportHeaderReport _header;
        private readonly Dictionary<string, ImportFieldReader> _readers;
        private readonly Dictionary<int, object?> _carried = [];
        private readonly List<ImportedParent> _parents = [];
        private readonly List<ImportGroupRow> _groups = [];
        private readonly Dictionary<string, int> _parentRows = new(StringComparer.Ordinal);
        private readonly HashSet<string> _childKeys = new(StringComparer.Ordinal);

        private ParentBuilder? _current;
        private ImportGroupRow? _group;
        private int _skipped;
        private int _children;

        public RowReader(IImportGrid grid, RegistryImportSpec spec, Dictionary<string, int> columns, IssueLog log, ImportHeaderReport header)
        {
            _grid = grid;
            _spec = spec;
            _columns = columns;
            _log = log;
            _header = header;
            _readers = spec.Fields.ToDictionary(f => f.Name, f => new ImportFieldReader(f, spec.NaMarkers), StringComparer.Ordinal);
        }

        private sealed class ParentBuilder
        {
            public required int Row { get; init; }

            public required string Key { get; init; }

            public required Dictionary<string, object?> Values { get; init; }

            public ImportGroupRow? Group { get; init; }

            public List<ImportedChild> Children { get; } = [];

            public HashSet<string> ChildKeys { get; } = new(StringComparer.Ordinal);

            public bool Duplicate { get; init; }
        }

        public ParsedImport Run()
        {
            var pField = _spec.Fields.First(f => f.Name == _spec.ParentKeyField);
            var cField = _spec.Fields.First(f => f.Name == _spec.ChildKeyField);
            var pCol = _columns[pField.Name];
            var cCol = _columns[cField.Name];

            for (var r = _header.DataFromRow; r <= _grid.RowCount; r++)
            {
                var pCell = _grid.Get(r, pCol);
                var cCell = _grid.Get(r, cCol);
                var pEmpty = ImportText.IsEmpty(pCell.Value);
                var cEmpty = ImportText.IsEmpty(cCell.Value);

                // A formula in a key cell is a totals row (sum of the block), never data.
                if ((!pEmpty && pCell.HasFormula) || (!cEmpty && cCell.HasFormula))
                {
                    _skipped++;
                    continue;
                }

                if (pEmpty && cEmpty)
                {
                    if (TryGroupRow(r))
                    {
                        continue;
                    }

                    if (RowHasContent(r))
                    {
                        _skipped++;
                    }

                    continue;
                }

                if (!pEmpty)
                {
                    Flush();
                    StartParent(r, pField, pCol);
                }

                if (!cEmpty)
                {
                    AddChild(r, cField, cCol);
                }
            }

            Flush();

            if (_parents.Count == 0 && !_log.HasErrors)
            {
                _log.Add(new ImportIssue(ImportSeverity.Error, RegistryImportKeys.NoRows));
            }

            var childKeys = _childKeys.Count;
            return new ParsedImport(
                _header,
                _parents,
                _groups,
                _log.Issues,
                new ImportStats(_parents.Count, _children, childKeys, _groups.Count, _skipped),
                _log.Dropped);
        }

        private bool TryGroupRow(int row)
        {
            if (_spec.GroupRows is not { } rule)
            {
                return false;
            }

            var text = ImportText.Clean(ImportText.AsText(_grid.Get(row, rule.Column).Value));
            if (text.Length == 0)
            {
                return false;
            }

            var merge = _grid.MergeAt(row, rule.Column);
            var isGroup = merge is not null && merge.Width >= rule.MinMergedColumns;

            if (!isGroup)
            {
                isGroup = true;
                for (var c = 1; c <= _grid.ColumnCount; c++)
                {
                    if (c != rule.Column && !ImportText.IsEmpty(_grid.Get(row, c).Value))
                    {
                        isGroup = false;
                        break;
                    }
                }
            }

            if (!isGroup)
            {
                return false;
            }

            _group = new ImportGroupRow(row, text);
            _groups.Add(_group);
            return true;
        }

        private bool RowHasContent(int row)
        {
            for (var c = 1; c <= _grid.ColumnCount; c++)
            {
                if (!ImportText.IsEmpty(_grid.Get(row, c).Value))
                {
                    return true;
                }
            }

            return false;
        }

        private void StartParent(int row, ImportFieldSpec keyField, int keyColumn)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            var key = ReadField(row, keyField, keyColumn, values);

            foreach (var field in _spec.Fields.Where(f => f.Level == ImportLevel.Parent && f.Name != keyField.Name && _columns.ContainsKey(f.Name)))
            {
                ReadField(row, field, _columns[field.Name], values);
            }

            if (key is null)
            {
                // The key is unreadable (already reported): keep a placeholder so its child rows are read
                // (their own problems are reported) without a cascade of "child without parent".
                _current = new ParentBuilder { Row = row, Key = "", Values = values, Group = _group, Duplicate = true };
                return;
            }

            var duplicate = _parentRows.TryGetValue(key, out var first);
            if (duplicate)
            {
                _log.Add(new ImportIssue(
                    ImportSeverity.Error, RegistryImportKeys.DuplicateParent, row, ColumnLetters.ToLetters(keyColumn), keyField.Name,
                    new Dictionary<string, string>
                    {
                        ["key"] = key,
                        ["row"] = row.ToString(CultureInfo.InvariantCulture),
                        ["firstRow"] = first.ToString(CultureInfo.InvariantCulture),
                    }));
            }
            else
            {
                _parentRows[key] = row;
            }

            _current = new ParentBuilder
            {
                Row = row,
                Key = key,
                Values = values,
                Group = _group,
                Duplicate = duplicate,
            };
        }

        private void AddChild(int row, ImportFieldSpec keyField, int keyColumn)
        {
            if (_current is null)
            {
                _log.Add(new ImportIssue(
                    ImportSeverity.Error, RegistryImportKeys.ChildWithoutParent, row, ColumnLetters.ToLetters(keyColumn), keyField.Name,
                    new Dictionary<string, string> { ["row"] = row.ToString(CultureInfo.InvariantCulture) }));
                return;
            }

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            var key = ReadField(row, keyField, keyColumn, values);

            foreach (var field in _spec.Fields.Where(f => f.Level == ImportLevel.Child && f.Name != keyField.Name && _columns.ContainsKey(f.Name)))
            {
                ReadField(row, field, _columns[field.Name], values);
            }

            if (key is null)
            {
                return;
            }

            if (!_current.ChildKeys.Add(key))
            {
                _log.Add(new ImportIssue(
                    ImportSeverity.Error, RegistryImportKeys.DuplicateChild, row, ColumnLetters.ToLetters(keyColumn), keyField.Name,
                    new Dictionary<string, string> { ["key"] = key, ["parent"] = _current.Key }));
                return;
            }

            if (!_current.Duplicate)
            {
                _children++;
                _childKeys.Add(key);
            }

            _current.Children.Add(new ImportedChild(row, key, values));
        }

        private void Flush()
        {
            if (_current is not { } p)
            {
                return;
            }

            _current = null;

            if (p.Duplicate)
            {
                return;
            }

            if (p.Children.Count == 0)
            {
                _log.Add(new ImportIssue(
                    ImportSeverity.Warning, RegistryImportKeys.ParentWithoutChildren, p.Row, null, null,
                    new Dictionary<string, string> { ["key"] = p.Key }));
            }

            _parents.Add(new ImportedParent(p.Row, p.Key, p.Group?.Label, p.Group?.Row, p.Values, p.Children));
        }

        /// <summary>Reads one field of a row, applies the fill rule, records the value and findings; returns the key string for key fields.</summary>
        private string? ReadField(int row, ImportFieldSpec field, int column, Dictionary<string, object?> values)
        {
            var raw = _grid.Get(row, column).Value;

            if (ImportText.IsEmpty(raw))
            {
                if (field.FillDown == FillDown.Merge && _grid.MergeAt(row, column) is { } merge)
                {
                    raw = _grid.Get(merge.FirstRow, merge.FirstColumn).Value;
                }
                else if (field.FillDown == FillDown.Blank && _carried.TryGetValue(column, out var above))
                {
                    raw = above;
                }
            }
            else if (field.FillDown == FillDown.Blank)
            {
                _carried[column] = raw;
            }

            var result = _readers[field.Name].Read(raw);
            var letter = ColumnLetters.ToLetters(column);

            if (result.ErrorKey is { } error)
            {
                _log.Add(new ImportIssue(
                    ImportSeverity.Error, error, row, letter, field.Name,
                    new Dictionary<string, string> { ["field"] = field.Name, ["value"] = Shorten(ImportText.AsText(raw)) }));
                values[field.Name] = null;
                return null;
            }

            if (result.WarningKey is { } warning)
            {
                _log.Add(new ImportIssue(
                    ImportSeverity.Warning, warning, row, letter, field.Name,
                    new Dictionary<string, string> { ["field"] = field.Name, ["value"] = Shorten(ImportText.AsText(raw)) }));
            }

            var isKey = field.Name == _spec.ParentKeyField || field.Name == _spec.ChildKeyField;

            if (result.Empty && (field.NonEmpty || isKey))
            {
                _log.Add(new ImportIssue(
                    ImportSeverity.Error, RegistryImportKeys.ValueRequired, row, letter, field.Name,
                    new Dictionary<string, string> { ["field"] = field.Name }));
            }

            if (result.Empty && field.WarnIfEmpty && !field.NonEmpty && !isKey)
            {
                _log.Add(new ImportIssue(
                    ImportSeverity.Warning, RegistryImportKeys.ValueEmpty, row, letter, field.Name,
                    new Dictionary<string, string> { ["field"] = field.Name }));
            }

            values[field.Name] = result.Value;
            return isKey ? result.Value as string ?? (result.Value is null ? null : Convert.ToString(result.Value, CultureInfo.InvariantCulture)) : null;
        }

        private static string Shorten(string text)
        {
            var clean = ImportText.Clean(text);
            return clean.Length <= 40 ? clean : clean[..40] + "...";
        }
    }

    [GeneratedRegex(@"^\d{1,3}\s*[A-Za-zА-я]?\z")]
    private static partial Regex ColumnNumber();

    [GeneratedRegex(@"…|\.\.\.|\*")]
    private static partial Regex WildcardSplit();
}
