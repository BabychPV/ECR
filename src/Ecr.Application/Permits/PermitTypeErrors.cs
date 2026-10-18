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

    /// <summary><c>409 ECR-PRM-4093 changed</c>: токен чернетки чужий.</summary>
    /// <param name="code">Код типу.</param>
    /// <param name="rowVersion">Актуальний токен.</param>
    internal static ConcurrencyConflictException Changed(string code, byte[] rowVersion)
        => new(
            ErrorCodes.PermitTypeConflict,
            $"Тип дозволу «{code}» змінили або опублікували після того, як його прочитали.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-4093.changed",
                ["code"] = code,
                ["rowVersion"] = Convert.ToBase64String(rowVersion),
            });

    /// <summary><c>409 ECR-PRM-4093 noDraft</c>: опублікований тип без відкритої чернетки.</summary>
    /// <param name="code">Код типу.</param>
    internal static ConcurrencyConflictException NoDraft(string code)
        => new(
            ErrorCodes.PermitTypeConflict,
            $"У типу дозволу «{code}» немає відкритої чернетки: спершу відкрийте нову версію.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-4093.noDraft",
                ["code"] = code,
            });

    /// <summary><c>409 ECR-PRM-4093 draftExists</c>.</summary>
    /// <param name="code">Код типу.</param>
    internal static ConcurrencyConflictException DraftExists(string code)
        => new(
            ErrorCodes.PermitTypeConflict,
            $"У типу дозволу «{code}» уже є чернетка нової версії.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-4093.draftExists",
                ["code"] = code,
            });

    /// <summary><c>409 ECR-PRM-4093 notPublished</c>.</summary>
    /// <param name="code">Код типу.</param>
    internal static ConcurrencyConflictException NotPublished(string code)
        => new(
            ErrorCodes.PermitTypeConflict,
            $"Тип дозволу «{code}» ще не опублікований: нової версії немає з чого відкривати.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-4093.notPublished",
                ["code"] = code,
            });

    /// <summary>Токен із запиту збігається з актуальним, інакше <c>409 changed</c>.</summary>
    /// <param name="type">Тип.</param>
    /// <param name="rowVersion">Токен з запиту (base64).</param>
    internal static void RequireRowVersion(Ecr.Domain.Entities.Configuration.PermitTypeDef type, string? rowVersion)
    {
        if (!string.Equals(Convert.ToBase64String(type.RowVersion), rowVersion, StringComparison.Ordinal))
        {
            throw Changed(type.Code, type.RowVersion);
        }
    }

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
