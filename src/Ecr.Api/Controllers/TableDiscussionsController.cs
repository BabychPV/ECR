using Ecr.Application.Documents.Discussions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Ecr.Api.Controllers;

/// <summary>
/// Чат таблиці документа: ОДНА розмова на пару «документ × код таблиці», кожне повідомлення несе період, у якому його
/// написано. Простий текст, без HTML.
/// </summary>
/// <remarks>
/// ⛔ Читання — те саме рішення про доступ, що й зріз таблиці: невидимий документ і схована таблиця — одна відповідь
/// <c>404 ECR-DOC-0404</c> (існування розмови не розкривається). Запис — право <c>Document.Discuss</c> у проєкті
/// документа + доступ до таблиці; дозволений у будь-якому стані документа (коментар — не дані). Запис у чат НІКОЛИ не
/// торкається комірок, розрахунків і станів аркушів.
/// ⚠ Видалення м'яке: у відповіді <c>body = null</c>, <c>isDeleted = true</c>.
/// </remarks>
[ApiController]
[Route("api/v1/documents/{documentId:long}")]
[Authorize]
public sealed class TableDiscussionsController(
    TableDiscussionQueries queries,
    TableDiscussionCommands commands) : ControllerBase
{
    /// <summary>Підсумок чатів усіх таблиць документа одним запитом (лічильники й «непрочитано»).</summary>
    /// <remarks>
    /// Віддає лише таблиці, які користувач бачить і де є повідомлення. <c>404 ECR-DOC-0404</c> — документ невидимий.
    /// </remarks>
    [HttpGet("discussions/summary")]
    [ProducesResponseType<TableDiscussionSummaryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Summary(long documentId, CancellationToken ct)
        => Ok(await queries.GetSummaryAsync(documentId, ct).ConfigureAwait(false));

    /// <summary>Повідомлення чату таблиці: сторінка за ключем (<c>before</c>) або опитування (<c>after</c>).</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="tableCode">Код таблиці (стабільний між версіями шаблону).</param>
    /// <param name="before">Повернути повідомлення з <c>id</c> менше за це (старіші); без параметрів — найновіші.</param>
    /// <param name="after">Повернути повідомлення з <c>id</c> більше за це (опитування; зростання).</param>
    /// <param name="limit">1..100, за замовчуванням 50.</param>
    /// <param name="periodKey">Лише повідомлення цього періоду (фільтр «лише цей період»).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Відповідь несе <c>ETag</c> (ревізія чату + права читача); <c>If-None-Match</c> → <c>304</c> без тіла.
    /// <c>422 ECR-REQ-0422</c> — <c>before</c> разом з <c>after</c>, недійсні межі. <c>404</c> — документ чи таблиця невидимі.
    /// </remarks>
    [HttpGet("tables/{tableCode}/messages")]
    [ProducesResponseType<TableDiscussionPageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Messages(
        long documentId, string tableCode,
        [FromQuery] long? before, [FromQuery] long? after, [FromQuery] int? limit, [FromQuery] int? periodKey,
        CancellationToken ct)
    {
        var (page, etag) = await queries
            .ListMessagesAsync(
                documentId, tableCode, before, after, limit, periodKey,
                Request.Headers[HeaderNames.IfNoneMatch].ToString(), ct)
            .ConfigureAwait(false);

        Response.Headers[HeaderNames.ETag] = etag;
        Response.Headers[HeaderNames.CacheControl] = "private, no-cache";

        return page is null ? StatusCode(StatusCodes.Status304NotModified) : Ok(page);
    }

    /// <summary>Кандидати у згадки: лише користувачі, які самі бачать цю таблицю (≥ 2 символи, до 20).</summary>
    /// <remarks>Імена користувачів без доступу не повертаються ніколи. <c>404</c> — таблиця невидима.</remarks>
    [HttpGet("tables/{tableCode}/mention-candidates")]
    [ProducesResponseType<IReadOnlyList<TableDiscussionMentionCandidateDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MentionCandidates(
        long documentId, string tableCode, [FromQuery] string? q, CancellationToken ct)
        => Ok(await queries.MentionCandidatesAsync(documentId, tableCode, q, ct).ConfigureAwait(false));

    /// <summary>Пише повідомлення в чат таблиці. Право <c>Document.Discuss</c>.</summary>
    /// <remarks>
    /// <c>403 ECR-AUTH-0403</c> — немає <c>Document.Discuss</c>; <c>404 ECR-DOC-0404</c> — документ чи таблиця невидимі;
    /// <c>422 ECR-REQ-0422</c> (<c>discussionBodyInvalid</c>) — порожній чи задовгий текст, недійсний період,
    /// забагато згадок; <c>429</c> — ліміт запитів. Згадки користувачів без доступу мовчки відкидаються — у відповіді
    /// лише прийняті.
    /// </remarks>
    [HttpPost("tables/{tableCode}/messages")]
    [ProducesResponseType<TableDiscussionMessageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Post(
        long documentId, string tableCode, [FromBody] PostTableDiscussionMessageRequest request, CancellationToken ct)
        => Ok(await commands.PostAsync(documentId, tableCode, request, ct).ConfigureAwait(false));

    /// <summary>Правка свого повідомлення впродовж 15 хвилин після створення.</summary>
    /// <remarks>
    /// <c>403 ECR-ACCS-0403</c> (<c>discussionEditForbidden</c>) — повідомлення чуже; <c>422 ECR-REQ-0422</c>
    /// (<c>discussionEditWindowClosed</c>) — вікно закрито, (<c>discussionMessageDeleted</c>) — вже видалено,
    /// (<c>discussionBodyInvalid</c>) — текст недійсний; <c>404</c> — таблиця невидима чи повідомлення немає в цьому чаті.
    /// </remarks>
    [HttpPatch("tables/{tableCode}/messages/{messageId:long}")]
    [ProducesResponseType<TableDiscussionMessageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Edit(
        long documentId, string tableCode, long messageId,
        [FromBody] EditTableDiscussionMessageRequest request, CancellationToken ct)
        => Ok(await commands.EditAsync(documentId, tableCode, messageId, request, ct).ConfigureAwait(false));

    /// <summary>М'яке видалення: власне повідомлення або будь-яке — з правом <c>Document.DiscussModerate</c>.</summary>
    /// <remarks>
    /// Повторне видалення — <c>204</c> без змін. <c>403 ECR-ACCS-0403</c> (<c>discussionDeleteForbidden</c>) — чуже без
    /// права модерації; <c>404</c> — таблиця невидима чи повідомлення немає в цьому чаті.
    /// </remarks>
    [HttpDelete("tables/{tableCode}/messages/{messageId:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long documentId, string tableCode, long messageId, CancellationToken ct)
    {
        await commands.DeleteAsync(documentId, tableCode, messageId, ct).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Позначка «прочитано до» (монотонна); знімає згадки користувача до цього повідомлення.</summary>
    /// <remarks>Значення понад найбільший <c>id</c> чату зрізається до нього. <c>404</c> — таблиця невидима.</remarks>
    [HttpPut("tables/{tableCode}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> MarkRead(
        long documentId, string tableCode, [FromBody] MarkTableDiscussionReadRequest request, CancellationToken ct)
    {
        await commands.MarkReadAsync(documentId, tableCode, request, ct).ConfigureAwait(false);
        return NoContent();
    }
}
