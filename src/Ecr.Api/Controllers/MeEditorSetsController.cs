using Ecr.Application.Documents.EditAssignments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Набори виконавців власника: щоб повторити передачу наступного місяця одним кліком.</summary>
/// <remarks>⛔ Лише власні набори; чужий набір ≡ відсутній (<c>404</c>).</remarks>
[ApiController]
[Route("api/v1/me/editor-sets")]
[Authorize]
public sealed class MeEditorSetsController(EditorSetsHandler sets) : ControllerBase
{
    /// <summary>Набори поточного користувача.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<EditorSetDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await sets.ListAsync(ct).ConfigureAwait(false));

    /// <summary>Створює набір.</summary>
    [HttpPost]
    [ProducesResponseType<EditorSetDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] SaveEditorSetRequest request, CancellationToken ct)
        => Ok(await sets.CreateAsync(request, ct).ConfigureAwait(false));

    /// <summary>Змінює назву й склад набору (<c>baseVersion</c> обов'язкова).</summary>
    [HttpPut("{setId:int}")]
    [ProducesResponseType<EditorSetDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Update(int setId, [FromBody] SaveEditorSetRequest request, CancellationToken ct)
        => Ok(await sets.UpdateAsync(setId, request, ct).ConfigureAwait(false));

    /// <summary>Видаляє набір (вже створені передачі лишаються).</summary>
    [HttpDelete("{setId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int setId, CancellationToken ct)
    {
        await sets.DeleteAsync(setId, ct).ConfigureAwait(false);
        return NoContent();
    }
}