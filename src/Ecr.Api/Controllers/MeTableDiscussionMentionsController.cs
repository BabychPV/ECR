using Ecr.Application.Documents.Discussions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>«Мої задачі»: непрочитані згадки поточного користувача в чатах таблиць.</summary>
[ApiController]
[Route("api/v1/me/table-discussion-mentions")]
[Authorize]
public sealed class MeTableDiscussionMentionsController(TableDiscussionQueries queries) : ControllerBase
{
    /// <summary>Непрочитані згадки, найновіші першими; лише таблиці, які користувач досі бачить.</summary>
    /// <param name="limit">1..100, за замовчуванням 50.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<TableDiscussionMentionTasksDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get([FromQuery] int? limit, CancellationToken ct)
        => Ok(await queries.MyMentionsAsync(limit, ct).ConfigureAwait(false));
}
