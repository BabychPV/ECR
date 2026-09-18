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

    /// <summary>
    /// Задає повний вигляд стилю (директива registry-lookup / cell-style, PR
    /// B1) — усі поля разом, а не поштучними сеттерами: `StyleMapper.cs`
    /// (Excel-експорт) читає їх як ОДНЕ узгоджене оформлення, і поштучний
    /// запис лишав би вікно, де стиль тимчасово суперечить сам собі (напр.
    /// `HorizontalAlign` поза діапазоном, який `StyleMapper.Horizontal`
    /// мовчки звів би до `Left`, — тут це відмова, а не тиха підміна).
    /// </summary>
    /// <param name="fontName"><c>null</c>/порожнє — шрифт теми Excel-книги за замовчуванням.</param>
    /// <param name="fontSize"><c>null</c> — розмір теми за замовчуванням.</param>
    /// <param name="isBold">Жирний.</param>
    /// <param name="isItalic">Курсив.</param>
    /// <param name="foregroundArgb">Колір тексту, ARGB; <c>null</c> — колір теми.</param>
    /// <param name="backgroundArgb">Колір заливки, ARGB; <c>null</c> — без заливки.</param>
    /// <param name="borderJson">
    /// Межі за стороною (`StyleMapper.ApplyBorders`): <c>{"top":1,"right":1,"bottom":2,"left":1}</c>,
    /// товщина 0..3; <c>null</c>/порожнє — без рамки.
    /// </param>
    /// <param name="horizontalAlign">
    /// `0` Left, `1` Center, `2` Right, `3` Justify (`StyleMapper.Horizontal`).
    /// </param>
    /// <param name="verticalAlign">`0` Top, `1` Center, `2` Bottom (`StyleMapper.Vertical`).</param>
    /// <param name="wrapText">Перенос тексту в комірці.</param>
    /// <param name="numberFormat">Формат числа Excel (напр. <c>0.00</c>); <c>null</c> — формат теми.</param>
    /// <exception cref="DomainException">
    /// Вирівнювання поза діапазоном, який знає <c>StyleMapper</c> — <c>ECR-CFG-0422</c>.
    /// </exception>
    public void SetAppearance(
        string? fontName,
        decimal? fontSize,
        bool isBold,
        bool isItalic,
        int? foregroundArgb,
        int? backgroundArgb,
        string? borderJson,
        byte? horizontalAlign,
        byte? verticalAlign,
        bool wrapText,
        string? numberFormat)
    {
        if (horizontalAlign is > 3)
        {
            throw new DomainException(
                "ECR-CFG-0422",
                $"Горизонтальне вирівнювання «{horizontalAlign}» невідоме: 0..3 (Left/Center/Right/Justify).",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CFG-0422.horizontalAlign",
                    ["value"] = horizontalAlign,
                });
        }

        if (verticalAlign is > 2)
        {
            throw new DomainException(
                "ECR-CFG-0422",
                $"Вертикальне вирівнювання «{verticalAlign}» невідоме: 0..2 (Top/Center/Bottom).",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CFG-0422.verticalAlign",
                    ["value"] = verticalAlign,
                });
        }

        FontName = string.IsNullOrWhiteSpace(fontName) ? null : fontName.Trim();
        FontSize = fontSize;
        IsBold = isBold;
        IsItalic = isItalic;
        ForegroundArgb = foregroundArgb;
        BackgroundArgb = backgroundArgb;
        BorderJson = string.IsNullOrWhiteSpace(borderJson) ? null : borderJson;
        HorizontalAlign = horizontalAlign;
        VerticalAlign = verticalAlign;
        WrapText = wrapText;
        NumberFormat = string.IsNullOrWhiteSpace(numberFormat) ? null : numberFormat.Trim();
    }
}
