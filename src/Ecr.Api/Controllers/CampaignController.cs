using Ecr.Application.Reporting;
using Ecr.Application.Reporting.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Огляд кампанії звітності: усі проєкти одного періоду (<c>BE-22</c>).</summary>
/// <remarks>
/// ⛔ Окремий контролер, а не дія в <c>ReportsController</c>: там межа — це
/// <c>rpt.*</c>, опис звіту і зріз. Тут же питання інше й ширше за звітність —
/// «на якому етапі кампанія в кожному проєкті», і відповідь складається з
/// документів, аркушів і зрізів разом.
/// </remarks>
[ApiController]
[Route("api/v1/campaign")]
[Authorize]
public sealed class CampaignController(GetCampaignSummaryHandler summary) : ControllerBase
{
    /// <summary>
    /// Зведення кампанії за період. Право <c>Report.ViewCampaign</c>.
    /// </summary>
    /// <param name="periodKey">Період кампанії (<c>Рік*100 + Номер</c>).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Період — обов'язковий параметр запиту, а не сегмент шляху: кампанія
    /// не є ресурсом, який можна відкрити чи змінити, і <c>/campaign/202601</c>
    /// обіцяв би саме це. Сусідні зведення адресуються так само
    /// (<c>GET /documents/summary?periodKey</c>).
    ///
    /// ⛔ Перелік проєктів НЕ звужується грантами користувача — рішення людини
    /// <c>Q15-07</c>. Замість межі видимості тут окреме право, яке видають
    /// свідомо; докладніше — у <see cref="GetCampaignSummaryHandler"/>.
    /// </remarks>
    [HttpGet("summary")]
    [ProducesResponseType<CampaignSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Summary([FromQuery] int periodKey, CancellationToken ct)
        => Ok(await summary.HandleAsync(periodKey, ct).ConfigureAwait(false));
}
