using System.Text.Json;
using Ecr.Application.Preferences;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Налаштування інтерфейсу поточного користувача (<c>BE-20</c>).</summary>
/// <remarks>
/// Лише власні: користувача бере обробник із сеансу, ідентифікатора в маршруті
/// немає. Ключ — з білого списку (<c>theme</c>, <c>density</c>, <c>language</c>,
/// <c>lastPeriodKey</c>, <c>lastProjectId</c>, <c>grid.*</c>), значення — JSON до 8 КБ,
/// не більше 200 ключів; порушення — <c>422 ECR-REQ-0422</c>.
/// </remarks>
[ApiController]
[Route("api/v1/me/preferences")]
[Authorize]
public sealed class PreferencesController(
    ListUserPreferencesHandler list, PutUserPreferenceHandler put, DeleteUserPreferenceHandler delete)
    : ControllerBase
{
    /// <summary>Усі налаштування поточного користувача, за ключем.</summary>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<UserPreferenceDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<UserPreferenceDto>>> List(CancellationToken ct)
        => Ok(await list.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>Створює або замінює налаштування; тіло — саме JSON-значення.</summary>
    /// <param name="key">Ключ налаштування.</param>
    /// <param name="value">Значення: рядок, число, об'єкт, масив.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPut("{key}")]
    [ProducesResponseType<UserPreferenceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<UserPreferenceDto>> Put(
        string key, [FromBody] JsonElement value, CancellationToken ct)
    {
        var raw = value.ValueKind == JsonValueKind.Undefined ? null : value.GetRawText();
        return Ok(await put.HandleAsync(key, raw, ct).ConfigureAwait(false));
    }

    /// <summary>Видаляє налаштування; відсутнє — теж <c>204</c>.</summary>
    /// <param name="key">Ключ налаштування.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpDelete("{key}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Delete(string key, CancellationToken ct)
    {
        await delete.HandleAsync(key, ct).ConfigureAwait(false);
        return NoContent();
    }
}
