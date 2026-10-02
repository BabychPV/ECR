// src/Ecr.Application/Validation/RelationCheckRunner.cs
using System.Globalization;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Validation;

/// <summary>
/// Знахідки зв'язків виду Check (D-230) для панелі валідації документа (<c>ValidateDocumentHandler</c>).
/// </summary>
/// <remarks>
/// Той самий раннер кличе й <c>SubmitSheetHandler</c> (по екземплярах свого аркуша): Check із
/// <c>Block</c> (→ <c>Error</c>) блокує подання, <c>Warn</c> потребує підтвердження.
/// Вартість: один запит <c>ListTableRelationsAsync</c> на версію шаблону; версія без активних Check —
/// більше нічого не читається. Схеми — ПРИПУЩЕННЯ (<c>RelationSpec.cs</c>); зв'язок із хибною схемою
/// (її відхиляє PUT, але могла бути збережена раніше) мовчки пропускається, а не валить перевірку.
/// </remarks>
public static class RelationCheckRunner
{
    public static async Task<IReadOnlyList<ValidationMessage>> RunAsync(
        ITemplateVersionStore store,
        ICellStore cellStore,
        IRowStore rowStore,
        IMetadataCache metadata,
        IReadOnlyList<TableInstanceRef> instances,
        PeriodKey periodKey,
        string language,
        CancellationToken ct)
    {
        var messages = new List<ValidationMessage>();

        foreach (var versionId in instances.Select(i => i.TemplateVersionId).Distinct())
        {
            var checks = new List<(TableRelationDef Relation, RelationMatchSpec Match, CheckSpec Spec)>();
            foreach (var relation in await store.ListTableRelationsAsync(versionId, ct).ConfigureAwait(false))
            {
                if (!relation.IsActive || relation.RelationKind != TableRelationKind.Check)
                {
                    continue;
                }

                var match = RelationSpecParser.ParseMatch(relation.MatchJson);
                var spec = RelationSpecParser.ParseCheck(relation.MapJson);
                if (match.IsOk && spec.IsOk)
                {
                    checks.Add((relation, match.Value!, spec.Value!));
                }
            }

            if (checks.Count == 0)
            {
                continue;
            }

            var tableIds = checks.SelectMany(c => new[] { c.Relation.SourceTableDefId, c.Relation.TargetTableDefId }).ToHashSet();
            var wanted = instances.Where(i => i.TemplateVersionId == versionId && tableIds.Contains(i.TableDefId)).ToList();
            var ids = wanted.Select(i => i.TableInstanceId).ToList();
            var snapshot = await metadata.GetAsync(versionId, ct).ConfigureAwait(false);

            var cells = await cellStore.ReadSlicesAsync(ids, periodKey, ct).ConfigureAwait(false);
            var rowIds = await rowStore.GetRowIdsBatchAsync(ids, periodKey, ct).ConfigureAwait(false);

            var rowsByTable = new Dictionary<int, List<RelationRow>>();
            foreach (var instance in wanted)
            {
                var rows = BuildRows(
                    snapshot,
                    cells.TryGetValue(instance.TableInstanceId, out var instanceCells) ? instanceCells : [],
                    rowIds.TryGetValue(instance.TableInstanceId, out var instanceRows)
                        ? instanceRows
                        : new Dictionary<string, long>(StringComparer.Ordinal));

                if (!rowsByTable.TryGetValue(instance.TableDefId, out var list))
                {
                    rowsByTable[instance.TableDefId] = list = [];
                }

                list.AddRange(rows);
            }

            foreach (var (relation, match, spec) in checks.OrderBy(c => c.Relation.Code, StringComparer.Ordinal))
            {
                if (!rowsByTable.TryGetValue(relation.SourceTableDefId, out var source)
                    || !rowsByTable.TryGetValue(relation.TargetTableDefId, out var target))
                {
                    continue;
                }

                var failures = CheckEvaluator.Failures(match, spec, source, target);
                messages.AddRange(CheckEvaluator.ToMessages(relation.Code, relation.TargetTableDefId, spec, failures, language, relation.SourceTableDefId));
            }
        }

        return messages;
    }

    /// <summary>Комірки зрізу → рядки зі значеннями за кодами колонок.</summary>
    public static IReadOnlyList<RelationRow> BuildRows(
        TemplateVersionSnapshot snapshot, IReadOnlyList<CellRecord> cells, IReadOnlyDictionary<string, long> rowIds)
    {
        var keyById = rowIds.ToDictionary(kv => kv.Value, kv => kv.Key);
        var numbers = rowIds.Keys.ToDictionary(k => k, _ => new Dictionary<string, decimal?>(), StringComparer.Ordinal);
        var texts = rowIds.Keys.ToDictionary(k => k, _ => new Dictionary<string, string?>(), StringComparer.Ordinal);

        foreach (var cell in cells)
        {
            if (cell.Value.IsEmpty
                || !keyById.TryGetValue(cell.Address.TableRowId, out var rowKey)
                || !snapshot.ColumnsById.TryGetValue(cell.Address.ColumnDefId, out var column))
            {
                continue;
            }

            numbers[rowKey][column.Code] = cell.Value.ValueNumeric;
            texts[rowKey][column.Code] = cell.Value.ValueString
                ?? cell.Value.ValueRegistryEntryId?.ToString(CultureInfo.InvariantCulture)
                ?? cell.Value.ValueUnitId?.ToString(CultureInfo.InvariantCulture);
        }

        return [.. rowIds.Keys.Order(StringComparer.Ordinal).Select(k => new RelationRow(k, numbers[k], texts[k]))];
    }
}
