// src/Ecr.Api/Controllers/ColumnDefsController.cs
using Ecr.Application.Templates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Пошук колонок за назвою чи кодом, наскрізь по всіх версіях шаблонів
/// (директива "пошук колонки за назвою замість голого ColumnDefId").
/// </summary>
/// <remarks>
/// ⛔ Окремий контролер, а не маршрут під <c>template-versions/{id}</c>:
/// прив'язка методології (<c>SaveCalculationBindingHandler</c>) не обмежена
/// ОДНІЄЮ версією — <c>TableDefId</c> виводиться із самої колонки, тож пошук
/// теж наскрізний.
/// </remarks>
[ApiController]
[Route("api/v1/column-defs")]
[Authorize]
public sealed class ColumnDefsController(SearchColumnDefsHandler search) : ControllerBase
{
    /// <summary>Пошук колонок. Право <c>Template.View</c>.</summary>
    /// <param name="q">Підрядок коду чи будь-якого перекладу заголовка; порожній — без фільтра.</param>
    /// <param name="limit">Стеля кількості результатів; <c>0</c> — типове значення.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("search")]
    [ProducesResponseType<IReadOnlyList<ColumnDefSearchResultDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(
        [FromQuery] string? q, [FromQuery] int limit, CancellationToken ct)
        => Ok(await search.HandleAsync(q, limit, ct).ConfigureAwait(false));
}
