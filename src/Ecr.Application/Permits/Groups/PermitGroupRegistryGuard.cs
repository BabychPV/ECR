// src/Ecr.Application/Permits/Groups/PermitGroupRegistryGuard.cs
using Ecr.Application.Errors;
using Ecr.Domain.Errors;

namespace Ecr.Application.Permits.Groups;

/// <summary>
/// Прихований реєстр групи типів дозволів (<c>PRMG_&lt;КОД&gt;</c>) керується ЛИШЕ групою: звичайні шляхи запису довідників
/// (записи, опис, чернетка, публікація, перемикання master) його не змінюють, а префікс <c>PRMG_</c> зарезервований
/// (Permits, PRMG).
/// </summary>
/// <remarks>
/// ⛔ Інакше запис, доданий у порожній <c>PRMG_</c> повз групу, збігся б за <c>RegistryDefId</c> із ціллю колонки й PATCH його
/// прийняв би без жодної групи. Ознака - префікс коду (код реєстру унікальний, префікс зарезервований цим же сторожем у
/// <c>CreateRegistryHandler</c> і <c>CreatePermitGroupHandler</c>), тому без додаткових запитів.
/// </remarks>
internal static class PermitGroupRegistryGuard
{
    /// <summary>Чи код належить прихованому реєстру групи.</summary>
    /// <param name="registryCode">Код реєстру.</param>
    internal static bool IsGroupRegistry(string? registryCode)
        => registryCode is not null && registryCode.StartsWith(PermitGroupErrors.RegistryPrefix, StringComparison.Ordinal);

    /// <summary>Відмова, коли реєстр - прихований реєстр групи.</summary>
    /// <param name="registryCode">Код реєстру.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-PRM-0422</c> <c>permitGroupRegistryManaged</c>.</exception>
    internal static void RequireNotManaged(string? registryCode)
    {
        if (IsGroupRegistry(registryCode))
        {
            throw new BusinessRuleException(
                ErrorCodes.PermitTypeInvalid,
                $"Довідник «{registryCode}» належить групі дозволів і змінюється лише через групу.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRM-0422.permitGroupRegistryManaged",
                    ["registryCode"] = registryCode,
                });
        }
    }

    /// <summary>
    /// Фільтр активності рядків методології (<c>КОД_ДОВІДНИКА:КОД_ЗАПИСУ</c>) не може вказувати на прихований реєстр групи: в ньому
    /// немає записів, тож фільтр мовчки не діяв би (<c>ECR-CALC-0422</c> <c>rowActivityFilterPermitGroup</c>).
    /// </summary>
    /// <param name="filter">Значення фільтра; порожнє - фільтра немає.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-CALC-0422</c>.</exception>
    internal static void RequireNotRowActivityTarget(string? filter)
    {
        var registryCode = filter?.Split(':')[0].Trim();
        if (IsGroupRegistry(registryCode))
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                $"Фільтр активності рядків не може вказувати на групу дозволів «{registryCode}»: у ній немає записів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.rowActivityFilterPermitGroup",
                    ["registryCode"] = registryCode,
                });
        }
    }

    /// <summary>Відмова створити звичайний довідник із зарезервованим префіксом <c>PRMG_</c> (<c>ECR-PRM-4097</c>).</summary>
    /// <param name="registryCode">Код нового довідника.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-PRM-4097</c> <c>registryCode</c>.</exception>
    internal static void RequireNotReserved(string registryCode)
    {
        if (IsGroupRegistry(registryCode))
        {
            throw PermitGroupErrors.RegistryPrefixReserved(registryCode);
        }
    }
}
