using Ecr.Application.Calculations.Scripting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Живі таблиці за кодами — підказки редактора скрипта методології (MS).</summary>
[ApiController]
[Route("api/v1/methodologies/script/tables")]
[Authorize]
public sealed class MethodologyScriptTablesController(GetScriptTablesHandler tables) : ControllerBase
{
    /// <summary>
    /// Живі таблиці за кодами — колонки (тип, одиниця, місяць) і псевдоніми місячних входів для підказок редактора.
    /// Право <c>Calculation.View</c>.
    /// </summary>
    /// <param name="codes">Коди таблиць через кому (до 50); <c>Шаблон.Таблиця</c> — з кодом шаблону.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>Код, якого немає серед живих таблиць, у відповіді відсутній; код у кількох шаблонах дає кілька записів.</remarks>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ScriptTableDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IReadOnlyList<ScriptTableDto>>> Tables([FromQuery] string? codes, CancellationToken ct)
        => Ok(await tables.HandleAsync(codes, ct).ConfigureAwait(false));
}
