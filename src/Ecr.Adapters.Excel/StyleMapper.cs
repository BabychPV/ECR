using ClosedXML.Excel;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Adapters.Excel;

// ⚠ CA1822 (методи можна зробити статичними) вимкнено свідомо. Обидва класи
// названі КОНСТРУКТОРОМ `ExcelExporter` у контракті пакета (`05g` §1) і
// живуть у контейнері як співавтори. Статичні методи зробили б параметри
// конструктора зайвими — і контракт, і можливість підмінити поведінку в
// тестах зникли б заради економії, якої не існує: обидва класи без стану і
// створюються один раз як Singleton.
#pragma warning disable CA1822

/// <summary>
/// Переносить стилі шаблону (<c>cfg.StyleDef</c>) у формати ClosedXML і назад.
/// </summary>
/// <remarks>
/// ⚠ Клас навмисно **без залежностей і без стану**: стиль — це чиста функція
/// від опису. Тримати тут книгу або кеш означало б, що два експорти, які
/// йдуть паралельно, впливають один на одного.
/// </remarks>
public sealed class StyleMapper
{
    /// <summary>Колір заливки обчислених комірок.</summary>
    /// <remarks>
    /// ⚠ Обчислена комірка позначається **видимо**, а не лише замком. Книгу
    /// правлять поза системою, і людина має бачити, що це поле рахується, ще
    /// до того, як спробує його змінити: при зворотному імпорті таку правку
    /// буде відхилено (<c>ECR-CELL-4221</c>), і без позначки це виглядало б
    /// як втрата роботи.
    /// </remarks>
    public static XLColor CalculatedFill => XLColor.FromArgb(0xEF, 0xEF, 0xEF);

    /// <summary>Колір заливки заголовків.</summary>
    public static XLColor HeaderFill => XLColor.FromArgb(0xDD, 0xE5, 0xF0);

    /// <summary>Кладе опис стилю на комірку або діапазон.</summary>
    /// <param name="target">Стиль ClosedXML, який змінюємо.</param>
    /// <param name="style">Опис із <c>cfg.StyleDef</c>; <c>null</c> — не чіпати.</param>
    /// <remarks>
    /// Кожне поле застосовується лише тоді, коли воно задане. Підставляти
    /// «типове» значення замість порожнього означало б затерти стиль, який
    /// комірка вже має від колонки чи таблиці.
    /// </remarks>
    public void Apply(IXLStyle? target, StyleDef? style)
    {
        if (target is null || style is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(style.FontName))
        {
            target.Font.FontName = style.FontName;
        }

        if (style.FontSize is { } size and > 0)
        {
            target.Font.FontSize = (double)size;
        }

        target.Font.Bold = style.IsBold;
        target.Font.Italic = style.IsItalic;

        if (style.ForegroundArgb is { } foreground)
        {
            target.Font.FontColor = XLColor.FromArgb(foreground);
        }

        if (style.BackgroundArgb is { } background)
        {
            target.Fill.BackgroundColor = XLColor.FromArgb(background);
        }

        if (style.HorizontalAlign is { } horizontal)
        {
            target.Alignment.Horizontal = Horizontal(horizontal);
        }

        if (style.VerticalAlign is { } vertical)
        {
            target.Alignment.Vertical = Vertical(vertical);
        }

        target.Alignment.WrapText = style.WrapText;

        if (!string.IsNullOrWhiteSpace(style.NumberFormat))
        {
            target.NumberFormat.Format = style.NumberFormat;
        }

        ApplyBorders(target, style.BorderJson);
    }

    /// <summary>Ставить формат відображення колонки.</summary>
    /// <param name="target">Стиль ClosedXML.</param>
    /// <param name="displayFormat">Формат із <c>ColumnDef.DisplayFormat</c>.</param>
    /// <param name="scale">Кількість знаків після коми; використовується, якщо формату немає.</param>
    /// <remarks>
    /// ⚠ Точність відображення береться з <c>Scale</c>, а не «як вийде».
    /// Число, показане з іншою кількістю знаків, ніж у системі, під час
    /// звірки читається як розбіжність — і його починають шукати.
    /// </remarks>
    public void ApplyNumberFormat(IXLStyle? target, string? displayFormat, byte? scale)
    {
        if (target is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(displayFormat))
        {
            target.NumberFormat.Format = displayFormat;
            return;
        }

        if (scale is { } digits)
        {
            target.NumberFormat.Format = digits == 0 ? "0" : "0." + new string('0', digits);
        }
    }

    /// <summary>Позначає комірку як таку, що рахується системою.</summary>
    public void MarkCalculated(IXLStyle? target)
    {
        if (target is null)
        {
            return;
        }

        target.Fill.BackgroundColor = CalculatedFill;
        target.Protection.Locked = true;
    }

    /// <summary>Оформлює заголовок.</summary>
    public void MarkHeader(IXLStyle? target)
    {
        if (target is null)
        {
            return;
        }

        target.Font.Bold = true;
        target.Fill.BackgroundColor = HeaderFill;
        target.Alignment.WrapText = true;
        target.Border.BottomBorder = XLBorderStyleValues.Thin;
    }

    /// <summary>Межі з <c>BorderJson</c>.</summary>
    /// <remarks>
    /// Формат — <c>{"top":1,"right":1,"bottom":2,"left":1}</c>, де число це
    /// товщина: 0 немає, 1 тонка, 2 середня, 3 товста. Нерозпізнане значення
    /// ігнорується: вигадана межа гірша за відсутню, бо виглядає як рішення
    /// того, хто налаштовував шаблон.
    /// </remarks>
    private static void ApplyBorders(IXLStyle target, string? borderJson)
    {
        if (string.IsNullOrWhiteSpace(borderJson))
        {
            return;
        }

        Dictionary<string, int>? borders;

        try
        {
            borders = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(borderJson);
        }
        catch (System.Text.Json.JsonException)
        {
            // ⚠ Зіпсований JSON стилю не валить експорт документа. Книга без
            // рамки лишається читабельною; виняток тут коштував би всієї
            // операції через оформлення.
            return;
        }

        if (borders is null)
        {
            return;
        }

        foreach (var (side, weight) in borders)
        {
            var border = Border(weight);

            switch (side.ToUpperInvariant())
            {
                case "TOP":
                    target.Border.TopBorder = border;
                    break;
                case "RIGHT":
                    target.Border.RightBorder = border;
                    break;
                case "BOTTOM":
                    target.Border.BottomBorder = border;
                    break;
                case "LEFT":
                    target.Border.LeftBorder = border;
                    break;
                default:
                    break;
            }
        }
    }

    private static XLBorderStyleValues Border(int weight) => weight switch
    {
        1 => XLBorderStyleValues.Thin,
        2 => XLBorderStyleValues.Medium,
        >= 3 => XLBorderStyleValues.Thick,
        _ => XLBorderStyleValues.None,
    };

    private static XLAlignmentHorizontalValues Horizontal(byte value) => value switch
    {
        1 => XLAlignmentHorizontalValues.Center,
        2 => XLAlignmentHorizontalValues.Right,
        3 => XLAlignmentHorizontalValues.Justify,
        _ => XLAlignmentHorizontalValues.Left,
    };

    private static XLAlignmentVerticalValues Vertical(byte value) => value switch
    {
        1 => XLAlignmentVerticalValues.Center,
        2 => XLAlignmentVerticalValues.Bottom,
        _ => XLAlignmentVerticalValues.Top,
    };
}
#pragma warning restore CA1822
