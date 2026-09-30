using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.TestKit;

/// <summary>
/// Ребра <c>cfg.RegistryUse</c> у пам'яті — те, що в публікації методології перепише
/// <c>RegistryUseStore</c> (RT-23b).
/// </summary>
/// <remarks>
/// ⚠ Семантика порту — «переписати цілком»: ребра версії прибираються, нові стають на
/// їхнє місце; ребра інших версій і видів лишаються.
/// </remarks>
public sealed class InMemoryRegistryUseStore : IRegistryUseStore
{
    private readonly List<RegistryUse> _uses = [];

    /// <summary>Усі ребра в сховищі.</summary>
    public IReadOnlyList<RegistryUse> Uses => _uses;

    /// <summary>Скільки разів ребра переписувались.</summary>
    public int Replacements { get; private set; }

    /// <summary>Додає ребро напряму — стан «до публікації».</summary>
    /// <param name="use">Ребро.</param>
    public void Seed(RegistryUse use) => _uses.Add(use);

    /// <inheritdoc />
    public Task ReplaceMethodologyUsesAsync(
        int methodologyVersionId, IReadOnlyCollection<RegistryUse> uses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uses);

        _uses.RemoveAll(u => u.SourceKind == RegistryUse.MethodologyVersionSource && u.SourceId == methodologyVersionId);
        _uses.AddRange(uses);
        Replacements++;
        return Task.CompletedTask;
    }
}
