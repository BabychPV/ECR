using Ecr.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Реалізація <see cref="IColumnRenameMap"/> над <c>cfg.ColumnRename</c>: читається раз на скоуп і проєкт.
/// ✎ B127: для документа — лише пари його проєкту (<c>cfg.ColumnRenameProject</c>) і застарілі глобальні.
/// </summary>
public sealed class ColumnRenameMap(EcrDbContext db) : IColumnRenameMap
{
    /// <summary>Мапи за проєктом; ключ <c>-1</c> — усі пари (без документа).</summary>
    private readonly Dictionary<int, (IReadOnlyDictionary<int, int>, IReadOnlyDictionary<int, int>)> _loaded = [];

    /// <summary>Проєкт документа (документ свого проєкту не змінює) — один запит на документ за скоуп.</summary>
    private readonly Dictionary<long, int> _projects = [];

    /// <inheritdoc />
    public async Task<(IReadOnlyDictionary<int, int> Forward, IReadOnlyDictionary<int, int> Backward)> GetAsync(
        long? documentId, CancellationToken ct)
    {
        // Невідомий документ — проєкт 0: лише застарілі глобальні пари (межа не розширюється через помилку викликача).
        var projectId = -1;
        if (documentId is { } id && !_projects.TryGetValue(id, out projectId))
        {
            projectId = await db.Documents.AsNoTracking().Where(d => d.Id == id).Select(d => (int?)d.ProjectId)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? 0;
            _projects[id] = projectId;
        }

        if (_loaded.TryGetValue(projectId, out var cached))
        {
            return cached;
        }

        var query = db.ColumnRenames.AsNoTracking();
        if (projectId >= 0)
        {
            query = query.Where(r => r.IsLegacyGlobal
                                     || db.ColumnRenameProjects.Any(p => p.FromColumnDefId == r.FromColumnDefId
                                                                         && p.ToColumnDefId == r.ToColumnDefId
                                                                         && p.ProjectId == projectId));
        }

        var rows = await query.OrderBy(r => r.FromColumnDefId).ThenBy(r => r.ToColumnDefId).Take(100_000)
            .ToListAsync(ct).ConfigureAwait(false);
        var forward = new Dictionary<int, int>();
        var backward = new Dictionary<int, int>();
        foreach (var row in rows)
        {
            forward[row.FromColumnDefId] = row.ToColumnDefId;
            backward[row.ToColumnDefId] = row.FromColumnDefId;
        }

        var loaded = ((IReadOnlyDictionary<int, int>)forward, (IReadOnlyDictionary<int, int>)backward);
        _loaded[projectId] = loaded;
        return loaded;
    }
}
