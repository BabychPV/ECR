// src/Ecr.Application/Registries/GetRegistryTargetHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Enums;

namespace Ecr.Application.Registries;

/// <summary>
/// Мінімальні відомості про довідник-ЦІЛЬ Lookup за ідентифікатором - для «unlisted»: колонка чи поле шапки зберігає лише
/// <c>lookupRegistryDefId</c>, а в переліку цілей (<c>GET /registries</c>) довідника може не бути (група деактивована, шапка
/// не опублікована, довідник прихований). Права: глобальне <c>Registry.View</c> АБО грант <c>Read</c> на цей довідник.
/// </summary>
/// <remarks>
/// ⛔ S18: «довідника немає» і «довідник схований забороною» - ОДНА відповідь (<c>404 ECR-REG-0404 registryId</c>, у тексті й
/// деталях лише переданий клієнтом Id, без коду й назви). Довідник завжди читається ПЕРШИМ запитом (і коли заборона, і коли його
/// немає), а право перевіряється після - так час відповіді не розрізняє ці випадки. Користувач без права й без гранта отримує
/// <c>403</c> однаково для існуючого й неіснуючого Id.
/// </remarks>
public sealed class GetRegistryTargetHandler(
    IRegistryStore registries,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IPermitTypeStore? permits = null,
    IPermitGroupStore? permitGroups = null)
{
    // ⚠ `permits`/`permitGroups` потрібні лише для ознаки `Kind`/опису; необов'язкові для тестів, що будують обробник руками.

    /// <summary>Право на читання довідників (`02-contracts.md` §9).</summary>
    public const string Permission = "Registry.View";

    /// <summary>Читає відомості про ціль.</summary>
    /// <param name="registryDefId">Ідентифікатор довідника, збережений у колонці чи полі шапки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Errors.NotFoundException"><c>ECR-REG-0404</c>: довідника немає або на нього заборона.</exception>
    public async Task<RegistryTargetDto> HandleAsync(int registryDefId, CancellationToken ct)
    {
        var definition = await registries.FindDefinitionByIdAsync(registryDefId, ct).ConfigureAwait(false);

        // Відмова без права/гранта і заборона S18 (404 з тим самим текстом, що й для неіснуючого) - у перевірці доступу.
        await RegistryAccess
            .RequireAsync(access, currentUser, Permission, GrantLevel.Read, registryDefId, ct)
            .ConfigureAwait(false);

        if (definition is null)
        {
            throw RegistryAccess.NotFound(registryDefId);
        }

        var kind = RegistryDefKind.Dictionary;
        Domain.ValueObjects.LocalizedText? description = null;

        // Лише реєстри з префіксом PRM: решта довідників не платить за зайві запити.
        if (definition.Code.StartsWith("PRM", StringComparison.Ordinal))
        {
            if (permitGroups is not null
                && (await permitGroups.ListBriefAsync(ct).ConfigureAwait(false)).FirstOrDefault(g => g.RegistryDefId == definition.Id) is { } group)
            {
                kind = RegistryDefKind.PermitGroup;
                description = group.DescriptionL10n;
            }
            else if (permits is not null
                     && (await permits.ListPublishedHeaderRegistryIdsAsync(ct).ConfigureAwait(false)).Contains(definition.Id))
            {
                kind = RegistryDefKind.PermitHeader;
            }
        }

        return new RegistryTargetDto(definition.Id, definition.Code, definition.NameL10n, description, kind, definition.IsActive);
    }
}
