// src/Ecr.Application/Permits/GetPermitTypeUsageHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Permits.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Registries;

namespace Ecr.Application.Permits;

/// <summary>
/// Скільки дозволів типу і скільки рядків у кожній його опублікованій таблиці (Permits, PM-03b2). Права - як у читання типу:
/// <c>Registry.View</c> плюс заборона на реєстр шапки (невидимий тип - той самий 404).
/// </summary>
/// <remarks>
/// <para>
/// Лічильники беруться ОДНИМ згрупованим запитом до записів реєстрів (шапка + усі таблиці), живі записи - без логічно
/// видалених. Склад береться з опублікованих таблиць (<c>cfg.PermitTypeTable</c>); <c>DraftJson</c> не читається.
/// </para>
/// <para>
/// ⚠ «Живий» тут - не видалений; неактивні (чернеткові) дозволи рахуються, бо їхні рядки теж зникли б разом з таблицею.
/// </para>
/// </remarks>
public sealed class GetPermitTypeUsageHandler(
    IPermitTypeStore permits,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання довідників.</summary>
    public const string Permission = "Registry.View";

    /// <summary>Рахує використання.</summary>
    /// <param name="code">Код типу.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Errors.NotFoundException">Типу немає (або на його шапку заборона читачу) - <c>ECR-PRM-0404</c>.</exception>
    public async Task<PermitTypeUsageDto> HandleAsync(string code, CancellationToken ct)
    {
        var profile = await Security.PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var type = await permits.FindByCodeAsync(code, ct).ConfigureAwait(false);
        if (type is null || RegistryAccess.IsDenied(profile, type.HeaderRegistryDefId))
        {
            throw PermitTypeErrors.NotFound(code);
        }

        var tables = await permits.ListTablesAsync(type.Id, ct).ConfigureAwait(false);
        var counts = await permits
            .CountPermitsAsync([type.HeaderRegistryDefId, .. tables.Select(t => t.RegistryDefId)], ct)
            .ConfigureAwait(false);

        return new PermitTypeUsageDto(
            counts.GetValueOrDefault(type.HeaderRegistryDefId),
            [.. tables.Select(t => new PermitTypeTableUsageDto(t.Code, counts.GetValueOrDefault(t.RegistryDefId)))]);
    }
}
