// src/Ecr.Application/Permits/Bindings/PermitBindingErrors.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Domain.Errors;

namespace Ecr.Application.Permits.Bindings;

/// <summary>Відмови прив'язок аркуша до дозволів (<c>ECR-PRM-*</c>, Permits PM-09).</summary>
/// <remarks>
/// ⚠ Ключ каталогу - повним літералом у точці створення винятку: сторож <c>ErrorTitleCatalogTests</c> звіряє з сідом
/// саме літерали.
/// </remarks>
internal static class PermitBindingErrors
{
    /// <summary><c>404 ECR-PRM-0404 binding</c>: прив'язки з таким кодом у версії немає.</summary>
    /// <param name="code">Код прив'язки.</param>
    /// <param name="templateVersionId">Версія шаблону.</param>
    internal static NotFoundException NotFound(string code, int templateVersionId)
        => new(
            ErrorCodes.PermitTypeNotFound,
            $"Прив'язки «{code}» у версії {templateVersionId} немає.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-0404.binding",
                ["bindingCode"] = code,
                ["versionId"] = templateVersionId.ToString(CultureInfo.InvariantCulture),
            });

    /// <summary><c>404 ECR-TMPL-0404 templateVersion</c>: версії шаблону немає.</summary>
    /// <param name="templateVersionId">Версія шаблону.</param>
    internal static NotFoundException VersionNotFound(int templateVersionId)
        => new(
            ErrorCodes.TemplateNotFound,
            $"Версії шаблону {templateVersionId} не існує.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-TMPL-0404.templateVersion",
                ["versionId"] = templateVersionId.ToString(CultureInfo.InvariantCulture),
            });
}
