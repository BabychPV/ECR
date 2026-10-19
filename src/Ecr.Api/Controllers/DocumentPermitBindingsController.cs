using Ecr.Application.Permits.Bindings;
using Ecr.Application.Security;
using Ecr.Domain.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Прив'язки аркуша до дозволів на рівні ДОКУМЕНТА: «оновити з дозволу» (Permits, PM-10).
/// </summary>
[ApiController]
[Route("api/v1/documents/{documentId:long}")]
[Authorize]
public sealed class DocumentPermitBindingsController(
    RefreshPermitBindingHandler refresh,
    IAccessDecisionService access,
    Ecr.Api.Auth.CurrentUser currentUser) : ControllerBase
{
    /// <summary>
    /// Додає в таблицю документа нові рядки дозволу й оновлює ліміти та довідкові значення, не видаляючи рядків.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="code">Код прив'язки.</param>
    /// <param name="periodKey">Період документа <c>РРРРММ</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Право - те саме, що на додавання рядка в цю таблицю; закритий період і поданий чи затверджений аркуш
    /// відхиляються <c>403 ECR-ACCS-0403</c> (<c>addRowDenied</c>). Значення переписуються лише там, де остання зміна
    /// комірки - від дозволу. Рядки, яких у дозволі більше немає, лишаються й повертаються в <c>removedFromPermit</c>.
    /// </remarks>
    [HttpPost("permit-bindings/{code}/refresh")]
    [ProducesResponseType<PermitRefreshResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PermitRefreshResultDto>> Refresh(
        long documentId, string code, [FromQuery] int periodKey, CancellationToken ct)
    {
        var profile = await access.BuildProfileAsync(
            currentUser.UserId ?? throw new Application.Errors.AccessDeniedException(
                ErrorCodes.Unauthorized, "Сесія не містить користувача.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" }),
            ct).ConfigureAwait(false);

        return Ok(await refresh.HandleAsync(documentId, code, periodKey, profile, ct).ConfigureAwait(false));
    }
}
