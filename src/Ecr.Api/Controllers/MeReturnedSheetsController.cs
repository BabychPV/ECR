using Ecr.Application.Workflow.ReturnedSheets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>✎ D-449: «Повернуто на доопрацювання» — аркуші, повернені поточному користувачеві на доопрацювання.</summary>
[ApiController]
[Route("api/v1/me/returned-sheets")]
[Authorize]
public sealed class MeReturnedSheetsController(ListMyReturnedSheetsHandler returned) : ControllerBase
{
    /// <summary>
    /// Аркуші, повернені на доопрацювання — відхилені погоджувачем (<c>Rejected</c>) або повернені в роботу
    /// (<c>Draft</c>, остання подія — <c>Reopen</c>, не самим викликачем; відкликання автором — ні), над якими працює
    /// поточний користувач: власник документа, учасник із наданням ≥ <c>Write</c> на аркуш або останній подавач із правом
    /// писати в аркуш; проєкт не заборонений, аркуш видимий йому. Повторне подання прибирає аркуш із переліку.
    /// Найдавніші повернення першими; без імен схованого.
    /// </summary>
    /// <param name="limit">Розмір сторінки 1..200 (за замовчуванням 50).</param>
    /// <param name="cursor">Курсор попередньої відповіді.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<ReturnedSheetsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Get(
        [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct)
        => Ok(await returned.HandleAsync(limit, cursor, ct).ConfigureAwait(false));
}
