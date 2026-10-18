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
