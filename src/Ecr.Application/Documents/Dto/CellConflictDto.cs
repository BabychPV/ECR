// src/Ecr.Application/Documents/Dto/CellConflictDto.cs
namespace Ecr.Application.Documents.Dto;

/// <summary>
/// Конфлікт паралельного редагування. Повертається в
/// <c>Extensions2.conflicts</c> при <c>ECR-CELL-0409</c>.
/// «Перезаписати мовчки» не є опцією: користувач має побачити розбіжність.
/// </summary>
public sealed record CellConflictDto(
    string RowKey,
    string ColumnCode,
    object? YourValue,
    object? TheirValue,
    string TheirUser,
    DateTime TheirChangedAt,
    string CurrentVersion);
