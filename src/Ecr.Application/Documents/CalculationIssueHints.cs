// src/Ecr.Application/Documents/CalculationIssueHints.cs
using System.Globalization;
using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>Підказки «що приймають правила» і поточні значення рядків для переліку проблем розрахунку.</summary>
/// <param name="Tables">Підказки таблиць.</param>
/// <param name="CurrentValues">(TableDefId, RowKey) → значення в колонках підказки таблиці.</param>
internal sealed record CalculationIssueHintSet(
    IReadOnlyList<CalculationIssueTableHintDto> Tables,
    IReadOnlyDictionary<(int TableDefId, string RowKey), IReadOnlyList<string>> CurrentValues)
{
    /// <summary>Без підказок.</summary>
    public static readonly CalculationIssueHintSet Empty =
        new([], new Dictionary<(int, string), IReadOnlyList<string>>());
}

/// <summary>
/// Що приймають правила методологій таблиці з рядками без правила і що стоїть у рядку зараз (UAT 08.11 b3).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Вибір версії — той самий, що в прогоні й у gate запису (<c>MethodologyResolver</c>,
/// <c>PatchCellsHandler.ResolveApplicableAsync</c>): чинна на кінець періоду, компаратор домену
/// <see cref="MethodologyVersionKey.Currency"/>. Ключі предикатів — через <see cref="MethodologyKeyLocalizer"/> (C1):
/// у сховищі Id колонок тієї версії шаблону, де правило писали.
/// </para>
/// <para>
/// ⚠ Правило потрапляє в підказку таблиці, лише коли ВСІ його ключі — колонки ЦІЄЇ таблиці: правило з ключем іншої
/// таблиці (або без відповідника) з рядком цієї таблиці не збігається ніколи, і показати його значення як «допустиме»
/// було б неправдою. Правило <c>{}</c> збігається з усім — рядка без правила за нього не буває, тож у підказку не йде.
/// </para>
/// <para>
/// ⛔ Безпека: хоч одна колонка підказки прихована від читача — підказки для таблиці немає зовсім (ні варіантів, ні
/// поточних значень): значення прихованої колонки не повинні витікати ні з комірки, ні з правила.
/// </para>
/// </remarks>
internal sealed class CalculationIssueHints(
    IRowStore rowStore,
    ICellStore cellStore,
    IMetadataCache metadata,
    IPeriodStore periods,
    IMethodologyStore methodologies,
    IColumnPathMapper? columnMapper)
{
    /// <summary>Стеля варіантів на таблицю (клієнт показує 8 і «+N» з переліком решти).</summary>
    public const int MaxOptions = 500;

    /// <summary>Будує підказки для видимих проблем.</summary>
    public async Task<CalculationIssueHintSet> BuildAsync(
        long documentId,
        PeriodKey period,
        long? sheetCopyId,
        IReadOnlyList<CalculationIssueRow> issues,
        Func<int, bool> canReadColumn,
        CancellationToken ct)
    {
        var tableIds = issues.Select(i => i.TableDefId).Distinct().ToList();

        var bounds = await periods.FindPeriodBoundsAsync(documentId, period.Value, ct).ConfigureAwait(false);
        if (bounds is null)
        {
            return CalculationIssueHintSet.Empty;
        }

        var instances = (await rowStore.GetTableInstancesAsync(documentId, period, ct).ConfigureAwait(false))
            .Where(i => i.SheetCopyId == sheetCopyId && tableIds.Contains(i.TableDefId))
            .GroupBy(i => i.TableDefId)
            .ToDictionary(g => g.Key, g => g.First());
        if (instances.Count == 0)
        {
            return CalculationIssueHintSet.Empty;
        }

        var methodologyIds = await methodologies
            .GetMethodologyIdsBoundToTablesAsync(tableIds, ct)
            .ConfigureAwait(false);
        if (methodologyIds.Count == 0)
        {
            return CalculationIssueHintSet.Empty;
        }

        var published = (await methodologies.GetPublishedVersionsOfAsync(methodologyIds, ct).ConfigureAwait(false))
            .ToLookup(v => v.MethodologyId);
        var chosen = methodologyIds
            .Select(id => published[id]
                .Where(v => v.EffectiveFrom is not null && v.EffectiveFrom <= bounds.PeriodEnd)
                .OrderByDescending(v => new MethodologyVersionKey(v.EffectiveFrom, v.Version, v.Id), MethodologyVersionKey.Currency)
                .FirstOrDefault())
            .OfType<MethodologyVersion>()
            .Select(v => v.Id)
            .Distinct()
            .ToList();
        if (chosen.Count == 0)
        {
            return CalculationIssueHintSet.Empty;
        }

        var rules = (await methodologies.GetRulesOfVersionsAsync(chosen, ct).ConfigureAwait(false))
            .Where(r => r.IsActive)
            .ToList();
        if (rules.Count == 0)
        {
            return CalculationIssueHintSet.Empty;
        }

        var labels = await methodologies.GetVersionLabelsAsync(chosen, ct).ConfigureAwait(false);

        var tables = new List<CalculationIssueTableHintDto>();
        var columnsOf = new Dictionary<int, IReadOnlyList<ColumnDef>>();
        foreach (var group in instances.Values.GroupBy(i => i.TemplateVersionId))
        {
            var snapshot = await metadata.GetAsync(group.Key, ct).ConfigureAwait(false);
            var localized = await MethodologyKeyLocalizer
                .LocalizeAsync(columnMapper, group.Key, rules, requiredInputs: null, ct)
                .ConfigureAwait(false);

            foreach (var instance in group.OrderBy(i => i.TableDefId))
            {
                var hint = HintOf(instance.TableDefId, snapshot, rules, localized.Rules, labels, canReadColumn);
                if (hint is null)
                {
                    continue;
                }

                var first = issues.First(i => i.TableDefId == instance.TableDefId);
                tables.Add(new CalculationIssueTableHintDto(
                    first.TableCode,
                    first.SheetCopyId,
                    hint.Value.Methodologies,
                    [.. hint.Value.Columns.Select(c => new CalculationIssueColumnDto(c.Code, c.HeaderL10n))],
                    hint.Value.Options));
                columnsOf[instance.TableDefId] = hint.Value.Columns;
            }
        }

        if (tables.Count == 0)
        {
            return CalculationIssueHintSet.Empty;
        }

        var current = await CurrentValuesAsync(period, issues, instances, columnsOf, ct).ConfigureAwait(false);

        return new CalculationIssueHintSet(tables, current);
    }

    private static (IReadOnlyList<CalculationIssueMethodologyDto> Methodologies, IReadOnlyList<ColumnDef> Columns, IReadOnlyList<IReadOnlyList<string>> Options)?
        HintOf(
            int tableDefId,
            TemplateVersionSnapshot snapshot,
            List<MethodologyRule> rules,
            IReadOnlyList<LocalizedRule> localized,
            IReadOnlyDictionary<int, MethodologyVersionLabel> labels,
            Func<int, bool> canReadColumn)
    {
        var applicable = new List<(MethodologyRule Rule, IReadOnlyList<KeyValuePair<int, string?>> Pairs)>();
        for (var i = 0; i < rules.Count && i < localized.Count; i++)
        {
            if (localized[i].UnmappedColumnIds.Count > 0
                || MethodologyRuleMatcher.CanonicalPairs(localized[i].MatchJson) is not { Count: > 0 } pairs)
            {
                continue;
            }

            var keyed = new List<KeyValuePair<int, string?>>(pairs.Count);
            foreach (var pair in pairs)
            {
                if (!int.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var columnId)
                    || !snapshot.ColumnsById.TryGetValue(columnId, out var column)
                    || column.TableDefId != tableDefId)
                {
                    keyed.Clear();
                    break;
                }

                keyed.Add(new KeyValuePair<int, string?>(columnId, pair.Value));
            }

            if (keyed.Count > 0)
            {
                applicable.Add((rules[i], keyed));
            }
        }

        if (applicable.Count == 0)
        {
            return null;
        }

        var columns = applicable
            .SelectMany(a => a.Pairs.Select(p => p.Key))
            .Distinct()
            .Select(id => snapshot.ColumnsById[id])
            .OrderBy(c => c.Ordinal)
            .ThenBy(c => c.Id)
            .ToList();

        // ⛔ Прихована колонка — жодної підказки для таблиці (див. remarks класу).
        if (columns.Any(c => !canReadColumn(c.Id)))
        {
            return null;
        }

        var options = applicable
            .Select(a =>
            {
                var values = a.Pairs.ToDictionary(p => p.Key, p => p.Value);
                return (IReadOnlyList<string>)[.. columns.Select(c => values.GetValueOrDefault(c.Id) ?? string.Empty)];
            })
            .DistinctBy(o => string.Join('\u001f', o), StringComparer.Ordinal)
            .OrderBy(o => string.Join('\u001f', o), StringComparer.Ordinal)
            .Take(MaxOptions)
            .ToList();

        // UAT 08.11 (свіжий погляд): людині — назва методології; код — лише запасний підпис.
        var methodologies = applicable
            .Select(a => labels.TryGetValue(a.Rule.MethodologyVersionId, out var label) ? label : null)
            .OfType<MethodologyVersionLabel>()
            .DistinctBy(l => l.MethodologyCode, StringComparer.Ordinal)
            .OrderBy(l => l.MethodologyCode, StringComparer.Ordinal)
            .Select(l => new CalculationIssueMethodologyDto(l.MethodologyCode, l.MethodologyNameL10n))
            .ToList();

        return (methodologies, columns, options);
    }

    private async Task<IReadOnlyDictionary<(int TableDefId, string RowKey), IReadOnlyList<string>>> CurrentValuesAsync(
        PeriodKey period,
        IReadOnlyList<CalculationIssueRow> issues,
        Dictionary<int, TableInstanceRef> instances,
        IReadOnlyDictionary<int, IReadOnlyList<ColumnDef>> columnsOf,
        CancellationToken ct)
    {
        var instanceIds = columnsOf.Keys.Select(id => instances[id].TableInstanceId).ToList();
        var rowIds = await rowStore.GetRowIdsBatchAsync(instanceIds, period, ct).ConfigureAwait(false);

        var wanted = new List<(int TableDefId, string RowKey, long RowId)>();
        foreach (var issue in issues.Where(i => columnsOf.ContainsKey(i.TableDefId)))
        {
            var instanceId = instances[issue.TableDefId].TableInstanceId;
            if (rowIds.TryGetValue(instanceId, out var keys) && keys.TryGetValue(issue.RowKey, out var rowId))
            {
                wanted.Add((issue.TableDefId, issue.RowKey, rowId));
            }
        }

        var addresses = wanted
            .SelectMany(w => columnsOf[w.TableDefId].Select(c => new CellAddress(period, w.RowId, c.Id)))
            .Distinct()
            .ToList();
        var cells = addresses.Count == 0
            ? new Dictionary<CellAddress, CellValueData>()
            : await cellStore.ReadCellsAsync(addresses, ct).ConfigureAwait(false);

        var result = new Dictionary<(int, string), IReadOnlyList<string>>();
        foreach (var (tableDefId, rowKey, rowId) in wanted)
        {
            result[(tableDefId, rowKey)] =
            [
                .. columnsOf[tableDefId].Select(c =>
                    cells.TryGetValue(new CellAddress(period, rowId, c.Id), out var value)
                        ? MethodologyRuleMatcher.Text(value) ?? string.Empty
                        : string.Empty),
            ];
        }

        return result;
    }
}
