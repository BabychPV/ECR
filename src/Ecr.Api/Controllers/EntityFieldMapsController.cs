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
public sealed class EntityFieldMapsController(CreateEntityFieldMapHandler create) : ControllerBase
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
}

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
