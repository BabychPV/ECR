using Ecr.Application.Common;
using Ecr.Application.Consistency;
using Ecr.Application.Ports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Журнал знахідок перевірки узгодженості і прогін її на вимогу.</summary>
/// <remarks>
/// ⛔ Сам ЖУРНАЛ лишається тільки для читання, як і аудит: знахідку закриває
/// той, хто усунув причину, а не той, хто на неї дивиться. Дії «взяти до
/// відома» тут немає і не буде — це пряме рішення людини на <c>Q15-03</c>
/// (директива №15, рішення 2): знахідка зникає сама, коли наступна перевірка
/// проходить.
///
/// ⚠ Саме з цього рішення випливає дія <c>POST /run</c>. Якщо знахідку не
/// можна зняти позначкою, єдиний спосіб зняти полагоджену знахідку з очей —
/// прогнати перевірку ще раз; доти вона ходила лише за нічним розкладом, і
/// той, хто усунув причину вранці, бачив її в переліку до наступної ночі.
/// </remarks>
[ApiController]
[Route("api/v1/consistency")]
[Authorize]
public sealed class ConsistencyController(
    GetConsistencyIssuesHandler issues, RunConsistencyCheckHandler run) : ControllerBase
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

    /// <summary>
    /// Прогін перевірки узгодженості на вимогу. Право <c>System.RunJob</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>System.RunJob</c> — одне з восьми прав, які сід видає і які не
    /// перевіряв жоден обробник (<c>BE-28</c>). Це його перший викликач.
    ///
    /// ⚠ <c>202</c>, не <c>200</c>: перевірка ходить по всіх партиціях і в
    /// відповідь укластися не може. Стан клієнт дочитує тим самим
    /// <c>GET /api/v1/jobs/{jobId}</c>, яким уже показує будь-який прогрес.
    ///
    /// ⚠ Перевірка вже в черзі або вже виконується — <c>409</c>
    /// (<c>ECR-JOB-0409</c>) із її <c>jobId</c>, а не другий повний обхід тих
    /// самих таблиць.
    /// </remarks>
    [HttpPost("run")]
    [ProducesResponseType<Contracts.JobAcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Run(
        [FromBody] RunConsistencyCheckRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Право перевіряє обробник (архітектурне правило 7): воно небезпечне,
        // і рішення має ухвалюватися там само, де ставиться задача.
        var jobId = await run.HandleAsync(request.Reason, ct).ConfigureAwait(false);

        return Accepted(new Contracts.JobAcceptedResponse(jobId));
    }
}

/// <summary>Запит на прогін перевірки узгодженості.</summary>
/// <param name="Reason">
/// Причина; обов'язкова, потрапляє в журнал безпеки (<c>aud.SecurityEvent</c>).
/// </param>
public sealed record RunConsistencyCheckRequest(string Reason);
