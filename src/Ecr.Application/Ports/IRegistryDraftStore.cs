using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Ports;

/// <summary>Чернетки опису довідників <c>cfg.RegistryDefinitionDraft</c> (<c>BE-24</c> крок 2).</summary>
public interface IRegistryDraftStore
{
    /// <summary>Чернетка довідника (відстежувана) або <c>null</c>.</summary>
    public Task<RegistryDefinitionDraft?> FindAsync(int registryDefId, CancellationToken ct);

    /// <summary>Додає нову чернетку.</summary>
    public void Add(RegistryDefinitionDraft draft);

    /// <summary>Прибирає чернетку (після публікації).</summary>
    public void Remove(RegistryDefinitionDraft draft);
}
