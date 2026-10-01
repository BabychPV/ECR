// src/Ecr.Application/Calculations/RelationRecalculator.cs
using Ecr.Application.Templates;

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

/// <summary>
/// ТОЧКА ІНТЕГРАЦІЇ зв'язків у перерахунок документа (D-230). Вхід — уже завантажені дані документа,
/// вихід — записи, які <c>RecalculationService</c> додасть до свого пакета; бази тут немає, тож
/// запитів кількість не росте.
/// </summary>
/// <remarks>
/// Підключення до <c>RecalculationService.cs</c> — ОКРЕМА правка зони «Аудит» (один виклик після
/// розрахунків); цей інтерфейс її не вимагає й сам нічого не підключає.
/// </remarks>
public interface IRelationRecalculator
{
    /// <summary>Обчислює записи приймачів для зв'язків Rollup.</summary>
    /// <param name="relations">Активні зв'язки Rollup (порожній список — одразу порожній результат).</param>
    /// <param name="rowsByTable">Рядки таблиць документа: <c>TableDefId</c> → рядки.</param>
    /// <returns>Записи; один прохід (ланцюжки Rollup → Rollup не підтримуються).</returns>
    public IReadOnlyList<RelationWrite> ComputeRollups(
        IReadOnlyList<RollupRelation> relations,
        IReadOnlyDictionary<int, IReadOnlyList<RelationRow>> rowsByTable);
}

/// <summary>Реалізація за замовчуванням: <see cref="RollupEvaluator"/> по кожному зв'язку.</summary>
public sealed class RelationRecalculator : IRelationRecalculator
{
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
