using Ecr.Application.Common;
using Ecr.Application.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Правила сповіщень і журнал доставок (<c>BE-33</c>). Право
/// <c>System.ManageNotifications</c>.
/// </summary>
/// <remarks>
/// ⚠ Окремий контролер від <see cref="NotificationChannelsController"/>: той
/// прив'язаний маршрутом до <c>…/channels</c>, а правила й журнал лежать поруч
/// із каналами, не всередині них.
/// </remarks>
[ApiController]
[Route("api/v1/notifications")]
[Authorize]
public sealed class NotificationRulesController(
    GetNotificationRulesHandler rules,
    ReplaceNotificationRulesHandler replace,
    ListNotificationDeliveriesHandler deliveries) : ControllerBase
{
    /// <summary>Матриця «подія × канал» цілком.</summary>
    /// <remarks>
    /// ⚠ <c>eventKinds</c> перелічує УСІ види подій, навіть ті, на які правила
    /// ще немає: порожня клітинка матриці — це «правила немає», а не «такої
    /// події не буває».
    /// </remarks>
    [HttpGet("rules")]
    [ProducesResponseType<NotificationRuleMatrix>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Rules(CancellationToken ct)
        => Ok(await rules.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>Замінює матрицю цілком; клітинка, якої немає в тілі, зникає.</summary>
    /// <remarks>
    /// ⚠ Ідемпотентно: те саме тіло двічі дає той самий стан і не дублює
    /// правил. Правило на неіснуючий канал — <c>404</c>, дві клітинки з однією
    /// парою «подія + канал» — <c>422</c>.
    /// </remarks>
    [HttpPut("rules")]
    [ProducesResponseType<NotificationRuleMatrix>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ReplaceRules(
        [FromBody] ReplaceNotificationRulesRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await replace.HandleAsync(request.Rules, ct).ConfigureAwait(false));
    }

    /// <summary>Журнал доставок, новіші першими.</summary>
    /// <remarks>
    /// ⛔ Ні секрету каналу, ні тіла повідомлення в журналі немає — лише
    /// подія, канал, підсумок і причина відмови.
    ///
    /// ⚠ Стеля сторінки — 200 (<see cref="ListNotificationDeliveriesHandler.MaxLimit"/>),
    /// менша за спільну: більше тут не читає ніхто, а базі коштує.
    ///
    /// ⚠ Фільтри необов'язкові й звужують ЗАПИТ, а не видачу: у шухляді каналу
    /// журнал відкривають саме з <c>channelId</c>, і сторінка на 50 рядків,
    /// відфільтрована після вибірки, віддавала б там два.
    /// </remarks>
    /// <param name="limit">Розмір сторінки 1..200; <c>0</c> — типове значення 50.</param>
    /// <param name="cursor">Курсор наступної сторінки.</param>
    /// <param name="channelId">Лише доставки цього каналу; без нього — усі.</param>
    /// <param name="status">
    /// Лише цей підсумок (<c>Sent</c>, <c>Failed</c>, <c>Suppressed</c>);
    /// невідоме значення — <c>422 ECR-REQ-0422</c>, а не мовчазне «усі».
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("deliveries")]
    [ProducesResponseType<PagedResult<NotificationDeliveryView>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Deliveries(
        [FromQuery] int limit,
        [FromQuery] string? cursor,
        [FromQuery] int? channelId,
        [FromQuery] string? status,
        CancellationToken ct)
        => Ok(await deliveries
            .HandleAsync(new CursorRequest(limit == 0 ? 50 : limit, cursor), channelId, status, ct)
            .ConfigureAwait(false));
}

/// <summary>Тіло заміни матриці правил.</summary>
/// <param name="Rules">Заповнені клітинки; порожній перелік прибирає всі правила.</param>
public sealed record ReplaceNotificationRulesRequest(IReadOnlyList<NotificationRuleView> Rules);
