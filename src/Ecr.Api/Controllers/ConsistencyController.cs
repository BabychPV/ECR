using Ecr.Application.Common;
using Ecr.Application.Consistency;
using Ecr.Application.Ports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Читання журналу знахідок перевірки узгодженості.</summary>
/// <remarks>
/// ⛔ Журнал **тільки читається**, як і аудит: знахідку закриває той, хто
/// усунув причину, а не той, хто на неї дивиться. Ендпоінта на зміну тут
/// немає і не буде, доки не з'явиться сценарій «усунув — позначив».
/// </remarks>
[ApiController]
[Route("api/v1/consistency")]
[Authorize]
public sealed class ConsistencyController(GetConsistencyIssuesHandler issues) : ControllerBase
{
    /// <summary>
    /// Знахідки перевірки узгодженості. Право <c>System.ViewHealth</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ До цього ендпоінта <c>aud.ConsistencyIssue</c> не читав НІХТО —
    /// ні сервер, ні клієнт. З продукту було видно лише кількість за типом
    /// (лічильник <c>ecr.consistency.issues</c>), тобто «є 12 знахідок
    /// <c>BROKEN_FK</c>» без жодного способу дізнатися, де саме.
    ///
    /// ⚠ Пагінація курсорна: таблиця росте від кожного нічного прогону, і
    /// стеля одного проходу — тисяча знахідок.
    ///
    /// ⚠ <c>message</c> у відповіді — український текст, який записала сама
    /// задача. Він НЕ локалізується: каталог рядків існує для відмов API
    /// (<c>err.*</c>), не для цього журналу.
    /// </remarks>
    [HttpGet("issues")]
    [ProducesResponseType<PagedResult<ConsistencyIssueView>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]

    // ⚠ `422`, а не `400`: розмір сторінки поза межами — це порушення
    // ПРАВИЛА (`BusinessRuleException` → `ECR-REQ-0422`), а не синтаксично
    // непридатний запит. Оголошено те, що конвеєр справді віддає: перевірено
    // тестом `Розмір_сторінки_понад_максимум_відхиляється_а_не_обрізається_мовчки`.
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PagedResult<ConsistencyIssueView>>> Issues(
        [FromQuery] string? ruleCode,
        [FromQuery] bool openOnly,
        [FromQuery] int limit,
        [FromQuery] string? cursor,
        CancellationToken ct)
    {
        // ⚠ `limit == 0` означає «клієнт не назвав розміру» — типове значення
        // `int`, яке приходить із відсутнього параметра. Та сама форма, що в
        // `AuditController`: межі перевіряє обробник, бо правило про розмір
        // сторінки має діяти незалежно від того, звідки його викликали.
        var page = new CursorRequest(limit == 0 ? 50 : limit, cursor);

        return Ok(await issues
            .HandleAsync(ruleCode, openOnly, page, ct)
            .ConfigureAwait(false));
    }
}
