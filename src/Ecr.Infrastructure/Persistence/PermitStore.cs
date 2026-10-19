using Ecr.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IPermitStore"/> над <see cref="EcrDbContext"/> (Permits, PM-04).</summary>
public sealed class PermitStore(EcrDbContext db) : IPermitStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, PermitRowStat>> RowStatsAsync(
        int permitFieldDefId, int? totalFieldDefId, IReadOnlyCollection<long>? permitIds, CancellationToken ct)
    {
        // Рядок таблиці належить дозволу значенням поля композиції; видалені логічно рядки не рахуються.
        var parents = db.RegistryValues.AsNoTracking()
            .Where(v => v.RegistryFieldDefId == permitFieldDefId && v.ValueRefEntryId != null && !v.Entry!.IsDeleted);
        if (permitIds is not null)
        {
            var wanted = permitIds.ToList();
            parents = parents.Where(v => wanted.Contains(v.ValueRefEntryId!.Value));
        }

        var counts = await parents
            .GroupBy(v => v.ValueRefEntryId!.Value)
            .Select(g => new { Permit = g.Key, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);

        var totals = new Dictionary<long, decimal>();
        if (totalFieldDefId is { } totalField && counts.Count > 0)
        {
            var sums = await parents
                .Join(
                    db.RegistryValues.AsNoTracking().Where(v => v.RegistryFieldDefId == totalField && v.ValueNumeric != null),
                    parent => parent.RegistryEntryId,
                    total => total.RegistryEntryId,
                    (parent, total) => new { Permit = parent.ValueRefEntryId!.Value, total.ValueNumeric })
                .GroupBy(x => x.Permit)
                .Select(g => new { Permit = g.Key, Sum = g.Sum(x => x.ValueNumeric!.Value) })
                .ToListAsync(ct).ConfigureAwait(false);
            totals = sums.ToDictionary(s => s.Permit, s => s.Sum);
        }

        return counts.ToDictionary(
            c => c.Permit,
            c => new PermitRowStat(c.Count, totalFieldDefId is null ? null : totals.GetValueOrDefault(c.Permit)));
    }
}
