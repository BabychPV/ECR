using Ecr.Application.Integration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Синк складу газу FLERT → дзеркало ECR (<c>D-476</c>, крок F3).</summary>
/// <remarks>⚠ Запису у FLERT тут немає і не буде: копія ECR — лише для читання, master — FLERT (<c>D-44</c>, <c>D-476</c>).</remarks>
[ApiController]
[Route("api/v1/integration/flert-sync")]
[Authorize]
public sealed class FlertSyncController(SyncFlertNowHandler syncNow, GetFlertSyncStatusHandler status) : ControllerBase
{
    /// <summary>Ставить синк у чергу позачергово («Синхронізувати зараз»). Право <c>Integration.Manage</c>.</summary>
    /// <param name="ct">Скасування.</param>
    /// <remarks>⚠ 202 з <c>jobId</c>: синк ходить до FLERT. Поки попередній у черзі — той самий <c>jobId</c> (злиття).</remarks>
    [HttpPost]
    [ProducesResponseType<Contracts.JobAcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SyncNow(CancellationToken ct)
    {
        var jobId = await syncNow.HandleAsync(ct).ConfigureAwait(false);

        return Accepted(new Contracts.JobAcceptedResponse(jobId));
    }

    /// <summary>
    /// Стан синку: чи увімкнено, де показувати (коди довідників-дзеркала) і останній прогін.
    /// Право <c>Integration.View</c> або <c>Integration.Manage</c>.
    /// </summary>
    /// <param name="ct">Скасування.</param>
    [HttpGet("status")]
    [ProducesResponseType<Ecr.Application.Ports.FlertSyncStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Status(CancellationToken ct)
        => Ok(await status.HandleAsync(ct).ConfigureAwait(false));
}
