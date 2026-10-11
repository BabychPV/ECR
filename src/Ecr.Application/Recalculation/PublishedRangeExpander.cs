using Ecr.Domain.Entities.Configuration;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Parsing;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Розкриває діапазон <c>[A:B]</c> ключами, які зафіксувала ПУБЛІКАЦІЯ (<c>cfg.FormulaDependency</c>),
/// а не поточним <c>RowDef.Ordinal</c> (ФВ-0.2, D-17).
/// </summary>
/// <remarks>
/// ⛔ Презентаційний патч (ФВ-7.2) міняє <c>Ordinal</c> рядка в ОПУБЛІКОВАНІЙ версії, і діапазон,
/// розкритий у рантаймі за поточним порядком, давав інше число без жодної помилки (60 → 1039).
///
/// ⚠ Видобувач залежностей розкриває діапазон у тому самому порядку обходу, що й при публікації, і
/// передає <c>SortOrder</c> першого ребра посилання. Збережені ребра цього посилання — це неперервний
/// пробіг <c>SortOrder</c> від ключа <c>From</c> до першого ключа <c>To</c> (ключі діапазону унікальні).
/// Якщо пробігу немає (чернетка, версія без збереженого графа, змінений вираз) — розкриття за
/// поточним порядком, як і було.
/// </remarks>
internal sealed class PublishedRangeExpander(IReadOnlyDictionary<int, FormulaDependency> bySortOrder)
    : RangeExpander
{
    /// <summary>Посилання (позиція в тексті виразу) → ключі, зафіксовані публікацією.</summary>
    public Dictionary<int, IReadOnlyList<string>> ByPosition { get; } = [];

    /// <inheritdoc />
    public override IReadOnlyList<string> ExpandAt(
        TableDef table,
        string fromRowKey,
        string toRowKey,
        int dependencyIndex,
        List<ExpressionDiagnostic>? diagnostics = null,
        int position = 0)
    {
        var run = Published(table, fromRowKey, toRowKey, dependencyIndex);
        if (run is null)
        {
            return base.ExpandAt(table, fromRowKey, toRowKey, dependencyIndex, diagnostics, position);
        }

        ByPosition[position] = run;
        return run;
    }

    private List<string>? Published(TableDef table, string fromRowKey, string toRowKey, int index)
    {
        var keys = new List<string>();

        for (; bySortOrder.TryGetValue(index, out var edge); index++)
        {
            if (edge.TableDefId != table.Id
                || edge.RowKey is not { } key
                || edge.FilterJson is not null
                || edge.DependsOnKind is not (0 or 3))
            {
                return null;
            }

            if (keys.Count == 0 && !string.Equals(key, fromRowKey, StringComparison.Ordinal))
            {
                return null;
            }

            keys.Add(key);

            if (string.Equals(key, toRowKey, StringComparison.Ordinal))
            {
                return keys;
            }
        }

        return null;
    }
}
