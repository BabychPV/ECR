using Ecr.Application.Registries;
using Ecr.Application.Registries.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Довідники: перелік, записи, редагування, вікно дії.</summary>
[ApiController]
[Route("api/v1/registries")]
[Authorize]
public sealed class RegistriesController(
    ListRegistriesHandler listRegistries,
    GetRegistryEntriesHandler getEntries,
    UpsertRegistryEntryHandler upsert,
    SetEntryValidityHandler setValidity) : ControllerBase
{
    /// <summary>Перелік довідників. Право <c>Registry.View</c>.</summary>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<RegistryDefDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RegistryDefDto>>> List(CancellationToken ct)
        => Ok(await listRegistries.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Записи довідника на дату. Право <c>Registry.View</c>.
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="asOf">Дата періоду.</param>
    /// <param name="parentEntryId">Обраний батьківський запис для каскаду.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// <paramref name="asOf"/> обов'язковий за змістом: довідники темпоральні,
    /// і «поточний» набір записів залежить від дати періоду, а не від «сьогодні».
    /// Мовчазна підстановка сьогоднішньої дати давала б інші числа при
    /// перерахунку старого періоду.
    /// </remarks>
    [HttpGet("{code}/entries")]
    [ProducesResponseType<IReadOnlyList<RegistryEntryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RegistryEntryDto>>> Entries(
        string code, [FromQuery] DateOnly asOf, [FromQuery] long? parentEntryId, CancellationToken ct)
        => Ok(await getEntries.HandleAsync(code, asOf, parentEntryId, ct).ConfigureAwait(false));

    /// <summary>Створює або оновлює запис. Право <c>Registry.EditData</c>.</summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="dto">Опис запису.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// <c>Registry.EditData</c> і <c>Registry.EditDefinition</c> — різні права:
    /// змінювати значення і змінювати склад полів довідника може не той самий
    /// користувач.
    /// </remarks>
    [HttpPost("{code}/entries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> Upsert(
        string code, [FromBody] RegistryEntryUpsertDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var isNew = dto.Id is null;
        var id = await upsert.HandleAsync(dto, ct).ConfigureAwait(false);

        // 201 для нового запису, 200 для оновлення: різниця видима клієнтові й
        // означає, чи з'явився новий Id, який тепер лежатиме в комірках.
        return isNew
            ? CreatedAtAction(nameof(Entries), new { code }, new { id })
            : Ok(new { id });
    }

    /// <summary>
    /// Змінює вікно дії запису. Право <c>Registry.EditData</c>.
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="id">Запис.</param>
    /// <param name="request">Нове вікно.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Запис не видаляється, а закривається датою: у комірці зберігається
    /// <c>Id</c>, і видалення зробило б історичні документи нечитабельними.
    /// </remarks>
    [HttpPost("{code}/entries/{id:long}/validity")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetValidity(
        string code, long id, [FromBody] SetValidityRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var affected = await setValidity
            .HandleAsync(id, request.From, request.To, ct)
            .ConfigureAwait(false);

        // Повертається кількість зачеплених рядків: той, хто звузив вікно, має
        // бачити масштаб наслідку, а не лише «ок».
        return Ok(new { affectedRows = affected });
    }
}

/// <summary>Запит на зміну вікна дії запису довідника.</summary>
/// <param name="From">Початок дії; <c>null</c> — без обмеження.</param>
/// <param name="To">Кінець дії; <c>null</c> — без обмеження.</param>
public sealed record SetValidityRequest(DateOnly? From, DateOnly? To);
