// src/Ecr.Application/Templates/Dto/TemplateStructureDto.cs
namespace Ecr.Application.Templates.Dto;

using Ecr.Application.Documents.Dto;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

/// <summary>
/// Структура опублікованої версії — те, що віддається клієнту й кешується за
/// ключем <c>v{id}:r{rev}</c> (`ФВ-2.5`).
/// </summary>
public sealed record TemplateStructureDto(
    int TemplateVersionId,
    int PresentationRevision,
    IReadOnlyList<SheetDto> Sheets);

public sealed record SheetDto(
    int Id, string Code, LocalizedText NameL10n, int Ordinal,
    IReadOnlyList<TableDto> Tables);

public sealed record TableDto(
    int Id, string Code, TableLayoutKind LayoutKind, TableRowMode RowMode,
    int? MaxDynamicRows,
    IReadOnlyList<ColumnDto> Columns,
    IReadOnlyList<RowDto> Rows);
