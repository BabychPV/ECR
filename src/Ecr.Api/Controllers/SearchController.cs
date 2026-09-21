// src/Ecr.Api/Controllers/SearchController.cs
using Ecr.Application.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Пошук даних для командної палітри (BE-19).</summary>
[ApiController]
[Route("api/v1/search")]
[Authorize]
public sealed class SearchController(SearchHandler search) : ControllerBase
{
    /// <summary>Документи, шаблони й довідники за підрядком коду чи назви — лише видимі користувачу.</summary>
    /// <param name="q">Підрядок; коротший за 2 символи — порожня відповідь.</param>
    /// <param name="limit">Стеля; <c>0</c> — 10, більше за 20 — обрізається до 20.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SearchHitDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] int limit, CancellationToken ct)
        => Ok(await search.HandleAsync(q, limit, ct).ConfigureAwait(false));
}
