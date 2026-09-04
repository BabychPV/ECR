using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Зрізи для регламентної звітності.</summary>
/// <remarks>
/// ⚠ Самих звітів тут немає: звітність лишається в SSRS (<c>D-52</c>).
/// Наша межа — <c>rpt.*</c>: незмінний зріз, який SSRS читає.
/// </remarks>
[ApiController]
[Route("api/v1/reports")]
[Authorize]
public sealed class ReportsController : ControllerBase
{
    /// <summary>Перелік побудованих зрізів. Право <c>Report.ViewRegulatory</c>.</summary>
    [HttpGet("snapshots")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> Snapshots(
        [FromQuery] int? projectId, [FromQuery] int? periodKey, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити Report.ViewRegulatory; повернути зрізи з їхнім часом побудови " +
            "і контрольною сумою — споживач має бачити, на яких даних побудовано.");

    /// <summary>
    /// Будує зріз. Право <c>Report.BuildSnapshot</c>.
    /// </summary>
    /// <remarks>
    /// Зріз **незмінний**: повторна побудова створює новий, а не переписує
    /// старий. Інакше звіт, роздрукований учора, і той самий звіт сьогодні
    /// давали б різні числа без жодного сліду.
    /// </remarks>
    [HttpPost("{code}/build")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public Task<IActionResult> Build(string code, [FromBody] BuildSnapshotRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити Report.BuildSnapshot; поставити ReportSnapshotJob; 202 із jobId.");
}

/// <summary>Запит на побудову зрізу.</summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="PeriodKey">Період.</param>
public sealed record BuildSnapshotRequest(int ProjectId, int PeriodKey);
