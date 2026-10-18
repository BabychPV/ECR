// src/Ecr.Application/Permits/PermitLocator.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;

namespace Ecr.Application.Permits;

/// <summary>
/// Знаходить дозвіл за ідентифікатором і перевіряє доступ до реєстру ЙОГО шапки (Permits, PM-04): глобальне право
/// або ресурсний грант потрібного рівня, а заборона на реєстр шапки - <c>404</c>, як неіснуючий дозвіл.
/// </summary>
internal sealed class PermitLocator(
    IPermitTypeStore permits,
    IRegistryStore registries,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Знаходить дозвіл (запис шапки відстежується).</summary>
    /// <param name="permitId">Ідентифікатор дозволу.</param>
    /// <param name="permission">Глобальне право.</param>
    /// <param name="level">Мінімальний рівень гранта на реєстр шапки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-PRM-0404</c>: дозволу немає, запис не з шапки типу, або заборона на реєстр.</exception>
    /// <exception cref="AccessDeniedException">Немає права й гранта.</exception>
    internal async Task<(PermitTypeDef Type, RegistryEntry Entry)> FindAsync(
        long permitId, string permission, GrantLevel level, CancellationToken ct)
    {
        var entry = await registries.FindEntryAsync(permitId, ct).ConfigureAwait(false);
        var type = entry is { IsDeleted: false }
            ? (await permits.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(t => t.HeaderRegistryDefId == entry.RegistryDefId)
            : null;

        if (entry is null || type is null)
        {
            // Спершу право: анонім і користувач без права не відрізняють «немає» від «є, але не твоє».
            await Security.PermissionCheck.RequireAsync(access, currentUser, permission, ct).ConfigureAwait(false);
            throw PermitErrors.NotFound(permitId);
        }

        try
        {
            await RegistryAccess.RequireAsync(access, currentUser, permission, level, type.HeaderRegistryDefId, ct).ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            // Заборона на реєстр шапки: відповідь тієї самої форми, що й для неіснуючого дозволу.
            throw PermitErrors.NotFound(permitId);
        }

        return (type, entry);
    }
}
