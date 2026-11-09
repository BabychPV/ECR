using Ecr.Application.Periods;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Діючі винятки «закритий місяць відкрито для одного документа» в проєкті — для вкладки Periods (приймальна i4:
/// «January shows "Locked · 1 document reopened until …" with the document name and a "Close now" action»).
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:int}/document-reopens")]
[Authorize]
public sealed class ProjectDocumentReopensController(ListProjectDocumentReopensHandler list) : ControllerBase
{
    /// <summary>
    /// Діючі винятки видимих документів проєкту (за місяцем і номером документа) і чи може викликач закрити їх.
    /// </summary>
    /// <remarks>
    /// <c>404</c> — проєкт невидимий або відсутній. Документи, яких викликач не бачить, у відповідь не потрапляють.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<ProjectDocumentReopens>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProjectDocumentReopens>> List(int projectId, CancellationToken ct)
        => Ok(await list.HandleAsync(projectId, ct).ConfigureAwait(false));
}
