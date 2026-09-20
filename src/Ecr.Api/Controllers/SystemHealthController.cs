using System.Diagnostics;
using System.Reflection;
using Ecr.Application.Health;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Факти про піднятий процес і команда для DBA (<c>BE-18</c>).</summary>
/// <remarks>
/// ⚠ Префікс <c>/api/v1/health</c>, а не <c>/health</c>: <c>/health/*</c> —
/// middleware перевірок для моніторингу (<c>Program.cs</c>), яке не знає про
/// функціональні права. Тут право перевіряє обробник, як у решті API.
/// </remarks>
[ApiController]
[Route("api/v1/health")]
[Authorize]
public sealed class SystemHealthController(
    GetSystemFactsHandler facts,
    GetPartitionScriptHandler partitionScript,
    IHostEnvironment environment,
    Ecr.Api.Observability.FileLogStatus fileLog) : ControllerBase
{
    /// <summary>
    /// Версія, час старту, середовище, транспорт сповіщень, тека журналу. Право <c>System.ViewHealth</c>.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("facts")]
    [ProducesResponseType<SystemFactsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SystemFactsResponse>> Facts(CancellationToken ct)
    {
        using var process = Process.GetCurrentProcess();

        var host = new SystemHostFacts(
            typeof(SystemHealthController).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion,
            process.StartTime.ToUniversalTime(),
            environment.EnvironmentName,
            fileLog.Directory);

        return Ok(await facts.HandleAsync(host, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Готова команда для DBA на створення наступних партицій. Право <c>System.ViewHealth</c>.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Лише ТЕКСТ (<c>D15-12</c>): застосунок не виконує DDL (<c>D-66</c>).
    /// </remarks>
    [HttpGet("partitions/script")]
    [Produces("text/plain")]
    [ProducesResponseType<string>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> PartitionScript(CancellationToken ct)
        => Content(await partitionScript.HandleAsync(ct).ConfigureAwait(false), "text/plain");
}
