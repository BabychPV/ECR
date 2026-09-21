// src/Ecr.Api/Controllers/EntityFieldMapsController.cs
using Ecr.Application.Sources;
using Ecr.Domain.Entities.External;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Мапінг поля зовнішнього джерела на колонку документа або поле реєстру
/// (<c>ext.EntityFieldMap</c>).
/// </summary>
/// <remarks>
/// ⛔ Прогалина 1 директиви паритету зі старою системою. До цього контролера
/// заведення мапінгу мало лише один шлях — ручний SQL: домен мав фабрики
/// (<see cref="EntityFieldMap.ToColumn"/>, <see cref="EntityFieldMap.ToRegistryField"/>),
/// але жоден обробник застосунку не викликав їх поза тестами. Перегляд
/// (<c>GET /api/v1/sources/{id}/mapping/preview</c>) лишається READ-ONLY і не
/// чіпається.
/// </remarks>
[ApiController]
[Route("api/v1/entity-field-maps")]
[Authorize]
public sealed class EntityFieldMapsController(
    CreateEntityFieldMapHandler create,
    SetEntityFieldMapPausedHandler pause,
    AcceptSourceUnitChangeHandler acceptUnit,
    DeleteEntityFieldMapHandler delete) : ControllerBase
{
    /// <summary>
    /// Заводить мапінг. Право <c>Integration.Manage</c>.
    /// </summary>
    /// <param name="request">Сутність джерела разом із налаштуванням мапінгу.</param>
    /// <param name="ct">Скасування.</param>
    [HttpPost]
    [ProducesResponseType<EntityFieldMapDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreateEntityFieldMapRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var command = new CreateEntityFieldMapCommand(
            request.SourceField,
            request.TargetKind,
            request.TargetColumnDefId,
            request.TargetRegistryFieldDefId,
            request.SourceUnitId,
            request.TargetUnitId,
            request.TargetRowKey,
            request.Aggregation);

        return Ok(await create.HandleAsync(request.SourceEntityId, command, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Призупиняє мапінг. Право <c>Integration.Manage</c> (<c>BE-27</c>).
    /// </summary>
    /// <param name="id">Мапінг.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ Пауза — не видалення: мапінг лишається на місці разом з одиницями й
    /// адресою рядка, а збір за ним перестає писати значення. Це і є штатний
    /// вихід для мапінгу, який уже зібрав дані й тому не видаляється.
    /// </remarks>
    [HttpPost("{id:int}/pause")]
    [ProducesResponseType<EntityFieldMapDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Pause(int id, CancellationToken ct)
        => Ok(await pause.HandleAsync(id, paused: true, ct).ConfigureAwait(false));

    /// <summary>
    /// Повертає призупинений мапінг у збір. Право <c>Integration.Manage</c>.
    /// </summary>
    /// <param name="id">Мапінг.</param>
    /// <param name="ct">Скасування.</param>
    [HttpPost("{id:int}/resume")]
    [ProducesResponseType<EntityFieldMapDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Resume(int id, CancellationToken ct)
        => Ok(await pause.HandleAsync(id, paused: false, ct).ConfigureAwait(false));

    /// <summary>
    /// Приймає нову одиницю джерела (<c>ФВ-16.9</c>). Право <c>Integration.Manage</c>.
    /// </summary>
    /// <param name="id">Мапінг.</param>
    /// <param name="request">Одиниця, яку джерело віддає тепер.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ Зміна одиниці в джерелі ЗУПИНЯЄ збір (<c>ECR-INT-0422</c>) і не
    /// приймається кодом: мовчазна конверсія «як здається» дає правдоподібні
    /// числа, помилку в яких знаходять на звірці через місяць. Ця дія — єдиний
    /// вихід із того стану, і тому вона лишає слід у журналі безпеки: хто,
    /// коли, з якої одиниці на яку.
    /// </remarks>
    [HttpPost("{id:int}/accept-unit-change")]
    [ProducesResponseType<EntityFieldMapDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AcceptUnitChange(
        int id, [FromBody] AcceptSourceUnitChangeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await acceptUnit
            .HandleAsync(id, request.SourceUnitId, ct)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Видаляє мапінг. Право <c>Integration.Manage</c> (<c>BE-27</c>).
    /// </summary>
    /// <param name="id">Мапінг.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ Мапінг, за яким уже зібрано дані, НЕ видаляється —
    /// <c>409 ECR-INT-0409</c> з лічильником точок і межами вікна в
    /// <c>details</c>. Мовчазне зникнення лишило б точки
    /// <c>ext.RawDataPoint</c> без пояснення того, у якій вони одиниці й куди
    /// лягали. Клієнт у відповідь пропонує паузу, а не повтор запиту — той
    /// самий шаблон, що й «на запис довідника посилаються N комірок».
    /// </remarks>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await delete.HandleAsync(id, ct).ConfigureAwait(false);

        return NoContent();
    }
}

/// <summary>Запит на приймання зміни одиниці джерела (<c>ФВ-16.9</c>).</summary>
/// <param name="SourceUnitId">Одиниця довідника, яку джерело віддає тепер.</param>
public sealed record AcceptSourceUnitChangeRequest(int SourceUnitId);

/// <summary>Запит на створення мапінгу.</summary>
/// <param name="SourceEntityId">Сутність джерела.</param>
/// <param name="SourceField">Поле або тег у джерелі.</param>
/// <param name="TargetKind">Куди лягає значення: колонка чи поле реєстру.</param>
/// <param name="TargetColumnDefId">Колонка-ціль; обов'язкове для <see cref="FieldTargetKind.Column"/>.</param>
/// <param name="TargetRegistryFieldDefId">
/// Поле реєстру-ціль; обов'язкове для <see cref="FieldTargetKind.RegistryField"/>.
/// </param>
/// <param name="SourceUnitId">Одиниця ДЖЕРЕЛА (ФВ-16.9); <c>null</c> — безрозмірне.</param>
/// <param name="TargetUnitId">Одиниця, у якій значення лягає в ECR; <c>null</c> — безрозмірне.</param>
/// <param name="TargetRowKey">Рядок-адресат (<c>D-118</c>); <c>null</c> — не матеріалізується.</param>
/// <param name="Aggregation">Спосіб згортання точок періоду; обов'язковий разом із <paramref name="TargetRowKey"/>.</param>
public sealed record CreateEntityFieldMapRequest(
    int SourceEntityId,
    string SourceField,
    FieldTargetKind TargetKind,
    int? TargetColumnDefId,
    int? TargetRegistryFieldDefId,
    int? SourceUnitId,
    int? TargetUnitId,
    string? TargetRowKey,
    AggregationKind? Aggregation);
