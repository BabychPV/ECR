using Ecr.Application.Common;
using Ecr.Application.Registries.Rows;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Дані довідника рядками для редактора (FEATURE-REGISTRY-TABLES §7.1, §8.4): читання — RT-13,
/// пакетний запис — RT-14, історія — RT-15, експорт — RT-16.
/// </summary>
/// <remarks>
/// ⚠ Окремий контролер, а не ще дії в <see cref="RegistriesController"/>: той уже несе сімнадцять
/// обробників, а редактор даних росте власними кроками.
/// </remarks>
[ApiController]
[Route("api/v1/registries")]
[Authorize]
public sealed class RegistryRowsController(GetRegistryRowsHandler getRows) : ControllerBase
{
    /// <summary>Префікс параметра фільтра поля: <c>field.&lt;КОД&gt;=значення</c>.</summary>
    private const string FieldFilterPrefix = "field.";

    /// <summary>
    /// Рядки довідника зі значеннями полів, сторінками за курсором. Право <c>Registry.View</c> або
    /// грант <c>Read</c> на довідник.
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="asOf">Бізнес-дата чинності; обов'язкова для темпорального довідника.</param>
    /// <param name="asOfUtc">Системний момент (UTC) — значення «станом на»; немає — поточні.</param>
    /// <param name="parentEntryId">Батько композиції або каскаду.</param>
    /// <param name="q">Підрядок коду, назви або текстового поля.</param>
    /// <param name="cursor">Курсор попередньої сторінки.</param>
    /// <param name="limit">Розмір сторінки, 1…500.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Фільтри полів — параметри <c>field.&lt;КОД&gt;=значення</c> у поданні <c>values[].value</c>.
    /// </remarks>
    [HttpGet("{code}/rows")]
    [ProducesResponseType<PagedResult<RegistryRowDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PagedResult<RegistryRowDto>>> Rows(
        string code,
        [FromQuery] DateOnly asOf,
        [FromQuery] DateTimeOffset? asOfUtc,
        [FromQuery] long? parentEntryId,
        [FromQuery] string? q,
        [FromQuery] string? cursor,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var fields = Request.Query
            .Where(p => p.Key.StartsWith(FieldFilterPrefix, StringComparison.OrdinalIgnoreCase)
                        && p.Key.Length > FieldFilterPrefix.Length)
            .ToDictionary(p => p.Key[FieldFilterPrefix.Length..], p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        var request = new RegistryRowsRequest(
            code, asOf, asOfUtc?.UtcDateTime, parentEntryId, q, fields, new CursorRequest(limit, cursor));

        return Ok(await getRows.HandleAsync(request, ct).ConfigureAwait(false));
    }
}
