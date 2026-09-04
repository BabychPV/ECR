using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Іменований стиль. Стиль — окрема сутність, а не набір атрибутів комірки:
/// інакше зміна оформлення означала б зміну даних.
/// </summary>
public sealed class StyleDef : Entity<int>
{
    private StyleDef() { }

    public StyleDef(int templateVersionId, EcrCode code)
    {
        TemplateVersionId = templateVersionId;
        Code = code.Value;
    }

    public int TemplateVersionId { get; private set; }
    public string Code { get; private set; } = null!;
    public string? FontName { get; private set; }
    public decimal? FontSize { get; private set; }
    public bool IsBold { get; private set; }
    public bool IsItalic { get; private set; }
    public int? ForegroundArgb { get; private set; }
    public int? BackgroundArgb { get; private set; }
    public string? BorderJson { get; private set; }
    public byte? HorizontalAlign { get; private set; }
    public byte? VerticalAlign { get; private set; }
    public bool WrapText { get; private set; }
    public string? NumberFormat { get; private set; }
}
