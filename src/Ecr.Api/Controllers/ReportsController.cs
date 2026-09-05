using Ecr.Application.Reporting;
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
public sealed class ReportsController(
    ListReportSnapshotsHandler snapshots, BuildReportSnapshotHandler build) : ControllerBase
{
    /// <summary>Перелік побудованих зрізів. Право <c>Report.ViewRegulatory</c>.</summary>
    /// <remarks>
    /// У переліку є час побудови й контрольна сума: споживач має бачити, на
    /// яких даних побудовано зріз. Два зрізи однієї версії за один період
    /// відрізняються лише цим.
    /// </remarks>
    [HttpGet("snapshots")]
    [ProducesResponseType<IReadOnlyList<Ecr.Application.Ports.ReportSnapshotSummary>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Snapshots(
        [FromQuery] int? projectId, [FromQuery] int? periodKey, CancellationToken ct)
        => Ok(await snapshots.HandleAsync(projectId, periodKey, ct).ConfigureAwait(false));

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
    public async Task<IActionResult> Build(
        string code, [FromBody] BuildSnapshotRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var jobId = await build
            .HandleAsync(code, request.ProjectId, request.PeriodKey, ct)
            .ConfigureAwait(false);

        return Accepted(new { jobId });
    }
}

/// <summary>Запит на побудову зрізу.</summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="PeriodKey">Період.</param>
public sealed record BuildSnapshotRequest(int ProjectId, int PeriodKey);
