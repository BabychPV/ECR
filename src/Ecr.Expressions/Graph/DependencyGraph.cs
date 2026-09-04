namespace Ecr.Expressions.Graph;

/// <summary>Граф залежностей формул.</summary>
public sealed class DependencyGraph
{
    private readonly Dictionary<int, HashSet<int>> _edges = [];

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
