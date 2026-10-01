// src/Ecr.Application/Calculations/RelationRecalculator.cs
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Application.Templates;
using Ecr.Application.Validation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Calculations;

/// <summary>Активний зв'язок Rollup зі схемами, розібраними заздалегідь.</summary>
/// <param name="Code">Код зв'язку.</param>
/// <param name="SourceTableDefId">Таблиця-джерело.</param>
/// <param name="TargetTableDefId">Таблиця-приймач.</param>
/// <param name="Match">Зіставлення рядків.</param>
/// <param name="Rollup">Налаштування Rollup.</param>
/// <param name="TargetScale">Scale колонки приймача.</param>
public sealed record RollupRelation(
    string Code, int SourceTableDefId, int TargetTableDefId,
    RelationMatchSpec Match, RollupSpec Rollup, byte? TargetScale);

/// <summary>Запис у комірку приймача від зв'язку.</summary>
/// <param name="RelationCode">Код зв'язку.</param>
/// <param name="TargetTableDefId">Таблиця-приймач.</param>
/// <param name="Write">Що записати.</param>
public sealed record RelationWrite(string RelationCode, int TargetTableDefId, RollupWrite Write);

/// <summary>Вхід хука зв'язків у перерахунок.</summary>
/// <param name="Snapshot">Знімок структури версії.</param>
/// <param name="TemplateVersionId">Версія шаблону.</param>
/// <param name="PeriodKey">Період прогону.</param>
/// <param name="Instances">Екземпляри таблиць документа за період (уже прочитані).</param>
/// <param name="RowIdsByInstance">Рядки екземплярів: ключ рядка → id (уже прочитані).</param>
/// <param name="Pending">Результати формул цього прогону, іще не записані: адреса → запис (Rollup іде ПІСЛЯ формул).</param>
public sealed record RelationRunInput(
    TemplateVersionSnapshot Snapshot, int TemplateVersionId, PeriodKey PeriodKey,
    IReadOnlyList<TableInstanceRef> Instances,
    IReadOnlyDictionary<long, IReadOnlyDictionary<string, long>> RowIdsByInstance,
    IReadOnlyDictionary<CellAddress, CellRecord> Pending);

/// <summary>Запис Rollup у комірку приймача (рівень комірки, готовий для <c>byInstance</c> перерахунку).</summary>
/// <param name="TargetInstanceId">Екземпляр таблиці-приймача.</param>
/// <param name="Record">Комірка й значення.</param>
/// <param name="Unchanged">
/// Значення збігається зі збереженим у базі: запису не потрібно, а раніше накопичений запис формули в цю
/// комірку знімається.
/// </param>
public sealed record RelationCellWrite(long TargetInstanceId, CellRecord Record, bool Unchanged);

/// <summary>
/// ТОЧКА ІНТЕГРАЦІЇ зв'язків у перерахунок документа (D-230): <c>RecalculationService.RunAsync</c> кличе її
/// після циклу формул і до формування пакета запису.
/// </summary>
public interface IRelationRecalculator
{
    /// <summary>Обчислює записи приймачів для зв'язків Rollup (чиста функція над уже завантаженими рядками).</summary>
    /// <param name="relations">Активні зв'язки Rollup (порожній список — одразу порожній результат).</param>
    /// <param name="rowsByTable">Рядки таблиць документа: <c>TableDefId</c> → рядки.</param>
    /// <returns>Записи; один прохід (ланцюжки Rollup → Rollup не підтримуються).</returns>
    public IReadOnlyList<RelationWrite> ComputeRollups(
        IReadOnlyList<RollupRelation> relations,
        IReadOnlyDictionary<int, IReadOnlyList<RelationRow>> rowsByTable);

    /// <summary>
    /// Хук перерахунку: активні Rollup версії → записи в комірки приймачів. Читає зв'язки й зрізи
    /// джерела/приймача ЛИШЕ коли <c>Snapshot.HasActiveRollupOrCheck</c>; без прапора — порожньо й без
    /// жодного звернення до бази.
    /// </summary>
    /// <param name="input">Знімок, екземпляри документо-періоду, їхні рядки й іще не записані результати формул.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Записи для <c>byInstance</c> (той самий шлях запису, що й результати формул).</returns>
    public Task<IReadOnlyList<RelationCellWrite>> ComputeForDocumentAsync(RelationRunInput input, CancellationToken ct);
}

