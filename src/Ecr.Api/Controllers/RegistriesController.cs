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
    GetRegistryEntriesHandler getEntries,
    UpsertRegistryEntryHandler upsert,
    SetEntryValidityHandler setValidity) : ControllerBase
{
    /// <summary>Перелік довідників. Право <c>Registry.View</c>.</summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> List(CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перелік cfg.RegistryDef із їхніми полями — це метадані, не дані.");

    /// <summary>
    /// Записи довідника на дату. Право <c>Registry.View</c>.
    /// </summary>
    /// <remarks>
    /// <paramref name="asOf"/> обов'язковий за змістом: довідники темпоральні,
    /// і «поточний» набір записів залежить від дати періоду, а не від «сьогодні».
    /// Мовчазна підстановка сьогоднішньої дати давала б інші числа при
    /// перерахунку старого періоду.
    /// </remarks>
    [HttpGet("{code}/entries")]
    [ProducesResponseType<IReadOnlyList<RegistryEntryDto>>(StatusCodes.Status200OK)]
    public Task<ActionResult<IReadOnlyList<RegistryEntryDto>>> Entries(
        string code, [FromQuery] DateOnly asOf, [FromQuery] long? parentEntryId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: делегувати getEntries.HandleAsync(code, asOf, parentEntryId, ct).");

    /// <summary>Створює або оновлює запис. Право <c>Registry.EditData</c>.</summary>
    /// <remarks>
    /// <c>Registry.EditData</c> і <c>Registry.EditDefinition</c> — різні права:
    /// змінювати значення і змінювати склад полів довідника може не той самий
    /// користувач.
    /// </remarks>
    [HttpPost("{code}/entries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public Task<IActionResult> Upsert(string code, [FromBody] RegistryEntryUpsertDto dto, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити Registry.EditData; делегувати upsert.HandleAsync(dto, ct); " +
            "201 для нового запису, 200 для оновлення.");

    /// <summary>
    /// Змінює вікно дії запису. Право <c>Registry.EditData</c>.
    /// </summary>
    /// <remarks>
    /// Запис не видаляється, а закривається датою: у комірці зберігається
    /// <c>Id</c>, і видалення зробило б історичні документи нечитабельними.
    /// </remarks>
    [HttpPost("{code}/entries/{id:long}/validity")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> SetValidity(
        string code, long id, [FromBody] SetValidityRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: делегувати setValidity.HandleAsync(id, request.From, request.To, ct); " +
            "повернути кількість зачеплених документів — користувач має бачити наслідок.");
}

/// <summary>Запит на зміну вікна дії запису довідника.</summary>
/// <param name="From">Початок дії; <c>null</c> — без обмеження.</param>
/// <param name="To">Кінець дії; <c>null</c> — без обмеження.</param>
public sealed record SetValidityRequest(DateOnly? From, DateOnly? To);
