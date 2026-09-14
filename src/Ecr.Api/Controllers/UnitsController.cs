using Ecr.Application.Ports;
using Ecr.Application.Units;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Одиниці вимірювання і конверсії.</summary>
[ApiController]
[Route("api/v1/units")]
[Authorize]
public sealed class UnitsController(
    ListUnitsHandler list, ConvertUnitHandler convert, CreateUnitHandler create) : ControllerBase
{
    /// <summary>Перелік одиниць із їхніми розмірностями.</summary>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Розмірність віддається разом із одиницею: без неї клієнт не може
    /// перевірити нічого — ні того, що конверсія можлива, ні того, що
    /// величини сумісні (ФВ-16.2).
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<UnitRef>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<UnitRef>>> List(CancellationToken ct)
        => Ok(await list.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Заводить нову похідну одиницю (UI-аудит, lane 4: доти жоден шлях,
    /// доступний людині, не існував — `Uom.EditCatalog`).
    /// </summary>
    /// <param name="request">Код, позначення, назва, розмірність, коефіцієнти.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost]
    [ProducesResponseType<UnitRef>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UnitRef>> Create(
        [FromBody] CreateUnitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var unit = await create
            .HandleAsync(
                request.Code, request.SymbolL10n, request.NameL10n,
                request.DimensionId, request.FactorToBase, request.OffsetToBase, ct)
            .ConfigureAwait(false);

        return Ok(new UnitRef(unit.Id, unit.Code, unit.DimensionId, unit.FactorToBase, unit.OffsetToBase));
    }

    /// <summary>
    /// Конвертує значення між одиницями.
    /// </summary>
    /// <param name="request">Значення і коди одиниць.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Конверсія можлива <b>лише в межах однієї розмірності</b>. Перехід
    /// «маса ↔ об'єм» — не конверсія, а контекстний коефіцієнт (щільність), і
    /// він живе в константах методології, а не в <c>uom.Conversion</c>
    /// (ФВ-16.5, <c>D-75</c>).
    /// </remarks>
    [HttpPost("convert")]
    // ⛔ Тип відповіді оголошений ЯВНО, а тіло — іменований запис, а не
    // анонімний об'єкт. Інакше в схемі OpenAPI лишається порожня 200-ка,
    // згенерувати клієнтський тип ні з чого, і клієнт описує відповідь
    // рукописним інтерфейсом — з помилкою в назві поля, яку ніхто не
    // побачить (`A7-16`, `A7-32`).
    [ProducesResponseType<ConvertUnitResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Convert(
        [FromBody] ConvertUnitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var value = await convert
            .HandleAsync(request.Value, request.FromUnit, request.ToUnit, ct)
            .ConfigureAwait(false);

        return Ok(new ConvertUnitResponse(value, request.ToUnit));
    }
}

/// <summary>Запит на конверсію.</summary>
/// <param name="Value">Значення; <c>decimal</c>, бо <c>float</c> заборонений (<c>D-30</c>).</param>
/// <param name="FromUnit">Код вихідної одиниці.</param>
/// <param name="ToUnit">Код цільової одиниці.</param>
public sealed record ConvertUnitRequest(decimal Value, string FromUnit, string ToUnit);

/// <summary>Результат конверсії одиниць.</summary>
/// <param name="Value">Значення у цільовій одиниці.</param>
/// <param name="Unit">Код цільової одиниці.</param>
public sealed record ConvertUnitResponse(decimal Value, string Unit);

/// <summary>Запит на заведення нової похідної одиниці.</summary>
/// <param name="Code">Код, унікальний серед одиниць.</param>
/// <param name="SymbolL10n">Позначення мовами каталогу.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="DimensionId">Розмірність — має існувати в <c>uom.Dimension</c>.</param>
/// <param name="FactorToBase">Множник переходу до базової одиниці розмірності.</param>
/// <param name="OffsetToBase">Зсув; ненульовий лише для одиниць температури.</param>
public sealed record CreateUnitRequest(
    string Code,
    IReadOnlyDictionary<string, string> SymbolL10n,
    IReadOnlyDictionary<string, string> NameL10n,
    byte DimensionId,
    decimal FactorToBase,
    decimal OffsetToBase);
