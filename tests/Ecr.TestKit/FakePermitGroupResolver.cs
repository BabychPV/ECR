using Ecr.Application.Permits.Groups;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.TestKit;

/// <summary>
/// Спільний in-memory фейк <see cref="ILookupTargetResolver"/> (і <see cref="IPermitGroupResolver"/>) для тестів перевірок
/// належності запису цілі Lookup (PatchCells, шапка, міграція, інтеграція, Excel): групи й склад задаються прямо, без бази й знімка.
/// </summary>
/// <remarks>
/// <para>Приклад: <c>new FakePermitGroupResolver().WithGroup(groupRegistryId: 900, memberRegistryIds: [10, 11])</c> - ціль 900 приймає записи
/// реєстрів 10 і 11. <see cref="Calls"/> рахує звернення до <c>Accepts/AcceptsFor</c>: тест «Land не кличе резолвер» перевіряє <c>0</c>,
/// або вмикає <see cref="ThrowOnUse"/> - тоді будь-яке звернення падає.</para>
/// <para>Вихід типу з групи моделюється викликом <see cref="WithGroup"/> знову (склад замінюється). Живий SQL-варіант -
/// <see cref="InMemoryPermitGroupStore"/> поруч із справжнім <c>PermitGroupResolver</c> або сама база (тести Infrastructure).</para>
/// </remarks>
public sealed class FakePermitGroupResolver : ILookupTargetResolver
{
    private readonly Dictionary<int, (bool IsActive, int[] Members)> _groups = [];
    private int _calls;

    /// <summary>Кидати виняток при будь-якому зверненні до <c>Accepts/AcceptsFor/Expand/Member/IsGroup</c>.</summary>
    public bool ThrowOnUse { get; set; }

    /// <summary>Скільки разів кликали <c>Accepts</c> або <c>AcceptsFor</c>.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Скільки разів кликали <c>EnsureFreshAsync</c>.</summary>
    public int EnsureFreshCalls { get; private set; }

    /// <summary>Задає (або замінює) групу.</summary>
    /// <param name="groupRegistryId">Прихований реєстр групи (ціль).</param>
    /// <param name="memberRegistryIds">Реєстри шапок типів складу.</param>
    /// <param name="isActive">Активність групи.</param>
    public FakePermitGroupResolver WithGroup(int groupRegistryId, int[] memberRegistryIds, bool isActive = true)
    {
        _groups[groupRegistryId] = (isActive, memberRegistryIds);
        return this;
    }

    /// <inheritdoc />
    public ValueTask EnsureFreshAsync(CancellationToken ct)
    {
        EnsureFreshCalls++;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public bool IsGroup(int registryDefId)
    {
        GuardUse();
        return _groups.ContainsKey(registryDefId);
    }

    /// <inheritdoc />
    public bool Accepts(int targetRegistryId, int entryRegistryId)
    {
        Interlocked.Increment(ref _calls);
        GuardUse();
        return _groups.TryGetValue(targetRegistryId, out var g) && g.IsActive && g.Members.Contains(entryRegistryId);
    }

    /// <inheritdoc />
    public bool AcceptsFor(AccessProfile profile, int targetRegistryId, int entryRegistryId)
        => Accepts(targetRegistryId, entryRegistryId) && !RegistryAccess.IsDenied(profile, entryRegistryId);

    /// <inheritdoc />
    public IReadOnlyList<int> MemberRegistryIds(int groupRegistryId)
    {
        GuardUse();
        return _groups.TryGetValue(groupRegistryId, out var g) ? g.Members : [];
    }

    /// <inheritdoc />
    public IReadOnlyList<int> ExpandRegistryIds(int targetRegistryId)
    {
        GuardUse();
        return _groups.TryGetValue(targetRegistryId, out var g) ? g.Members : [targetRegistryId];
    }

    private void GuardUse()
    {
        if (ThrowOnUse)
        {
            throw new InvalidOperationException("Резолвер груп не мав викликатися (збіг реєстру цілі й запису - гарячий шлях без нього).");
        }
    }
}

/// <summary>In-memory <see cref="IPermitGroupStore"/> для тестів справжнього <see cref="PermitGroupResolver"/> (знімок, ревізія, інвалідація).</summary>
public sealed class InMemoryPermitGroupStore : IPermitGroupStore
{
    private readonly List<PermitGroup> _groups = [];

    /// <summary>Групи знімка; тест змінює і піднімає <see cref="Revision"/>.</summary>
    public List<PermitGroupSnapshotItem> Items { get; } = [];

    /// <summary>Поточна ревізія складу.</summary>
    public long Revision { get; set; }

    /// <summary>Скільки разів читали ревізію.</summary>
    public int RevisionReads { get; private set; }

    /// <summary>Скільки разів читали знімок.</summary>
    public int SnapshotLoads { get; private set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<PermitGroup>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PermitGroup>>([.. _groups]);

    /// <inheritdoc />
    public Task<IReadOnlyList<PermitGroupBrief>> ListBriefAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PermitGroupBrief>>([.. _groups.Select(g => new PermitGroupBrief(g.RegistryDefId, g.IsActive, g.DescriptionL10n))]);

    /// <inheritdoc />
    public Task<PermitGroup?> FindByCodeAsync(string code, CancellationToken ct)
        => Task.FromResult(_groups.Find(g => g.Code == code));

    /// <inheritdoc />
    public void Add(PermitGroup group) => _groups.Add(group);

    /// <inheritdoc />
    public void RemoveMembersNotIn(PermitGroup group, IReadOnlyCollection<int> keepPermitTypeDefIds)
    {
    }

    /// <inheritdoc />
    public Task RemoveAsync(PermitGroup group, CancellationToken ct)
    {
        _groups.Remove(group);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PermitGroupSnapshot> LoadSnapshotAsync(CancellationToken ct)
    {
        SnapshotLoads++;
        return Task.FromResult(new PermitGroupSnapshot(Revision, [.. Items]));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Ecr.Domain.Entities.Dictionaries.RegistryEntry>> ListMemberEntriesAsync(
        IReadOnlyCollection<int> registryDefIds, DateOnly windowStart, DateOnly windowEndInclusive, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Ecr.Domain.Entities.Dictionaries.RegistryEntry>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<int, int>> GetDataRevisionsAsync(IReadOnlyCollection<int> registryDefIds, CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<int, int>>(new Dictionary<int, int>());

    /// <inheritdoc />
    public Task<long> GetSnapshotRevisionAsync(CancellationToken ct)
    {
        RevisionReads++;
        return Task.FromResult(Revision);
    }
}
