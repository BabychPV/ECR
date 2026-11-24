// src/Ecr.Application/Calculations/Scripting/ScriptEnvironmentChecker.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Scripting;

namespace Ecr.Application.Calculations.Scripting;

/// <summary>Результат перевірки оточення.</summary>
/// <param name="Diagnostics">Діагностики оточення (без діагностик компілятора).</param>
/// <param name="RowTables">Розв'язані «живі» таблиці всіх речень <c>rows from</c>.</param>
/// <param name="ResultSourceMethodologyIds">Методології, чиї результати скрипт читає як колонки <c>Calculated</c>.</param>
public sealed record ScriptEnvironment(
    IReadOnlyList<ScriptDiagnostic> Diagnostics,
    IReadOnlyList<LiveTable> RowTables,
    IReadOnlyList<int> ResultSourceMethodologyIds);

/// <summary>
/// Перевірки скрипта, яким потрібна база (MS, план 5.2, 13.1, 13.2, 13.4, 13.9): одиниці, речовини, таблиці й
/// колонки, записи довідників, читання результатів інших методологій, цикл між методологіями.
/// </summary>
/// <param name="methodologies">Сховище методологій.</param>
/// <param name="drafts">Сховище чернеток (коди методологій для пояснення циклу).</param>
/// <param name="units">Довідник одиниць.</param>
/// <param name="registries">Довідники.</param>
public sealed class ScriptEnvironmentChecker(
    IMethodologyStore methodologies,
    IMethodologyDraftStore drafts,
    IUnitCatalog units,
    IRegistryStore registries)
{
    /// <summary>Перевіряє скомпільований скрипт.</summary>
    /// <param name="script">Скрипт.</param>
    /// <param name="text">Текст (для місць, яких компілятор не позначив).</param>
    /// <param name="methodologyId">Методологія-власник версії.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Діагностики оточення й розв'язані таблиці.</returns>
    public async Task<ScriptEnvironment> CheckAsync(
        CompiledScript script, string text, int methodologyId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(text);

        var diagnostics = new List<ScriptDiagnostic>();
        var catalog = await units.GetAsync(ct).ConfigureAwait(false);

        CheckUnits(script, catalog, diagnostics);
        CheckOutputCodes(script, text, diagnostics);
        await CheckSubstancesAsync(script, text, methodologyId, diagnostics, ct).ConfigureAwait(false);

        var resolved = await ResolveTablesAsync(script, diagnostics, ct).ConfigureAwait(false);
        var rowTables = script.Rows
            .SelectMany(r => r.Tables)
            .Select(t => resolved.GetValueOrDefault(t.ToString()))
            .OfType<LiveTable>()
            .DistinctBy(t => t.TableDefId)
            .ToList();

        var used = new List<UsedCalculated>();
        CheckMatchColumns(script, resolved, diagnostics);
        CheckRowFields(script, rowTables, diagnostics, used);
        CheckAliasColumns(script, resolved, diagnostics, used);

        await CheckEntriesAsync(script, diagnostics, ct).ConfigureAwait(false);

        var sources = await CheckResultReadsAsync(methodologyId, used, diagnostics, ct).ConfigureAwait(false);

        diagnostics.AddRange(ScriptUnitChecker.Check(script, catalog, rowTables));

        return new ScriptEnvironment(
            [.. diagnostics.Select((d, i) => (d, i)).OrderBy(x => x.d.Line).ThenBy(x => x.d.Column).ThenBy(x => x.i).Select(x => x.d)],
            rowTables,
            sources);
    }

    private static void CheckUnits(CompiledScript script, UnitCatalogSnapshot catalog, List<ScriptDiagnostic> diagnostics)
    {
        foreach (var use in script.Units)
        {
            if (!catalog.Units.ContainsKey(use.Code))
            {
                diagnostics.Add(new ScriptDiagnostic(
                    ScriptDiagnosticCodes.UnknownUnit, use.Line, use.Column, use.Length,
                    $"Unit '{use.Code}' does not exist in the unit catalogue.",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["unit"] = use.Code }));
            }
        }
    }

    private static void CheckOutputCodes(CompiledScript script, string text, List<ScriptDiagnostic> diagnostics)
    {
        foreach (var output in script.Outputs.Where(o => !EcrCode.TryCreate(o.Code, out _)))
        {
            var span = ScriptTextLocator.FindOutput(text, output.Code);
            diagnostics.Add(ScriptDiagnostic.At(
                ScriptEnvironmentCodes.InvalidOutputCode, span,
                $"Output code '{output.Code}' is invalid: use Latin letters, digits and '_', start with a letter, at most 64 characters.",
                ("code", output.Code)));
        }
    }

    private async Task CheckSubstancesAsync(
        CompiledScript script, string text, int methodologyId, List<ScriptDiagnostic> diagnostics, CancellationToken ct)
    {
        if (script.Substances.Count == 0)
        {
            return;
        }

        // ✎ c11-10: код у кількох довідниках речовин розв'язується довідником, який уже вживає методологія; інакше —
        // діагностика з кодами записів (ними автор пише речовину однозначно), а не мовчазний «менший Id».
        var candidates = await methodologies.FindSubstanceCandidatesAsync(script.Substances, methodologyId, ct).ConfigureAwait(false);
        var resolution = ScriptSubstanceResolver.Resolve(script.Substances, new Dictionary<long, string>(), candidates);
        foreach (var code in script.Substances)
        {
            if (resolution.Ambiguous.TryGetValue(code, out var options))
            {
                var entries = string.Join(", ", options.Select(o => $"{o.EntryCode} ({o.RegistryCode})"));
                diagnostics.Add(ScriptDiagnostic.At(
                    ScriptDiagnosticCodes.AmbiguousSubstance, ScriptTextLocator.FindLiteral(text, code),
                    $"Substance '{code}' exists in several substance registries ({entries}): write the entry code instead.",
                    ("code", code),
                    ("entries", entries)));
            }
            else if (!resolution.Found.ContainsKey(code))
            {
                diagnostics.Add(ScriptDiagnostic.At(
                    ScriptDiagnosticCodes.UnknownSubstance, ScriptTextLocator.FindLiteral(text, code),
                    $"Substance '{code}' was not found in the substance registry.",
                    ("code", code)));
            }
        }
    }

    private async Task<Dictionary<string, LiveTable>> ResolveTablesAsync(
        CompiledScript script, List<ScriptDiagnostic> diagnostics, CancellationToken ct)
    {
        var resolved = new Dictionary<string, LiveTable>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var refs = script.Rows.SelectMany(r => r.Tables).Concat(script.Tables.Values);
        foreach (var reference in refs)
        {
            var key = reference.ToString();
            if (!seen.Add(key))
            {
                continue;
            }

            var live = await methodologies
                .FindLiveTablesByCodeAsync(reference.TableCode, reference.TemplateCode, ct)
                .ConfigureAwait(false);

            if (live.Count == 0)
            {
                diagnostics.Add(ScriptDiagnostic.At(
                    ScriptDiagnosticCodes.UnknownTable, reference.Span,
                    $"Table '{key}' was not found in the latest published version of any template.",
                    ("table", key)));
            }
            else if (live.Select(t => t.TemplateId).Distinct().Count() > 1)
            {
                var templates = string.Join(", ", live.Select(t => t.TemplateCode).Distinct(StringComparer.Ordinal));
                diagnostics.Add(ScriptDiagnostic.At(
                    ScriptDiagnosticCodes.AmbiguousTable, reference.Span,
                    $"Table '{key}' exists in several templates ({templates}): write 'Template.Table'.",
                    ("table", key),
                    ("templates", templates)));
            }
            else
            {
                resolved[key] = live[0];
            }
        }

        return resolved;
    }

    private static void CheckMatchColumns(
        CompiledScript script, Dictionary<string, LiveTable> resolved, List<ScriptDiagnostic> diagnostics)
    {
        foreach (var clause in script.Rows)
        {
            var tables = clause.Tables.Select(t => resolved.GetValueOrDefault(t.ToString())).OfType<LiveTable>().ToList();
            if (tables.Count == 0)
            {
                continue;
            }

            foreach (var (column, span) in MatchColumnsOf(clause.Where).Concat(MatchColumnsOf(clause.Active)))
            {
                if (!tables.Any(t => t.Columns.Any(c => string.Equals(c.Code, column, StringComparison.OrdinalIgnoreCase))))
                {
                    diagnostics.Add(UnknownColumn(span, column, string.Join(", ", clause.Tables.Select(t => t.ToString()))));
                }
            }
        }
    }

    private static IEnumerable<(string Column, ScriptSpan Span)> MatchColumnsOf(MatchNode? node)
    {
        switch (node)
        {
            case null:
                yield break;
            case MatchAnd a:
                foreach (var x in MatchColumnsOf(a.Left).Concat(MatchColumnsOf(a.Right)))
                {
                    yield return x;
                }

                break;
            case MatchOr o:
                foreach (var x in MatchColumnsOf(o.Left).Concat(MatchColumnsOf(o.Right)))
                {
                    yield return x;
                }

                break;
            case MatchNot n:
                foreach (var x in MatchColumnsOf(n.Operand))
                {
                    yield return x;
                }

                break;
            case MatchEquals e:
                yield return (e.Column, e.Span);
                break;
            case MatchIn i:
                yield return (i.Column, i.Span);
                break;
            default:
                break;
        }
    }

    private static void CheckRowFields(
        CompiledScript script, List<LiveTable> rowTables, List<ScriptDiagnostic> diagnostics, List<UsedCalculated> used)
    {
        if (rowTables.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", script.Rows.SelectMany(r => r.Tables).Select(t => t.ToString()).Distinct(StringComparer.Ordinal));

        foreach (var field in script.RowFields)
        {
            Resolution? hit = null;
            foreach (var table in rowTables)
            {
                var r = Resolve(table, field.Name, field.IsMonthly);
                if (r.Found)
                {
                    hit = r;
                    if (r.Calculated is not null)
                    {
                        used.Add(new UsedCalculated(table, r.Calculated, field.Span, field.IsOptional));
                    }

                    break;
                }
            }

            // Необов'язкове поле (`row.X?`) — як у публікації формул: описка в його імені не відмова.
            if (hit is null && !field.IsOptional)
            {
                diagnostics.Add(UnknownColumn(field.Span, field.Name, names));
            }
        }
    }

    private static void CheckAliasColumns(
        CompiledScript script,
        Dictionary<string, LiveTable> resolved,
        List<ScriptDiagnostic> diagnostics,
        List<UsedCalculated> used)
    {
        foreach (var (alias, columns) in script.TableColumns)
        {
            if (!script.Tables.TryGetValue(alias, out var reference)
                || !resolved.TryGetValue(reference.ToString(), out var table))
            {
                continue;
            }

            foreach (var column in columns)
            {
                var r = Resolve(table, column, monthly: false);
                if (!r.Found)
                {
                    r = Resolve(table, column, monthly: true);
                }

                if (!r.Found)
                {
                    diagnostics.Add(UnknownColumn(reference.Span, column, reference.ToString()));
                }
                else if (r.Calculated is not null)
                {
                    used.Add(new UsedCalculated(table, r.Calculated, reference.Span, IsOptional: false));
                }
            }
        }
    }

    private async Task CheckEntriesAsync(CompiledScript script, List<ScriptDiagnostic> diagnostics, CancellationToken ct)
    {
        var registryIds = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in script.RegistryEntries)
        {
            if (!registryIds.TryGetValue(entry.RegistryCode, out var registryId))
            {
                var definition = await registries.FindDefinitionAsync(entry.RegistryCode, ct).ConfigureAwait(false);
                registryId = definition?.Id;
                registryIds[entry.RegistryCode] = registryId;
            }

            var exists = registryId is { } id
                         && await registries.FindEntryByCodeAsync(id, entry.EntryCode, ct).ConfigureAwait(false) is not null;
            if (!exists)
            {
                diagnostics.Add(ScriptDiagnostic.At(
                    ScriptDiagnosticCodes.UnknownRegistryEntry, entry.Span,
                    $"Entry '{entry.EntryCode}' was not found in registry '{entry.RegistryCode}'.",
                    ("registry", entry.RegistryCode),
                    ("entry", entry.EntryCode)));
            }
        }
    }

    private async Task<IReadOnlyList<int>> CheckResultReadsAsync(
        int methodologyId,
        List<UsedCalculated> used,
        List<ScriptDiagnostic> diagnostics,
        CancellationToken ct)
    {
        if (used.Count == 0)
        {
            return [];
        }

        var tableIds = used.Select(u => u.Table.TableDefId).Distinct().ToList();
        var bindings = await methodologies.GetColumnResultBindingsAsync(tableIds, ct).ConfigureAwait(false);
        var sources = new SortedSet<int>();

        foreach (var use in used)
        {
            var bound = bindings.Where(b => b.ColumnDefId == use.Column.Id).ToList();
            if (bound.Count == 0)
            {
                // Колонка-результат без активної прив'язки: читати нічого (§13.9). Необов'язкове поле — `null`.
                if (!use.IsOptional)
                {
                    diagnostics.Add(UnknownColumn(use.Span, use.Column.Code, use.Table.TableCode, "noActiveBinding"));
                }

                continue;
            }

            if (bound.Any(b => b.MethodologyId == methodologyId))
            {
                diagnostics.Add(ScriptDiagnostic.At(
                    ScriptEnvironmentCodes.ReadsOwnOutput, use.Span,
                    $"Column '{use.Column.Code}' holds an output of this very methodology: a script cannot read its own result.",
                    ("column", use.Column.Code)));
                continue;
            }

            foreach (var binding in bound)
            {
                sources.Add(binding.MethodologyId);
            }
        }

        // Цикл між методологіями: джерело (транзитивно, за матеріалізованими залежностями опублікованих версій)
        // залежить від цієї.
        var reported = new HashSet<int>();
        foreach (var source in sources)
        {
            var path = await FindPathAsync(source, methodologyId, ct).ConfigureAwait(false);
            if (path is null || !reported.Add(source))
            {
                continue;
            }

            var named = await NamesAsync([methodologyId, .. path], ct).ConfigureAwait(false);
            var use = used.First(u => bindings.Any(b => b.ColumnDefId == u.Column.Id && b.MethodologyId == source));
            diagnostics.Add(ScriptDiagnostic.At(
                ScriptEnvironmentCodes.ResultCycle, use.Span,
                $"Results of methodologies form a cycle: {named}.",
                ("path", named)));
        }

        return [.. sources];
    }

    /// <summary>Шлях від <paramref name="from"/> до <paramref name="target"/> за ребрами «хто від кого залежить».</summary>
    private async Task<List<int>?> FindPathAsync(int from, int target, CancellationToken ct)
    {
        var parents = new Dictionary<int, int> { [from] = from };
        var queue = new Queue<int>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == target)
            {
                var path = new List<int>();
                for (var n = current; ; n = parents[n])
                {
                    path.Add(n);
                    if (n == from)
                    {
                        break;
                    }
                }

                path.Reverse();
                return path;
            }

            var edges = await methodologies.GetMethodologyDependenciesAsync([current], ct).ConfigureAwait(false);
            if (!edges.TryGetValue(current, out var next))
            {
                continue;
            }

            foreach (var n in next.Where(n => parents.TryAdd(n, current)))
            {
                queue.Enqueue(n);
            }
        }

        return null;
    }

    private async Task<string> NamesAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var names = new List<string>();
        foreach (var id in ids)
        {
            var methodology = await drafts.FindAsync(id, ct).ConfigureAwait(false);
            names.Add(methodology?.Code ?? "#" + id.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(" -> ", names);
    }

    private static ScriptDiagnostic UnknownColumn(ScriptSpan span, string column, string table, string? reason = null)
    {
        var pairs = new List<(string, string)> { ("column", column), ("table", table) };
        if (reason is not null)
        {
            pairs.Add(("reason", reason));
        }

        return ScriptDiagnostic.At(
            ScriptDiagnosticCodes.UnknownColumn, span,
            reason is null
                ? $"Column '{column}' was not found in {table}."
                : $"Column '{column}' of {table} holds a calculated result, but no active binding writes it.",
            [.. pairs]);
    }

    /// <summary>Розв'язання входу в колонку живої таблиці (план 13.2: псевдонім місяця, колонка <c>X_Mmm</c>).</summary>
    private static Resolution Resolve(LiveTable table, string name, bool monthly)
    {
        if (!monthly)
        {
            var column = table.Columns.FirstOrDefault(c => string.Equals(c.Code, name, StringComparison.OrdinalIgnoreCase));
            if (column is not null)
            {
                return new Resolution(true, column.DataType == CellDataType.Calculated ? column : null);
            }

            if (DerivedArgumentNames.All.Contains(name, StringComparer.OrdinalIgnoreCase)
                || table.MonthAliases.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return new Resolution(true, null);
            }

            return new Resolution(false, null);
        }

        if (table.MonthAliases.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return new Resolution(true, null);
        }

        // Місячна колонка з кодом `X_Mmm`: достатньо, щоб розв'язувався хоч один місяць.
        var found = table.Columns.FirstOrDefault(c =>
            c.IsMonthColumn
            && c.MonthNumber is { } month
            && string.Equals(
                c.Code, name + "_M" + month.ToString("00", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase));
        return new Resolution(found is not null, found is not null && found.DataType == CellDataType.Calculated ? found : null);
    }

    private sealed record Resolution(bool Found, LiveColumn? Calculated);

    private sealed record UsedCalculated(LiveTable Table, LiveColumn Column, ScriptSpan Span, bool IsOptional);
}
