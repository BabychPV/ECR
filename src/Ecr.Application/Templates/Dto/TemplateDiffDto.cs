// src/Ecr.Application/Templates/Dto/TemplateDiffDto.cs
namespace Ecr.Application.Templates.Dto;

/// <summary>
/// Diff двох версій шаблону. Зіставлення — **за ідентичністю** (`Code`,
/// `RowKey`), не за позицією: інакше будь-яке перевпорядкування дало б
/// «змінено все».
/// </summary>
/// <param name="Changes">Зміни з класифікацією за ризиком (`ФВ-7.3`).</param>
/// <param name="AffectedDocumentCount">Скільки документів прив'язано до вихідної версії.</param>
public sealed record TemplateDiffDto(
    IReadOnlyList<TemplateChangeDto> Changes,
    int AffectedDocumentCount);

/// <param name="ElementPath">Шлях: <c>Sheet.Table.Column</c> або <c>Sheet.Table.RowKey</c>.</param>
/// <param name="Kind">`Added` / `Removed` / `Modified` / `Presentation`.</param>
/// <param name="ChangeClass">Клас ризику; `Breaking` у версії з документами — відмова.</param>
public sealed record TemplateChangeDto(
    string ElementPath,
    string Kind,
    ChangeClass ChangeClass,
    string? OldValue,
    string? NewValue);
