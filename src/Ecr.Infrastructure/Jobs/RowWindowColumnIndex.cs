// src/Ecr.Infrastructure/Jobs/RowWindowColumnIndex.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Знімок колонок вікна (Початок, Кінець, селектор) активних прив'язок за таблицями (HSE301 A1).
/// </summary>
/// <remarks>
/// ⛔ Реєстрація — одиночка (singleton): хук запису комірок питає його на КОЖЕН запис, і запит до бази щоразу
/// зламав би стелю звернень (<c>PatchCellsQueryCountTests</c>). Знімок живе <see cref="Ttl"/>; прив'язку, змінену цим
/// інстансом, скидає <see cref="Invalidate"/>, змінену іншим — вичерпання строку. Ціна: правка Початку/Кінця в
/// перші <see cref="Ttl"/> після створення прив'язки не ставить підтягування; щогодинний <c>RowWindowRefetchJob</c>
/// бере лише вже підтягнуті рядки, тож такий рядок дочитає наступна правка вікна.
/// </remarks>
public sealed class RowWindowColumnIndex(IServiceScopeFactory scopes, IClock clock) : IRowWindowColumnIndex
{
    /// <summary>Строк життя знімка.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly object gate = new();
    private Snapshot? snapshot;

    /// <inheritdoc />
    public async Task<IReadOnlySet<int>> WindowColumnsAsync(int tableDefId, CancellationToken ct)
    {
        var current = Volatile.Read(ref snapshot);
        if (current is null || current.ExpiresAt <= clock.UtcNow)
        {
            current = await LoadAsync(ct).ConfigureAwait(false);
        }

        return current.ByTable.TryGetValue(tableDefId, out var columns) ? columns : Empty;
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        lock (gate)
        {
            snapshot = null;
        }
    }

    private static readonly IReadOnlySet<int> Empty = new HashSet<int>();

    private async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EcrDbContext>();

        var maps = await db.RowWindowMaps
            .AsNoTracking()
            .Where(m => m.IsActive)
            .Select(m => new { m.TableDefId, m.StartColumnDefId, m.EndColumnDefId, m.SelectorColumnDefId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byTable = maps
            .GroupBy(m => m.TableDefId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlySet<int>)g
                    .SelectMany(m => new int?[] { m.StartColumnDefId, m.EndColumnDefId, m.SelectorColumnDefId })
                    .OfType<int>()
                    .ToHashSet());

        var fresh = new Snapshot(byTable, clock.UtcNow + Ttl);
        lock (gate)
        {
            snapshot = fresh;
        }

        return fresh;
    }

    private sealed record Snapshot(Dictionary<int, IReadOnlySet<int>> ByTable, DateTime ExpiresAt);
}
