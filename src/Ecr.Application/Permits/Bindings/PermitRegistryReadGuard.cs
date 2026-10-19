// src/Ecr.Application/Permits/Bindings/PermitRegistryReadGuard.cs
using Ecr.Application.Errors;
using Ecr.Application.Registries;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Application.Permits.Bindings;

/// <summary>
/// Право читати ДАНІ дозволу, які віддає перегляд і «Оновити з дозволу» (Permits, PM-09/PM-10): номер, ліміти, рядки.
/// Те саме правило, що й у <c>GetPermitHandler</c> / <c>ListPermitsHandler</c>: <c>Registry.View</c> (або ресурсний грант
/// <c>Read</c>) на реєстр шапки типу ТА на реєстр таблиці дозволу; явна заборона на реєстр перекриває обидва.
/// </summary>
/// <remarks>
/// ⛔ <c>Template.View</c> (право бачити саму прив'язку) і <c>Document.View</c> (право бачити документ) НЕ дають права читати
/// реєстри дозволів: без цього перегляд з довільним <c>facilityName</c> віддавав би номери, ліміти й рядки дозволів
/// об'єктів, яких читач не бачить (аудит PM-09, C4).
/// </remarks>
internal static class PermitRegistryReadGuard
{
    /// <summary>Глобальне право читання довідників (те саме, що в <c>PermitQueryHandlers</c>).</summary>
    public const string Permission = "Registry.View";

    /// <summary>Кидає відмову, якщо профіль не читає реєстр шапки чи реєстр таблиці дозволу.</summary>
    /// <param name="profile">Профіль того, хто питає.</param>
    /// <param name="type">Тип дозволу прив'язки.</param>
    /// <param name="table">Таблиця типу прив'язки.</param>
    /// <param name="hidden">
    /// Відповідь на ЯВНУ заборону реєстру: та сама, що й для неіснуючої прив'язки, - щоб заборону не можна було відрізнити
    /// від відсутності (як у <c>PermitLocator</c>).
    /// </param>
    /// <exception cref="AccessDeniedException">Немає ні права, ні гранта (403, як у <c>PermitQueryHandlers</c>).</exception>
    public static void Require(AccessProfile profile, PermitTypeDef type, PermitTypeTable table, Func<Exception> hidden)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(hidden);

        int[] registries = [type.HeaderRegistryDefId, table.RegistryDefId];

        if (registries.Any(id => RegistryAccess.IsDenied(profile, id)))
        {
            throw hidden();
        }

        if (!registries.All(id => RegistryAccess.CanAccess(profile, Permission, GrantLevel.Read, id)))
        {
            throw RegistryAccess.Denied(Permission);
        }
    }
}
