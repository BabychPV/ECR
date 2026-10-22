// src/Ecr.Application/Permits/Groups/PermitGroupResolver.cs
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Security;

namespace Ecr.Application.Permits.Groups;

/// <summary>Знімок груп у памʼяті (незмінний).</summary>
/// <param name="Revision">Ревізія складу, за якою знято.</param>
/// <param name="Groups">Групи за <c>RegistryDefId</c> прихованого реєстру.</param>
internal sealed record PermitGroupSnapshotState(long Revision, IReadOnlyDictionary<int, PermitGroupSnapshotItem> Groups)
{
    /// <summary>Порожній знімок (груп немає / ще не читали).</summary>
    internal static PermitGroupSnapshotState Empty { get; } =
        new(long.MinValue, new Dictionary<int, PermitGroupSnapshotItem>());
}

/// <summary>
/// Singleton-сховище знімка груп: атомарна підміна посилання, перевірка ревізії не частіше за <see cref="CheckInterval"/>
/// (Permits, PRMG).
/// </summary>
/// <remarks>
/// ⚠ Спільний між scope-ами й запитами; сама логіка перечитування - у <see cref="PermitGroupResolver"/>. Негативного
/// кешу окремо немає: невідома ціль просто не знаходиться у знімку, а перевірка ревізії має той самий 5-секундний
/// інтервал для всіх запитів, тож потік чужих id не збільшує кількість звернень до бази.
/// </remarks>
public sealed class PermitGroupSnapshotCache : IDisposable
{
    /// <summary>Інтервал між перевірками ревізії.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private PermitGroupSnapshotState _state = PermitGroupSnapshotState.Empty;
    private long _checkedAtTicks = long.MinValue / 2;

    internal PermitGroupSnapshotState State => Volatile.Read(ref _state);

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>Скидає «свіжість» знімка: наступний <c>EnsureFreshAsync</c> перевірить ревізію одразу (після коміту зміни групи).</summary>
    public void Invalidate() => Interlocked.Exchange(ref _checkedAtTicks, long.MinValue / 2);

    internal async ValueTask EnsureFreshAsync(IPermitGroupStore store, CancellationToken ct)
    {
        if (IsFresh())
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsFresh())
            {
                return;
            }

            var revision = await store.GetSnapshotRevisionAsync(ct).ConfigureAwait(false);
            if (revision != State.Revision)
            {
                var loaded = await store.LoadSnapshotAsync(ct).ConfigureAwait(false);
                Volatile.Write(
                    ref _state,
                    new PermitGroupSnapshotState(
                        loaded.Revision,
                        loaded.Items.ToDictionary(item => item.GroupRegistryDefId)));
            }

            Interlocked.Exchange(ref _checkedAtTicks, Environment.TickCount64);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsFresh()
        => Environment.TickCount64 - Interlocked.Read(ref _checkedAtTicks) < (long)CheckInterval.TotalMilliseconds;
}

/// <summary>Реалізація <see cref="IPermitGroupResolver"/> і <see cref="ILookupTargetResolver"/> над знімком у памʼяті.</summary>
public sealed class PermitGroupResolver(IPermitGroupStore store, PermitGroupSnapshotCache cache) : ILookupTargetResolver
{
    private static readonly IReadOnlyList<int> NoMembers = [];

    /// <inheritdoc />
    public ValueTask EnsureFreshAsync(CancellationToken ct) => cache.EnsureFreshAsync(store, ct);

    /// <inheritdoc />
    public bool IsGroup(int registryDefId) => cache.State.Groups.ContainsKey(registryDefId);

    /// <inheritdoc />
    public bool Accepts(int targetRegistryId, int entryRegistryId)
        => cache.State.Groups.TryGetValue(targetRegistryId, out var group)
           && group.IsActive
           && group.MemberRegistryDefIds.Contains(entryRegistryId);

    /// <inheritdoc />
    public bool AcceptsFor(AccessProfile profile, int targetRegistryId, int entryRegistryId)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return Accepts(targetRegistryId, entryRegistryId) && !RegistryAccess.IsDenied(profile, entryRegistryId);
    }

    /// <inheritdoc />
    public IReadOnlyList<int> MemberRegistryIds(int groupRegistryId)
        => cache.State.Groups.TryGetValue(groupRegistryId, out var group) ? group.MemberRegistryDefIds : NoMembers;

    /// <inheritdoc />
    public IReadOnlyList<int> ExpandRegistryIds(int targetRegistryId)
        => cache.State.Groups.TryGetValue(targetRegistryId, out var group) ? group.MemberRegistryDefIds : [targetRegistryId];
}
