// src/Ecr.Application/Documents/DocumentVisibility.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Domain.Errors;

namespace Ecr.Application.Documents;

/// <summary>
/// Документ, якого користувач не бачить, для нього НЕ ІСНУЄ: відповідь та
/// сама, що й на неіснуючий, — <c>404 ECR-DOC-0404</c> (B-08, UX-прохід,
/// четвертий раунд).
/// </summary>
/// <remarks>
/// ⛔ Що відтворили аналітики: оператор без гранта отримував на
/// <c>GET /documents/9</c> — <c>404</c> (<c>GetDocumentHandler</c>), а на
/// <c>…/9/tables</c>, <c>…/header</c>, <c>…/validation</c> — <c>403</c>
/// «no access to document 9: NoGrant», тоді як неіснуючий документ давав
/// <c>404</c>. Різниця між двома відповідями сама є відомістю: за нею
/// перебором ідентифікаторів видно, які документи існують у чужих проєктах.
///
/// ⚠ Відповідь бере ТОЙ САМИЙ ключ, що й відсутній документ
/// (<c>AccessDecisionService.ProjectIdAsync</c>, <c>CreateRowHandler</c>,
/// <c>PatchCellsHandler</c>), — тобто тіло теж не відрізняється.
///
/// ⚠ Лише ВИДИМІСТЬ. Документ, який користувач бачить, але не може змінити
/// (грант <c>Read</c> без <c>Write</c>), і далі отримує <c>403</c> із причиною:
/// там приховувати нічого, а причина потрібна людині.
/// </remarks>
public static class DocumentVisibility
{
    /// <summary>Вимагає, щоб документ був видимий користувачеві.</summary>
    /// <param name="access">Служба рішень доступу.</param>
    /// <param name="profile">Профіль користувача.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-DOC-0404</c> — документа немає або він невидимий.</exception>
    public static async Task RequireVisibleAsync(
        IAccessDecisionService access, AccessProfile profile, long documentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);

        var read = await access.CanReadDocumentAsync(profile, documentId, ct).ConfigureAwait(false);
        if (!read.IsAllowed)
        {
            throw NotFound(documentId);
        }
    }

    /// <summary>Відповідь «документа немає» — однакова для відсутнього й невидимого.</summary>
    /// <param name="documentId">Документ.</param>
    public static NotFoundException NotFound(long documentId)
        => new(
            ErrorCodes.DocumentNotFound,
            $"Документ {documentId} не знайдено.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "err.ECR-DOC-0404.document",
                ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
            });
}
