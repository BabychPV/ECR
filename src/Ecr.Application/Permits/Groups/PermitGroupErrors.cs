// src/Ecr.Application/Permits/Groups/PermitGroupErrors.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Domain.Errors;

namespace Ecr.Application.Permits.Groups;

/// <summary>Відмови груп типів дозволів (<c>ECR-PRM-*</c>, Permits PRMG).</summary>
/// <remarks>
/// ⚠ Ключ каталогу - повним літералом у точці створення винятку: сторож <c>ErrorTitleCatalogTests</c>
/// звіряє з сідом саме літерали.
/// </remarks>
internal static class PermitGroupErrors
{
    /// <summary>Префікс коду прихованого реєстру групи; зарезервований за групами.</summary>
    internal const string RegistryPrefix = "PRMG_";

    /// <summary>Код прихованого реєстру групи.</summary>
    /// <param name="groupCode">Код групи.</param>
    internal static string RegistryCode(string groupCode) => RegistryPrefix + groupCode;

    /// <summary><c>404 ECR-PRM-0405 group</c>: групи немає.</summary>
    /// <param name="code">Код з запиту.</param>
    internal static NotFoundException NotFound(string code)
        => new(
            ErrorCodes.PermitGroupNotFound,
            $"Групи дозволів «{code}» не існує.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-0405.group",
                ["code"] = code,
            });

    /// <summary><c>409 ECR-PRM-4097 code</c>: група з таким кодом уже є.</summary>
    /// <param name="code">Код групи.</param>
    internal static BusinessRuleException Duplicate(string code)
        => new(
            ErrorCodes.PermitGroupDuplicate,
            $"Група дозволів «{code}» уже існує.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-4097.code",
                ["code"] = code,
            });

    /// <summary><c>409 ECR-PRM-4097 registryCode</c>: код реєстру групи зайнятий звичайним довідником.</summary>
    /// <param name="registryCode">Код реєстру.</param>
    internal static BusinessRuleException RegistryCodeTaken(string registryCode)
        => new(
            ErrorCodes.PermitGroupDuplicate,
            $"Код реєстру «{registryCode}» зайнятий.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-4097.registryCode",
                ["registryCode"] = registryCode,
            });

    /// <summary><c>409 ECR-PRM-4097 registryReserved</c>: префікс <c>PRMG_</c> зарезервований за групами дозволів.</summary>
    /// <param name="registryCode">Код нового довідника.</param>
    internal static BusinessRuleException RegistryPrefixReserved(string registryCode)
        => new(
            ErrorCodes.PermitGroupDuplicate,
            $"Префікс «{RegistryPrefix}» зарезервований за групами дозволів: код «{registryCode}» не можна використати.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-4097.registryReserved",
                ["registryCode"] = registryCode,
                ["prefix"] = RegistryPrefix,
            });

    /// <summary><c>409 ECR-PRM-4098 inUse</c>: групу використовують колонки/поля шаблонів - лише деактивація.</summary>
    /// <param name="code">Код групи.</param>
    /// <param name="columns">Скільки колонок/полів.</param>
    /// <param name="templates">Скільки шаблонів.</param>
    internal static BusinessRuleException InUse(string code, int columns, int templates)
        => new(
            ErrorCodes.PermitGroupInUse,
            $"Групу дозволів «{code}» використовують шаблони ({templates}): її можна лише деактивувати.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-4098.inUse",
                ["code"] = code,
                ["columns"] = columns.ToString(CultureInfo.InvariantCulture),
                ["templates"] = templates.ToString(CultureInfo.InvariantCulture),
            });

    /// <summary><c>409 ECR-PRM-4099 changed</c>: групу змінили після читання.</summary>
    /// <param name="code">Код групи.</param>
    /// <param name="rowVersion">Актуальний токен.</param>
    internal static ConcurrencyConflictException Changed(string code, byte[] rowVersion)
        => new(
            ErrorCodes.PermitGroupConflict,
            $"Групу дозволів «{code}» змінили після того, як її прочитали.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-4099.changed",
                ["code"] = code,
                ["rowVersion"] = Convert.ToBase64String(rowVersion),
            });

    /// <summary>Токен із запиту збігається з актуальним, інакше <c>409 changed</c>.</summary>
    /// <param name="group">Група.</param>
    /// <param name="rowVersion">Токен із запиту (base64).</param>
    internal static void RequireRowVersion(Ecr.Domain.Entities.Configuration.PermitGroup group, string? rowVersion)
    {
        if (!string.Equals(Convert.ToBase64String(group.RowVersion), rowVersion, StringComparison.Ordinal))
        {
            throw Changed(group.Code, group.RowVersion);
        }
    }

    /// <summary><c>422 ECR-PRM-0422</c> з окремим <c>messageKey</c> на випадок.</summary>
    /// <param name="message">Текст.</param>
    /// <param name="messageKey">Ключ каталогу, повним літералом.</param>
    /// <param name="details">Додаткові параметри.</param>
    internal static BusinessRuleException Invalid(string message, string messageKey, params (string Name, string Value)[] details)
    {
        var map = new Dictionary<string, object?> { ["messageKey"] = messageKey };
        foreach (var (name, value) in details)
        {
            map[name] = value;
        }

        return new BusinessRuleException(ErrorCodes.PermitTypeInvalid, message, map);
    }
}
