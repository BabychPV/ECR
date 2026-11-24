// src/Ecr.Infrastructure/Jobs/GasCompositionProvisioning.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// <see cref="IGasCompositionProvisioning"/> над <see cref="FlertRegistryProvisioner"/>: перший потік, створений на сторінці
/// «Склад газу» до будь-якого імпорту, заводить ті самі довідники (<c>Local</c>), що й імпорт.
/// </summary>
/// <param name="provisioner">Провізія довідників.</param>
/// <param name="uow">Одиниця роботи — провізія в одній транзакції.</param>
public sealed class GasCompositionProvisioning(FlertRegistryProvisioner provisioner, IUnitOfWork uow) : IGasCompositionProvisioning
{
    /// <summary>Ключ: наявний довідник із кодом складу газу несумісний (поля інших типів).</summary>
    public const string IncompatibleKey = "err.ECR-REG-0409.gasCompositionRegistryIncompatible";

    /// <inheritdoc />
    public async Task<GasRegistryIds> EnsureAsync(CancellationToken ct)
    {
        FlertRegistrySet? set = null;
        await uow.ExecuteInTransactionAsync(
            async tx => set = await provisioner.EnsureAsync(FlertRegistryCodes.Default, tx).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        if (set is null || set.Problems.Count > 0 || set.Stream is null || set.Case is null || set.Composition is null || set.Component is null)
        {
            throw new BusinessRuleException(
                ErrorCodes.RegistryEntryInUse,
                "Довідник із кодом складу газу вже є і несумісний: " + string.Join("; ", set?.Problems ?? []),
                new Dictionary<string, object?>
                {
                    ["messageKey"] = IncompatibleKey,
                    ["problems"] = string.Join("; ", set?.Problems ?? []),
                });
        }

        return new GasRegistryIds(set.Stream.Id, set.Case.Id, set.Composition.Id, set.Component.Id);
    }
}
