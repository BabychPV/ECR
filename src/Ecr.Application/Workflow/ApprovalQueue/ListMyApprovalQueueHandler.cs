// src/Ecr.Application/Workflow/ApprovalQueue/ListMyApprovalQueueHandler.cs
namespace Ecr.Application.Workflow.ApprovalQueue;

/// <summary>Черга «До погодження» поточного користувача (ФВ-12.5).</summary>
/// <remarks>
/// ⚠ КАРКАС контрактного коміту: віддає порожню чергу, щоб клієнт міг стартувати на фіксованому контракті.
/// Читання й правила — наступним комітом тієї ж lane.
/// </remarks>
public sealed class ListMyApprovalQueueHandler
{
    /// <summary>Сторінка черги.</summary>
    /// <param name="limit">Розмір сторінки.</param>
    /// <param name="cursor">Курсор.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<ApprovalQueueDto> HandleAsync(int? limit, string? cursor, CancellationToken ct)
        => Task.FromResult(new ApprovalQueueDto([], null));
}
