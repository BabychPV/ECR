using Ecr.Application.Documents;
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
    Ecr.Api.Auth.CurrentUser currentUser) : ControllerBase
{
    /// <summary>Зріз таблиці для grid.</summary>
    /// <remarks>Бюджет: p95 1.5 с на 500×60 (tz/08 §8.2).</remarks>
    [HttpGet("tables/{tableInstanceId:long}")]
    [ProducesResponseType<TableSliceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<ActionResult<TableSliceDto>> GetSlice(long documentId, long tableInstanceId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: узяти AccessProfile із контексту (він уже в кеші сесії), викликати sliceHandler. " +
            "Жодної логіки в контролері.");

    /// <summary>Пакетна зміна комірок.</summary>
    /// <remarks>
    /// Часткове застосування заборонене: конфлікт у будь-якому рядку відхиляє
    /// весь батч із переліком розбіжностей (409 <c>ECR-CELL-0409</c>).
    /// </remarks>
    [HttpPatch("cells")]
    [ProducesResponseType<PatchCellsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public Task<ActionResult<PatchCellsResponse>> Patch(
        long documentId, [FromBody] PatchCellsRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити, що request.TableInstanceId належить documentId; " +
            "викликати patchHandler. Винятки перетворює ExceptionHandlingMiddleware — " +
            "ловити їх тут не треба.");

    /// <summary>Додає рядок у динамічну таблицю.</summary>
    [HttpPost("rows")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public Task<IActionResult> CreateRow(long documentId, [FromBody] CreateRowRequest request, CancellationToken ct)
        => throw new NotImplementedException("TODO: делегувати rowHandler; повернути 201 із RowKey.");
}

/// <summary>Запит на створення рядка.</summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="RowKey">Бажаний ключ; <c>null</c> — згенерувати GUID.</param>
public sealed record CreateRowRequest(long TableInstanceId, string? RowKey);
