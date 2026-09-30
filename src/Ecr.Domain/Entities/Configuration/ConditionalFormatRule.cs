using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Правило умовного форматування комірок колонки (ФВ-2.6/2.7): «якщо значення
/// відповідає умові — фарбувати комірку». Належить структурі
/// <see cref="TemplateVersion"/> (не документу): після заморозки версії правила
/// незмінні (тригер <c>cfg.TR_ConditionalFormatRule_Immutable</c>, 50001..50005).
/// </summary>
/// <remarks>
/// Форма — дзеркало клієнтської моделі <c>features/templates/conditionalFormat.ts</c>:
/// колонка за кодом (<see cref="ColumnCode"/> — як у клієнті; унікальність коду
/// колонки в межах версії не вимагається моделлю, правило діє на колонки з цим кодом),
/// оператор, операнд(и) текстом (порівняння числове на клієнті), кольори
/// <c>#rrggbb</c> і жирність. Порядок застосування — <see cref="Ordinal"/>,
/// перше спрацьоване правило виграє.
/// </remarks>
public sealed class ConditionalFormatRule : Entity<int>
{
    /// <summary>Допустимі оператори (ті самі рядки, що в клієнтському <c>ConditionOperator</c>).</summary>
    public static readonly IReadOnlyList<string> Operators =
        ["gt", "ge", "lt", "le", "eq", "ne", "between", "empty", "notEmpty"];

    /// <summary>Максимальна довжина операнда.</summary>
    public const int MaxOperandLength = 64;

    private ConditionalFormatRule() { }

    public ConditionalFormatRule(
        int templateVersionId,
        string columnCode,
        int ordinal,
        string @operator,
        string? value,
        string? valueTo,
        string? backgroundHex,
        string? foregroundHex,
        bool isBold)
    {
        TemplateVersionId = templateVersionId;
        ColumnCode = columnCode;
        Ordinal = ordinal;
        Operator = @operator;
        Value = value;
        ValueTo = valueTo;
        BackgroundHex = backgroundHex;
        ForegroundHex = foregroundHex;
        IsBold = isBold;
    }

    public int TemplateVersionId { get; private set; }
    public string ColumnCode { get; private set; } = null!;
    public int Ordinal { get; private set; }
    public string Operator { get; private set; } = null!;
    public string? Value { get; private set; }

    /// <summary>Верхня межа — лише для <c>between</c>.</summary>
    public string? ValueTo { get; private set; }

    /// <summary><c>#rrggbb</c> або <c>null</c> (колір теми).</summary>
    public string? BackgroundHex { get; private set; }
    public string? ForegroundHex { get; private set; }
    public bool IsBold { get; private set; }
}