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
    SetEntryValidityHandler setValidity,
    SwitchRegistrySourceHandler switchSource) : ControllerBase
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
    // ⛔ Тип відповіді оголошений ЯВНО, а тіло — іменований запис, а не
    // анонімний об'єкт. Інакше в схемі OpenAPI лишається порожня 200-ка,
    // згенерувати клієнтський тип ні з чого, і клієнт описує відповідь
    // рукописним інтерфейсом — з помилкою в назві поля, яку ніхто не
    // побачить (`A7-16`, `A7-32`).
    [ProducesResponseType<RegistryEntryIdResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<RegistryEntryIdResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Upsert(
        string code, [FromBody] RegistryEntryUpsertDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var isNew = dto.Id is null;
        var id = await upsert.HandleAsync(dto, ct).ConfigureAwait(false);

        // 201 для нового запису, 200 для оновлення: різниця видима клієнтові й
        // означає, чи з'явився новий Id, який тепер лежатиме в комірках.
        // ⚠ Один і той самий ІМЕНОВАНИЙ запис в обох гілках. Тут стояв
        // анонімний `new { id }` для 201 — рівно те, від чого застерігає
        // коментар вище: форма збігалася випадково, і перше ж перейменування
        // поля розвело б 200 і 201 мовчки.
        return isNew
            ? CreatedAtAction(nameof(Entries), new { code }, new RegistryEntryIdResponse(id))
            : Ok(new RegistryEntryIdResponse(id));
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
    [ProducesResponseType<AffectedRowsResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetValidity(
        string code, long id, [FromBody] SetValidityRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var affected = await setValidity
            .HandleAsync(id, request.From, request.To, ct)
            .ConfigureAwait(false);

        // Повертається кількість зачеплених рядків: той, хто звузив вікно, має
        // бачити масштаб наслідку, а не лише «ок».
        return Ok(new AffectedRowsResponse(affected));
    }

    /// <summary>
    /// Перемикає master-джерело <b>набору</b> довідників. Право <c>Integration.Manage</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Операція над НАБОРОМ, і сутності «група довідників» немає навмисно
    /// (<c>ФВ-13.10</c>): група — це факт одного перемикання, а не властивість
    /// довідника. Набір складає той, хто перемикає: він єдиний, хто знає, які
    /// довідники пов'язані <b>сьогодні</b>.
    ///
    /// ⚠ Усе або нічого: невідомий код у переліку відхиляє операцію цілком, а
    /// перевірка «немає відкритого періоду» робиться один раз на весь набір.
    /// Половина блоку в одному режимі, половина в іншому — гірше, ніж відмова.
    /// </remarks>
    [HttpPut("source-kind")]
    [ProducesResponseType<AffectedRowsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SwitchSourceKind(
        [FromBody] SwitchSourceKindRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var changed = await switchSource
            .HandleAsync(request.RegistryCodes, request.SourceKind, request.Reason, ct)
            .ConfigureAwait(false);

        // ⚠ Повертається, скільки СПРАВДІ змінилося, а не розмір набору: у
        // наборі постійно трапляються довідники, які вже в цільовому режимі,
        // і «перемкнуто 3» там, де змінився один, — це неправда в журналі.
        return Ok(new AffectedRowsResponse(changed));
    }
}

/// <summary>Запит на перемикання master-джерела набору довідників.</summary>
/// <param name="RegistryCodes">Коди довідників; порожній набір відхиляється.</param>
/// <param name="SourceKind">Нове джерело для всіх перелічених.</param>
/// <param name="Reason">
/// Причина. Обов'язкова: через рік питання «навіщо перемикали цей набір
/// разом» — єдине, на яке доведеться відповісти, і відповідь має бути в
/// журналі, а не в чиїйсь пам'яті.
/// </param>
public sealed record SwitchSourceKindRequest(
    IReadOnlyList<string> RegistryCodes,
    Ecr.Domain.Enums.RegistrySourceKind SourceKind,
    string Reason);

/// <summary>Запит на зміну вікна дії запису довідника.</summary>
/// <param name="From">Початок дії; <c>null</c> — без обмеження.</param>
/// <param name="To">Кінець дії; <c>null</c> — без обмеження.</param>
public sealed record SetValidityRequest(DateOnly? From, DateOnly? To);

/// <summary>Ідентифікатор запису довідника.</summary>
/// <param name="Id">Запис.</param>
public sealed record RegistryEntryIdResponse(long Id);

/// <summary>Скільки рядків зачепила операція.</summary>
/// <param name="AffectedRows">Кількість.</param>
public sealed record AffectedRowsResponse(int AffectedRows);
