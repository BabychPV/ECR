using Ecr.Application.Workflow.ApprovalQueue;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>«До погодження»: аркуші, що чекають рішення поточного користувача (ФВ-12.5).</summary>
[ApiController]
[Route("api/v1/me/approval-queue")]
[Authorize]
public sealed class MeApprovalQueueController(ListMyApprovalQueueHandler queue) : ControllerBase
{
    /// <summary>
    /// Подані аркуші, які поточний користувач може затвердити зараз: документ і аркуш видимі йому, крок
    /// маршруту погодження — його ролі, рівень гранта не нижчий за <c>Approve</c>, подав не він сам.
    /// Найдавніші подання першими; без імен схованого.
    /// </summary>
    /// <param name="limit">Розмір сторінки 1..200 (за замовчуванням 50).</param>
    /// <param name="cursor">Курсор попередньої відповіді.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<ApprovalQueueDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Get(
        [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct)
        => Ok(await queue.HandleAsync(limit, cursor, ct).ConfigureAwait(false));
}
