// src/Ecr.Application/Permits/PermitTypeErrors.cs
using Ecr.Application.Errors;
using Ecr.Domain.Errors;

namespace Ecr.Application.Permits;

/// <summary>Відмови типів дозволів (<c>ECR-PRM-*</c>, Permits PM-03).</summary>
/// <remarks>
/// ⚠ Ключ каталогу — повним літералом у точці створення винятку: сторож <c>ErrorTitleCatalogTests</c>
/// звіряє з сідом саме літерали.
/// </remarks>
internal static class PermitTypeErrors
{
    /// <summary><c>409 ECR-PRM-4091 code</c>: тип з таким кодом уже є.</summary>
    /// <param name="code">Код типу.</param>
    internal static BusinessRuleException Duplicate(string code)
        => new(
            ErrorCodes.PermitTypeDuplicate,
            $"Тип дозволу «{code}» уже існує.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-4091.code",
                ["code"] = code,
            });

    /// <summary><c>409 ECR-PRM-4091 registryCode</c>: код реєстру, потрібного типу, зайнятий.</summary>
    /// <param name="registryCode">Код реєстру.</param>
    internal static BusinessRuleException RegistryCodeTaken(string registryCode)
        => new(
            ErrorCodes.PermitTypeDuplicate,
            $"Код реєстру «{registryCode}» зайнятий.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-4091.registryCode",
                ["registryCode"] = registryCode,
            });

    /// <summary><c>404 ECR-PRM-0404 type</c>.</summary>
    /// <param name="code">Код типу з запиту.</param>
    internal static NotFoundException NotFound(string code)
        => new(
            ErrorCodes.PermitTypeNotFound,
            $"Типу дозволу «{code}» не існує.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-0404.type",
                ["code"] = code,
            });
}
