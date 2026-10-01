// src/Ecr.Application/Registries/RegistryLookup.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Application.Registries;

/// <summary>
/// Довідник зі шляху запиту: читається щонайбільше раз — або ліниво всередині
/// <see cref="RegistryAccess"/> (немає глобального права, або є заборона на довідник),
/// або після перевірки права.
/// </summary>
/// <remarks>
/// ⚠ Порядок той самий, що в решти маршрутів довідників: спершу право, потім 404.
/// Без права відповідь — <c>403</c>, і про існування довідника вона не каже.
/// Право обробник передає САМ, константою: сторож <c>ProjectPermissionCheckTests</c>
/// читає код права в місці виклику <see cref="RegistryAccess.RequireAsync(IAccessDecisionService, ICurrentUser, string, GrantLevel, RegistryLookup, CancellationToken)"/>.
///
/// ⛔ S18: код довідника з маршруту несе сам <see cref="RegistryLookup"/>, а не рядковий
/// параметр перевірки: явна заборона на довідник відповідає тим самим <c>404</c>, що й
/// неіснуючий довідник (<see cref="NotFound"/>), — різниця відповідей розкривала б існування.
/// </remarks>
internal sealed class RegistryLookup(IRegistryStore registries, string registryCode)
{
    private RegistryDef? _definition;
    private bool _loaded;

    /// <summary>Резолвер для <see cref="RegistryAccess"/>.</summary>
    internal async Task<int?> IdAsync(CancellationToken ct) => (await LoadAsync(ct).ConfigureAwait(false))?.Id;

    /// <summary>Довідник або <c>404 err.ECR-REG-0404.registry</c>.</summary>
    internal async Task<RegistryDef> RequireAsync(CancellationToken ct)
        => await LoadAsync(ct).ConfigureAwait(false) ?? throw NotFound();

    /// <summary>
    /// <c>404 err.ECR-REG-0404.registry</c> — і для довідника, якого немає, і для довідника,
    /// схованого забороною (S18).
    /// </summary>
    internal NotFoundException NotFound() => RegistryAccess.NotFound(registryCode);

    private async Task<RegistryDef?> LoadAsync(CancellationToken ct)
    {
        if (!_loaded)
        {
            _definition = await registries.FindDefinitionAsync(registryCode, ct).ConfigureAwait(false);
            _loaded = true;
        }

        return _definition;
    }
}
