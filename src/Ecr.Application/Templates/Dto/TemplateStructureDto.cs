// src/Ecr.Application/Templates/Dto/TemplateStructureDto.cs

using Ecr.Application.Documents.Dto;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Templates.Dto;

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
    IReadOnlyList<TemplateRowDto> Rows);

/// <summary>
/// Рядок у СТРУКТУРІ шаблону — опис, а не дані.
/// </summary>
/// <remarks>
/// ⚠ Окремий тип від <see cref="RowDto"/> (`Q-012`). Той описує рядок
/// ДОКУМЕНТА і несе <c>Cells</c>, <c>RowVersion</c> та <c>IsOrphaned</c> — усе
/// три належать <c>doc.TableRow</c> і в структурі шаблону не існують:
/// значень там немає, версії рядка немає, а осиротіти може лише посилання в
/// даних. Спільний тип означав би, що половина полів відповіді завжди
/// порожня, і клієнт не міг би відрізнити «немає значення» від «тут значень
/// не буває».
/// </remarks>
/// <param name="RowKey">Стабільна бізнес-ідентичність (`R-B6`).</param>
/// <param name="Ordinal">Порядок відображення; презентаційне поле.</param>
/// <param name="RowKind">Вид рядка: <c>Item</c>, <c>Group</c>, <c>Balance</c>, <c>Note</c>.</param>
/// <param name="Label">Локалізований підпис.</param>
/// <param name="ParentRowKey">Батьківський рядок в ієрархії; <c>null</c> — корінь.</param>
/// <param name="IsReadOnly">Рядок недоступний для введення.</param>
public sealed record TemplateRowDto(
    string RowKey,
    int Ordinal,
    string RowKind,
    string? Label,
    string? ParentRowKey,
    bool IsReadOnly);
