using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.ValueObjects;
using Ecr.Application.Documents.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Робота з комірками документа.</summary>
[ApiController]
[Route("api/v1/documents/{documentId:long}")]
[Authorize]
public sealed class CellsController(
    PatchCellsHandler patchHandler,
    GetTableSliceHandler sliceHandler,
    CreateRowHandler rowHandler,
    IAccessDecisionService access,
    IRowStore rows,
    Ecr.Api.Auth.CurrentUser currentUser) : ControllerBase
{
    /// <summary>Зріз таблиці для grid.</summary>
    /// <remarks>Бюджет: p95 1.5 с на 500×60 (tz/08 §8.2).</remarks>
    [HttpGet("tables/{tableInstanceId:long}")]
    [ProducesResponseType<TableSliceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TableSliceDto>> GetSlice(
        long documentId, long tableInstanceId, CancellationToken ct)
    {
        // Профіль береться з кешу сесії: він уже побудований, і другий похід
        // за правами з'їв би бюджет відкриття таблиці (ФВ-6.10).
        var profile = await ProfileAsync(ct).ConfigureAwait(false);

        return await sliceHandler
            .HandleAsync(documentId, tableInstanceId, profile, currentUser.Language, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Пакетна зміна комірок.</summary>
    /// <remarks>
    /// Часткове застосування заборонене: конфлікт у будь-якому рядку відхиляє
    /// весь батч із переліком розбіжностей (409 <c>ECR-CELL-0409</c>).
    /// </remarks>
    [HttpPatch("cells")]
    [ProducesResponseType<PatchCellsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PatchCellsResponse>> Patch(
        long documentId, [FromBody] PatchCellsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ⚠ Належність екземпляра таблиці документові перевіряється ТУТ і до
        // будь-якої роботи. Без цієї перевірки шлях у URL стає декоративним:
        // клієнт указав би чужий TableInstanceId і писав би в чужий документ,
        // маючи право лише на свій.
        var owner = await rows.ResolveTableInstanceAsync(request.TableInstanceId, ct)
            .ConfigureAwait(false);

        if (owner.DocumentId != documentId)
        {
            return NotFound(new { errorCode = "ECR-DOC-0404" });
        }

        // Винятки перетворює ExceptionHandlingMiddleware — ловити їх тут не
        // треба: конфлікт baseVersion має піти клієнту як 409 із переліком.
        return await patchHandler.HandleAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>Додає рядок у динамічну таблицю.</summary>
    [HttpPost("rows")]
    [ProducesResponseType<Contracts.RowKeyResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateRow(
        long documentId, [FromBody] CreateRowRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var profile = await ProfileAsync(ct).ConfigureAwait(false);
        var requested = string.IsNullOrWhiteSpace(request.RowKey)
            ? (RowKey?)null
            : RowKey.Create(request.RowKey);

        var rowKey = await rowHandler
            .HandleAsync(documentId, request.TableInstanceId, requested, profile, ct)
            .ConfigureAwait(false);

        return Created(
            $"/api/v1/documents/{documentId}/tables/{request.TableInstanceId}",
            new Contracts.RowKeyResponse(rowKey.Value));
    }

    /// <summary>Профіль доступу поточного користувача.</summary>
    private Task<AccessProfile> ProfileAsync(CancellationToken ct)
        => access.BuildProfileAsync(
            currentUser.UserId ?? throw new Application.Errors.AccessDeniedException(
                Errors.ErrorCodes.Unauthorized, "Сесія не містить користувача."),
            ct);
}

/// <summary>Запит на створення рядка.</summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="RowKey">Бажаний ключ; <c>null</c> — згенерувати GUID.</param>
public sealed record CreateRowRequest(long TableInstanceId, string? RowKey);
