using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Імпорт пакета методологій з AF (крок V, FEATURE-HSE301-VIEW §11.6).
/// </summary>
/// <remarks>
/// ⚠ Окремий контролер, а не ще одна дія в <see cref="MethodologiesController"/>: той уже тримає
/// два десятки обробників, а імпорт не адресується жодною методологією — він їх створює.
/// </remarks>
[ApiController]
[Route("api/v1/methodologies")]
[Authorize]
public sealed class MethodologyImportController(ImportMethodologyPackageHandler import) : ControllerBase
{
    /// <summary>
    /// Імпортує пакет <c>ecr-methodology-package</c> v1: методології, версії-чернетки,
    /// формули, константи, імпорти між методологіями. Права <c>Calculation.EditFormula</c> і
    /// <c>Calculation.EditConstant</c>.
    /// </summary>
    /// <param name="package">Пакет, як його пише <c>Ecr.MethodologyImport export</c>.</param>
    /// <param name="dryRun"><c>true</c> — нічого не писати, лише звіт (зокрема з блокерами).</param>
    /// <param name="timeZone">Пояс майданчика (IANA) для дат AF; за замовчуванням <c>Asia/Atyrau</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Лише чернетки: публікує інша людина звичайною дією публікації (чотири ока, золотий
    /// набір). Блокери — <c>422</c>, розбіжність із наявною версією — <c>409</c>; обидва зі
    /// звітом у <c>report</c> і без жодного запису. Повторний імпорт того самого пакета —
    /// <c>200</c> з <c>outcome = unchanged</c>.
    /// </remarks>
    [HttpPost("import")]
    [RequestSizeLimit(64 * 1024 * 1024)]
    [ProducesResponseType<MethodologyImportReportDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MethodologyImportReportDto>> Import(
        [FromBody] MethodologyPackageDto package,
        [FromQuery] bool dryRun,
        [FromQuery] string? timeZone,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);

        return Ok(await import.HandleAsync(package, dryRun, timeZone, ct).ConfigureAwait(false));
    }
}
