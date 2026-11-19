using Ecr.Application.DocumentDependencies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// B179 L4: граф залежностей документів для екрана адміністратора. Право <c>Calculation.ManageSourceBinding</c> (з вбудованих
/// ролей — лише <c>SystemAdministrator</c>).
/// </summary>
[ApiController]
[Route("api/v1/dependency-graph")]
[Authorize]
public sealed class DependencyGraphController(GetDependencyGraphHandler graph) : ControllerBase
{
    /// <summary>
    /// Граф: вузли (шаблони, довідники, методології), ребра з видом і доказами, згорнуті зв'язки «джерело → споживач»,
    /// рівні порядку обчислення, цикли й зауваження побудови; <c>revision</c> — ревізія вхідних даних. Право
    /// <c>Calculation.ManageSourceBinding</c>.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<DependencyGraphDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<DependencyGraphDto>> Get(CancellationToken ct)
        => Ok(await graph.HandleAsync(ct).ConfigureAwait(false));
}
