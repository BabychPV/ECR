using Ecr.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IColumnRenameMap"/> над <c>cfg.ColumnRename</c>: читається раз на скоуп.</summary>
public sealed class ColumnRenameMap(EcrDbContext db) : IColumnRenameMap
{
    private (IReadOnlyDictionary<int, int>, IReadOnlyDictionary<int, int>)? _loaded;

    /// <inheritdoc />
    public async Task<(IReadOnlyDictionary<int, int> Forward, IReadOnlyDictionary<int, int> Backward)> GetAsync(CancellationToken ct)
    {
        if (_loaded is { } cached)
        {
            return cached;
        }

        var rows = await db.ColumnRenames.AsNoTracking().OrderBy(r => r.FromColumnDefId).ThenBy(r => r.ToColumnDefId).Take(100_000).ToListAsync(ct).ConfigureAwait(false);
        var forward = new Dictionary<int, int>();
        var backward = new Dictionary<int, int>();
        foreach (var row in rows)
        {
            forward[row.FromColumnDefId] = row.ToColumnDefId;
            backward[row.ToColumnDefId] = row.FromColumnDefId;
        }

        _loaded = (forward, backward);
        return _loaded.Value;
    }
}
