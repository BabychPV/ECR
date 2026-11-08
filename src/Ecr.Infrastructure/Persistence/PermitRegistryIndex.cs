using System.Diagnostics;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// <see cref="IPermitRegistryIndex"/>: a process-wide (singleton) set of permit-type Lookup targets — published permit type
/// headers and permit group registries — refreshed at most once per <see cref="IPermitRegistryIndex.RefreshInterval"/>.
/// </summary>
/// <remarks>
/// ⚠ Singleton over a scoped <see cref="EcrDbContext"/>: the refresh opens its own scope. Concurrent refreshes are folded
/// into one (semaphore); readers between refreshes get the last set without waiting. A failed refresh keeps the last set
/// and is retried on the next request after the interval — the rule never blocks a write because the index is down.
/// </remarks>
public sealed class PermitRegistryIndex(IServiceScopeFactory scopes) : IPermitRegistryIndex, IDisposable
{
    /// <inheritdoc />
    public void Dispose() => _refresh.Dispose();

    /// <summary>Ceiling of rows per query: permit types and groups are tens.</summary>
    private const int MaxRows = 10_000;

    private readonly SemaphoreSlim _refresh = new(1, 1);
    private volatile Snapshot? _snapshot;

    /// <inheritdoc />
    public async ValueTask<IReadOnlySet<int>> PermitTargetIdsAsync(CancellationToken ct)
    {
        var current = _snapshot;
        if (current is not null && current.Age.Elapsed < IPermitRegistryIndex.RefreshInterval)
        {
            return current.Ids;
        }

        await _refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            current = _snapshot;
            if (current is not null && current.Age.Elapsed < IPermitRegistryIndex.RefreshInterval)
            {
                return current.Ids;
            }

            var loaded = await LoadAsync(ct).ConfigureAwait(false);
            _snapshot = new Snapshot(loaded, Stopwatch.StartNew());
            return loaded;
        }
        finally
        {
            _refresh.Release();
        }
    }

    private async Task<IReadOnlySet<int>> LoadAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EcrDbContext>();

        var headers = await db.PermitTypeDefs.AsNoTracking()
            .Where(t => t.Status == PermitTypeStatus.Published)
            .Select(t => t.HeaderRegistryDefId)
            .OrderBy(id => id)
            .Take(MaxRows)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var groups = await db.PermitGroups.AsNoTracking()
            .Select(g => g.RegistryDefId)
            .OrderBy(id => id)
            .Take(MaxRows)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return headers.Concat(groups).ToHashSet();
    }

    private sealed record Snapshot(IReadOnlySet<int> Ids, Stopwatch Age);
}
