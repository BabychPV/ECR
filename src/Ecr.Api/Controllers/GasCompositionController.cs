using Ecr.Application.GasComposition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Склад газу (D-NEW, людина 24.11): потоки, кейси й версії складу — власник ECR. Право читання
/// <c>GasComposition.View</c>, змін — <c>GasComposition.Edit</c>.
/// </summary>
/// <remarks>⚠ У FLERT тут не пишеться нічого: FLERT лише читається один раз імпортом (<c>POST import</c>).</remarks>
[ApiController]
[Route("api/v1/gas-composition")]
[Authorize]
public sealed class GasCompositionController(GasCompositionService service) : ControllerBase
{
    /// <summary>Перелік потоків зі зведенням версій, право змін і стан імпорту з FLERT.</summary>
    /// <param name="ct">Скасування.</param>
    [HttpGet("streams")]
    [ProducesResponseType<GasStreamListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListStreams(CancellationToken ct)
        => Ok(await service.ListAsync(ct).ConfigureAwait(false));

    /// <summary>Компоненти для редактора складу.</summary>
    /// <param name="ct">Скасування.</param>
    [HttpGet("components")]
    [ProducesResponseType<IReadOnlyList<GasComponentDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListComponents(CancellationToken ct)
        => Ok(await service.ListComponentsAsync(ct).ConfigureAwait(false));

    /// <summary>Потік з кейсами й версіями.</summary>
    /// <param name="id">Запис потоку.</param>
    /// <param name="ct">Скасування.</param>
    [HttpGet("streams/{id:long}")]
    [ProducesResponseType<GasStreamDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStream(long id, CancellationToken ct)
        => Ok(await service.GetStreamAsync(id, ct).ConfigureAwait(false));

    /// <summary>Новий потік (Id = найбільший + 1).</summary>
    /// <param name="request">Атрибути.</param>
    /// <param name="ct">Скасування.</param>
    [HttpPost("streams")]
    [ProducesResponseType<GasChangeResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateStream([FromBody] GasStreamRequest request, CancellationToken ct)
        => Ok(await service.CreateStreamAsync(request, ct).ConfigureAwait(false));

    /// <summary>Зміна атрибутів потоку.</summary>
    /// <param name="id">Запис потоку.</param>
    /// <param name="request">Атрибути.</param>
    /// <param name="ct">Скасування.</param>
    [HttpPut("streams/{id:long}")]
    [ProducesResponseType<GasChangeResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UpdateStream(long id, [FromBody] GasStreamRequest request, CancellationToken ct)
        => Ok(await service.UpdateStreamAsync(id, request, ct).ConfigureAwait(false));

    /// <summary>Логічне видалення потоку з усіма версіями.</summary>
    /// <param name="id">Запис потоку.</param>
    /// <param name="ct">Скасування.</param>
    [HttpDelete("streams/{id:long}")]
    [ProducesResponseType<GasChangeResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteStream(long id, CancellationToken ct)
        => Ok(await service.DeleteStreamAsync(id, ct).ConfigureAwait(false));

    /// <summary>Нова версія кейсу (або перша версія нового кейсу).</summary>
    /// <param name="id">Запис потоку.</param>
    /// <param name="request">Версія.</param>
    /// <param name="ct">Скасування.</param>
    [HttpPost("streams/{id:long}/versions")]
    [ProducesResponseType<GasChangeResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateVersion(long id, [FromBody] GasVersionCreateRequest request, CancellationToken ct)
        => Ok(await service.CreateVersionAsync(id, request, ct).ConfigureAwait(false));

    /// <summary>Перейменування кейсу потоку (усіх його версій).</summary>
    /// <param name="id">Запис потоку.</param>
    /// <param name="request">Стара й нова назва.</param>
    /// <param name="ct">Скасування.</param>
    [HttpPut("streams/{id:long}/cases")]
    [ProducesResponseType<GasChangeResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RenameCase(long id, [FromBody] GasCaseRenameRequest request, CancellationToken ct)
        => Ok(await service.RenameCaseAsync(id, request, ct).ConfigureAwait(false));

    /// <summary>Зміна версії (значення і/або місяць початку).</summary>
    /// <param name="versionId">Версія.</param>
    /// <param name="request">Нові значення.</param>
    /// <param name="ct">Скасування.</param>
    [HttpPut("versions/{versionId:long}")]
    [ProducesResponseType<GasChangeResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UpdateVersion(long versionId, [FromBody] GasVersionUpdateRequest request, CancellationToken ct)
        => Ok(await service.UpdateVersionAsync(versionId, request, ct).ConfigureAwait(false));

    /// <summary>Видалення версії (попередня продовжується на її вікно).</summary>
    /// <param name="versionId">Версія.</param>
    /// <param name="ct">Скасування.</param>
    [HttpDelete("versions/{versionId:long}")]
    [ProducesResponseType<GasChangeResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteVersion(long versionId, CancellationToken ct)
        => Ok(await service.DeleteVersionAsync(versionId, ct).ConfigureAwait(false));

    /// <summary>Разовий імпорт із FLERT (лише в порожню базу): 202 з <c>jobId</c>.</summary>
    /// <param name="ct">Скасування.</param>
    [HttpPost("import")]
    [ProducesResponseType<Contracts.JobAcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Import(CancellationToken ct)
    {
        var jobId = await service.StartImportAsync(ct).ConfigureAwait(false);
        return Accepted(new Contracts.JobAcceptedResponse(jobId));
    }
}
