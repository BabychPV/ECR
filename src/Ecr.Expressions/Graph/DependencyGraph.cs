namespace Ecr.Expressions.Graph;

/// <summary>Граф залежностей формул.</summary>
public sealed class DependencyGraph
{
    private readonly Dictionary<int, HashSet<int>> _edges = [];

    /// <summary>
    /// Оголошує вузол без залежностей.
    /// </summary>
    /// <param name="node">Вузол.</param>
    /// <remarks>
    /// ⚠ Потрібен саме окремо від <see cref="AddEdge"/>. Формула, яка ні від
    /// чого не залежить і від якої ніхто не залежить, не має жодного ребра —
    /// і без цього методу просто зникла б із порядку обчислення. Спокуса
    /// «додати ребро із себе на себе» дає цикл там, де його немає.
    /// </remarks>
    public void AddNode(int node)
    {
        if (!_edges.ContainsKey(node))
        {
            _edges[node] = [];
        }
    }

    /// <summary>Додає ребро «<paramref name="dependent"/> залежить від <paramref name="dependency"/>».</summary>
    public void AddEdge(int dependent, int dependency)
    {
        if (!_edges.TryGetValue(dependent, out var set))
        {
            set = [];
            _edges[dependent] = set;
        }
        set.Add(dependency);
    }

    /// <summary>Усі вузли графа.</summary>
    public IReadOnlyCollection<int> Nodes => _edges.Keys;

    /// <summary>Залежності вузла.</summary>
    public IReadOnlyCollection<int> DependenciesOf(int node)
        => _edges.TryGetValue(node, out var set) ? set : Array.Empty<int>();
}
