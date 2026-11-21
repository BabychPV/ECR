using Ecr.Api.Security;
using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Scripting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ecr.Api.Controllers;

/// <summary>
/// Скрипт версії методології («ECR Script», MS): читання, збереження, перевірка, перегляд на документі,
/// форматування константи.
/// </summary>
/// <remarks>
/// ⛔ Кожна дія спершу звіряє, що версія належить методології з адреси (<see cref="MethodologyVersionScope"/>:
/// право, потім належність) — так само, як дії <c>MethodologiesController</c> над версією.
/// </remarks>
[ApiController]
[Route("api/v1/methodologies/{id:int}/versions/{vid:int}/script")]
[Authorize]
public sealed class MethodologyScriptController(
    GetMethodologyScriptHandler get,
    SaveMethodologyScriptHandler save,
    ValidateMethodologyScriptHandler validate,
    PreviewMethodologyScriptHandler preview,
    FormatScriptConstHandler formatConst,
    MethodologyVersionScope scope,
    ScriptRunGate gate) : ControllerBase
{
    /// <summary>Скрипт версії: текст, хеш, діагностики, структура. Право <c>Calculation.View</c>.</summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<MethodologyScriptDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MethodologyScriptDto>> Get(int id, int vid, CancellationToken ct)
    {
        await scope.RequireAsync(id, vid, GetMethodologyScriptHandler.Permission, ct).ConfigureAwait(false);

        return Ok(await get.HandleAsync(vid, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Зберігає текст скрипта чернетки. Право <c>Calculation.EditFormula</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія-чернетка з рушієм <c>Script</c>.</param>
    /// <param name="request">Текст і, за потреби, хеш, з якого редактор виходив.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Текст зберігається завжди, навіть із помилками; виходи, речовини, імпорти й таблиці читання
    /// матеріалізуються лише коли діагностик немає. Не чернетка чи не скрипт — <c>409 scriptNotEditable</c>;
    /// <c>expectedHash</c> не збігся — <c>409 scriptChanged</c>.
    /// </remarks>
    [HttpPut]
    [ProducesResponseType<MethodologyScriptDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MethodologyScriptDto>> Save(
        int id, int vid, [FromBody] SaveMethodologyScriptRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await scope.RequireAsync(id, vid, SaveMethodologyScriptHandler.Permission, ct).ConfigureAwait(false);

        return Ok(await save.HandleAsync(vid, request.Text, request.ExpectedHash, ct).ConfigureAwait(false));
    }

    /// <summary>Перевіряє текст, нічого не зберігаючи. Право <c>Calculation.View</c>; межа 120/хв на користувача (PSS-21).</summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія.</param>
    /// <param name="request">Текст скрипта.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost("validate")]
    [EnableRateLimiting(ScriptCheckRateLimitPolicy.PolicyName)] // PSS-21: кошик перевірок, спільний із format-const
    [ProducesResponseType<ScriptValidationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ScriptValidationDto>> Validate(
        int id, int vid, [FromBody] ScriptTextRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await scope.RequireAsync(id, vid, ValidateMethodologyScriptHandler.Permission, ct).ConfigureAwait(false);

        return Ok(await validate.HandleAsync(vid, request.Text, ct).ConfigureAwait(false));
    }

    /// <summary>Прогін скрипта на документі «насухо»: що вибрано, що пораховано. Право <c>Calculation.View</c>, доступ до документа й <c>Document.View</c>; межа 30/хв на користувача (PSS-21).</summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія.</param>
    /// <param name="request">Текст (порожній — збережений), документ, період, таблиця, кількість рядків.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>Нічого не записує. Текст із діагностиками не виконується — повертаються лише діагностики. Понад 30 с — 422 scriptPreviewTimeout.</remarks>
    [HttpPost("preview")]
    [EnableRateLimiting(ScriptAuthoringRateLimitPolicy.PolicyName)] // PSS-21: кошик прогонів, спільний із паритетом
    [ProducesResponseType<ScriptPreviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ScriptPreviewDto>> Preview(
        int id, int vid, [FromBody] PreviewMethodologyScriptRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await scope.RequireAsync(id, vid, PreviewMethodologyScriptHandler.Permission, ct).ConfigureAwait(false);

        using var lease = await gate.EnterAsync(ct).ConfigureAwait(false);
        return Ok(await preview.HandleAsync(vid, request, ct).ConfigureAwait(false));
    }

    /// <summary>Канонічний друк літерала константи для редактора таблиць коефіцієнтів. Право <c>Calculation.View</c>; межа 120/хв на користувача (PSS-21).</summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія.</param>
    /// <param name="request">Літерал константи: число, рядок або мапа.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost("format-const")]
    [EnableRateLimiting(ScriptCheckRateLimitPolicy.PolicyName)] // PSS-21: той самий кошик, що validate
    [ProducesResponseType<FormatConstDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<FormatConstDto>> FormatConst(
        int id, int vid, [FromBody] ScriptTextRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await scope.RequireAsync(id, vid, FormatScriptConstHandler.Permission, ct).ConfigureAwait(false);

        return Ok(await formatConst.HandleAsync(request.Text, ct).ConfigureAwait(false));
    }
}