/// <summary>Реалізація за замовчуванням: <see cref="RollupEvaluator"/> по кожному зв'язку.</summary>
/// <remarks>
/// Параметри конструктора необов'язкові: чиста частина (<see cref="ComputeRollups"/>) не потребує бази, а
/// <see cref="ComputeForDocumentAsync"/> без них нічого не робить.
/// </remarks>
public sealed class RelationRecalculator(ITemplateVersionStore? store = null, ICellStore? cellStore = null) : IRelationRecalculator
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<RelationCellWrite>> ComputeForDocumentAsync(RelationRunInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Без прапора (і без залежностей) — жодного звернення до бази: шлях без Rollup не дорожчає.
        if (!input.Snapshot.HasActiveRollupOrCheck || store is null || cellStore is null)
        {
            return [];
        }

        var columns = input.Snapshot.ColumnsById.Values.Where(c => !c.IsDeleted).ToList();
        var relations = new List<RollupRelation>();
        foreach (var relation in await store.ListTableRelationsAsync(input.TemplateVersionId, ct).ConfigureAwait(false))
        {
            if (!relation.IsActive || relation.RelationKind != TableRelationKind.Rollup)
            {
                continue;
            }

            // Зв'язок із хибною схемою (збережений до перевірки при PUT) мовчки пропускається, а не валить перерахунок.
            var match = RelationSpecParser.ParseMatch(relation.MatchJson);
            var spec = RelationSpecParser.ParseRollup(relation.MapJson);
            if (!match.IsOk || !spec.IsOk)
            {
                continue;
            }

            var target = columns.FirstOrDefault(c => c.TableDefId == relation.TargetTableDefId
                && string.Equals(c.Code, spec.Value!.TargetColumn, StringComparison.Ordinal));
            relations.Add(new RollupRelation(
                relation.Code, relation.SourceTableDefId, relation.TargetTableDefId, match.Value!, spec.Value!, target?.Scale));
        }

        if (relations.Count == 0)
        {
            return [];
        }

        var tableIds = relations.SelectMany(r => new[] { r.SourceTableDefId, r.TargetTableDefId }).ToHashSet();
        var wanted = input.Instances.Where(i => tableIds.Contains(i.TableDefId)).ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var slices = await cellStore
            .ReadSlicesAsync([.. wanted.Select(i => i.TableInstanceId)], input.PeriodKey, ct)
            .ConfigureAwait(false);

        var rowsByTable = new Dictionary<int, List<RelationRow>>();
        var stored = new Dictionary<CellAddress, CellValueData>();
        var instanceOfRow = new Dictionary<(int TableDefId, string RowKey), (long InstanceId, long RowId)>();

        foreach (var instance in wanted)
        {
            var rowIds = input.RowIdsByInstance.TryGetValue(instance.TableInstanceId, out var found)
                ? found
                : new Dictionary<string, long>(StringComparer.Ordinal);
            var rowIdSet = rowIds.Values.ToHashSet();
            var cells = slices.TryGetValue(instance.TableInstanceId, out var sliceCells) ? sliceCells : [];

            foreach (var cell in cells)
            {
                stored[cell.Address] = cell.Value;
            }

            // Значення ПІСЛЯ формул документа: ще не записані результати перекривають збережені.
            var effective = cells.Where(c => !input.Pending.ContainsKey(c.Address))
                .Concat(input.Pending.Values.Where(p => rowIdSet.Contains(p.Address.TableRowId)))
                .ToList();

            if (!rowsByTable.TryGetValue(instance.TableDefId, out var list))
            {
                rowsByTable[instance.TableDefId] = list = [];
            }

            list.AddRange(RelationCheckRunner.BuildRows(input.Snapshot, effective, rowIds));
            foreach (var (rowKey, rowId) in rowIds)
            {
                instanceOfRow[(instance.TableDefId, rowKey)] = (instance.TableInstanceId, rowId);
            }
        }

        var writes = ComputeRollups(
            relations,
            rowsByTable.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<RelationRow>)kv.Value));

        var result = new Dictionary<CellAddress, RelationCellWrite>();
        foreach (var relationWrite in writes)
        {
            var write = relationWrite.Write;
            var column = columns.FirstOrDefault(c => c.TableDefId == relationWrite.TargetTableDefId
                && string.Equals(c.Code, write.TargetColumn, StringComparison.Ordinal));
            if (column is null
                || !instanceOfRow.TryGetValue((relationWrite.TargetTableDefId, write.TargetRowKey), out var where))
            {
                continue;
            }

            var address = new CellAddress(input.PeriodKey, where.RowId, column.Id);
            var data = write.Value is { } number
                ? new CellValueData { ValueNumeric = number, IsCalculated = true }
                : CellValueData.Empty;

            stored.TryGetValue(address, out var current);

            // Порожнє джерело над коміркою, якої немає, нічого не міняє; над заповненою — очищає її.
            var unchanged = write.Value is null
                ? current is null || current.IsEmpty
                : CellValueComparison.AreEqual(current, data);

            result[address] = new RelationCellWrite(
                where.InstanceId, new CellRecord(address, relationWrite.TargetTableDefId, data), unchanged);
        }

        return [.. result.Values];
    }

    /// <inheritdoc />
    public IReadOnlyList<RelationWrite> ComputeRollups(
        IReadOnlyList<RollupRelation> relations,
        IReadOnlyDictionary<int, IReadOnlyList<RelationRow>> rowsByTable)
    {
        ArgumentNullException.ThrowIfNull(relations);
        ArgumentNullException.ThrowIfNull(rowsByTable);

        var result = new List<RelationWrite>();
        foreach (var relation in relations.OrderBy(r => r.Code, StringComparer.Ordinal))
        {
            // Таблиці немає в документі (інший аркуш/період) — зв'язок мовчки пропускається: ні
            // джерело, ні приймач вигадувати не можна.
            if (!rowsByTable.TryGetValue(relation.SourceTableDefId, out var source)
                || !rowsByTable.TryGetValue(relation.TargetTableDefId, out var target))
            {
                continue;
            }

            var rollup = RollupEvaluator.Evaluate(relation.Match, relation.Rollup, source, target, relation.TargetScale);
            result.AddRange(rollup.Writes.Select(w => new RelationWrite(relation.Code, relation.TargetTableDefId, w)));
        }

        return result;
    }
}
