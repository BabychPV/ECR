using System.Globalization;
using System.Text.RegularExpressions;
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
public sealed partial class ConditionalFormatRule : Entity<int>
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
        Validate(ordinal, columnCode, @operator, value, valueTo, backgroundHex, foregroundHex);

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

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    /// <summary>Скільки операндів потрібно оператору: 0 (<c>empty</c>/<c>notEmpty</c>), 1, 2 (<c>between</c>).</summary>
    public static int OperandCount(string @operator)
        => @operator is "empty" or "notEmpty" ? 0 : @operator == "between" ? 2 : 1;

    private static void Validate(
        int ordinal, string columnCode, string @operator, string? value, string? valueTo,
        string? backgroundHex, string? foregroundHex)
    {
        if (!Operators.Contains(@operator))
        {
            Fail("err.ECR-CFG-0422.condFormatOperator", ordinal, columnCode, @operator);
        }

        var needed = OperandCount(@operator);
        if (needed >= 1 && !IsNumber(value))
        {
            Fail("err.ECR-CFG-0422.condFormatOperand", ordinal, columnCode, @operator);
        }

        if (needed == 2 && !IsNumber(valueTo))
        {
            Fail("err.ECR-CFG-0422.condFormatOperand", ordinal, columnCode, @operator);
        }

        foreach (var hex in new[] { backgroundHex, foregroundHex })
        {
            if (hex is not null && !HexColor().IsMatch(hex))
            {
                Fail("err.ECR-CFG-0422.condFormatColor", ordinal, columnCode, @operator);
            }
        }
    }

    // Порівняння на клієнті — через Number після normalizeDecimal (кома → крапка).
    private static bool IsNumber(string? text)
        => !string.IsNullOrWhiteSpace(text)
           && text.Length <= MaxOperandLength
           && decimal.TryParse(
               text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out _);

    private static void Fail(string key, int ordinal, string columnCode, string @operator)
        => throw new DomainException(
            "ECR-CFG-0422",
            $"Правило умовного форматування {ordinal} колонки {columnCode} невалідне ({key}).",
            new Dictionary<string, object?>
            {
                ["messageKey"] = key,
                ["index"] = ordinal,
                ["columnCode"] = columnCode,
                ["operator"] = @operator,
            });}
