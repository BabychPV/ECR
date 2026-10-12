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
///
/// CELL-STYLE L6 (12.10): правило може бути ОБМЕЖЕНЕ таблицею (<see cref="TableCode"/>), рядками
/// (<see cref="Scope"/> = <c>row</c>, <see cref="RowKeys"/>), читати значення ІНШОЇ колонки того ж рядка
/// (<see cref="RefColumnCode"/> — «статус рядка», як <c>$B5="Removed"</c> в Excel), порівнювати ТЕКСТ
/// (<c>textEq</c>/<c>textNe</c>; для довідника — код запису) і малювати штрих (<see cref="Pattern"/>).
/// <see cref="ColumnCode"/> = <see cref="AnyColumn"/> — усі видимі колонки таблиці (вимагає <see cref="TableCode"/>).
/// Без цих полів правило — колишнє колонкове: поведінка не змінилась.
/// </remarks>
public sealed partial class ConditionalFormatRule : Entity<int>
{
    /// <summary>Допустимі оператори (ті самі рядки, що в клієнтському <c>ConditionOperator</c>).</summary>
    public static readonly IReadOnlyList<string> Operators =
        ["gt", "ge", "lt", "le", "eq", "ne", "between", "empty", "notEmpty", "textEq", "textNe"];

    /// <summary>Максимальна довжина операнда.</summary>
    public const int MaxOperandLength = 64;

    /// <summary>Область за замовчуванням: усі рядки (колишня поведінка).</summary>
    public const string ScopeColumn = "column";

    /// <summary>Область «лише перелічені рядки» (<see cref="RowKeys"/>).</summary>
    public const string ScopeRow = "row";

    /// <summary>Допустимі області.</summary>
    public static readonly IReadOnlyList<string> Scopes = [ScopeColumn, ScopeRow];

    /// <summary>Код колонки «усі колонки таблиці».</summary>
    public const string AnyColumn = "*";

    /// <summary>Штрих (діагональна сітка — «неактуальна комірка» оригіналу).</summary>
    public const string PatternHatch = "hatch";

    /// <summary>Допустимі штрихи.</summary>
    public static readonly IReadOnlyList<string> Patterns = [PatternHatch];

    /// <summary>Стеля ключів рядків в одному правилі.</summary>
    public const int MaxRowKeys = 500;

    /// <summary>Довжина ключа рядка / коду таблиці / коду колонки.</summary>
    public const int MaxCodeLength = 64;

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
        bool isBold,
        string scope = ScopeColumn,
        string? tableCode = null,
        IReadOnlyList<string>? rowKeys = null,
        string? refColumnCode = null,
        string? pattern = null)
    {
        Validate(
            ordinal, columnCode, @operator, value, valueTo, backgroundHex, foregroundHex,
            scope, tableCode, rowKeys, refColumnCode, pattern);

        TemplateVersionId = templateVersionId;
        ColumnCode = columnCode;
        Ordinal = ordinal;
        Operator = @operator;
        Value = value;
        ValueTo = valueTo;
        BackgroundHex = backgroundHex;
        ForegroundHex = foregroundHex;
        IsBold = isBold;
        Scope = scope;
        TableCode = tableCode;
        RowKeys = rowKeys is null ? null : [.. rowKeys];
        RefColumnCode = refColumnCode;
        Pattern = pattern;
    }

    /// <summary><c>column</c> (усі рядки) або <c>row</c> (лише <see cref="RowKeys"/>).</summary>
    public string Scope { get; private set; } = ScopeColumn;

    /// <summary>Код таблиці, до якої прив'язане правило; <c>null</c> — усі таблиці версії (колишнє).</summary>
    public string? TableCode { get; private set; }

    /// <summary>Ключі рядків — лише для <see cref="ScopeRow"/>.</summary>
    public IReadOnlyList<string>? RowKeys { get; private set; }

    /// <summary>
    /// Код колонки, чиє значення в ТОМУ Ж рядку перевіряє умова; <c>null</c> — значення самої комірки.
    /// </summary>
    public string? RefColumnCode { get; private set; }

    /// <summary>Штрих (<see cref="PatternHatch"/>) або <c>null</c>.</summary>
    public string? Pattern { get; private set; }

    /// <summary>Чи правило несе що-небудь понад колишню колонкову форму.</summary>
    public bool IsScoped
        => Scope != ScopeColumn || TableCode is not null || RowKeys is not null || RefColumnCode is not null
           || ColumnCode == AnyColumn;

    /// <summary>Копія правила в іншу версію шаблону (клон версії): усі поля, без дрейфу списку полів.</summary>
    public ConditionalFormatRule CloneTo(int templateVersionId)
        => new(
            templateVersionId, ColumnCode, Ordinal, Operator, Value, ValueTo,
            BackgroundHex, ForegroundHex, IsBold, Scope, TableCode, RowKeys, RefColumnCode, Pattern);

    /// <summary>Чи оператор текстовий (операнд — довільний текст, не число).</summary>
    public static bool IsTextOperator(string @operator) => @operator is "textEq" or "textNe";

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

    [GeneratedRegex("^#[0-9a-fA-F]{6}\\z")]
    private static partial Regex HexColor();

    /// <summary>Скільки операндів потрібно оператору: 0 (<c>empty</c>/<c>notEmpty</c>), 1, 2 (<c>between</c>).</summary>
    public static int OperandCount(string @operator)
        => @operator is "empty" or "notEmpty" ? 0 : @operator == "between" ? 2 : 1;

    private static void Validate(
        int ordinal, string columnCode, string @operator, string? value, string? valueTo,
        string? backgroundHex, string? foregroundHex,
        string scope, string? tableCode, IReadOnlyList<string>? rowKeys, string? refColumnCode, string? pattern)
    {
        if (!Operators.Contains(@operator))
        {
            Fail("err.ECR-CFG-0422.condFormatOperator", ordinal, columnCode, @operator);
        }

        var needed = OperandCount(@operator);
        if (IsTextOperator(@operator))
        {
            // Текстовий операнд: непорожній, до 64 символів (регістр при порівнянні ігнорується).
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaxOperandLength)
            {
                Fail("err.ECR-CFG-0422.condFormatTextOperand", ordinal, columnCode, @operator);
            }
        }
        else if (needed >= 1 && !IsNumber(value))
        {
            Fail("err.ECR-CFG-0422.condFormatOperand", ordinal, columnCode, @operator);
        }

        if (needed == 2 && !IsNumber(valueTo))
        {
            Fail("err.ECR-CFG-0422.condFormatOperand", ordinal, columnCode, @operator);
        }

        if (!Scopes.Contains(scope))
        {
            Fail("err.ECR-CFG-0422.condFormatScope", ordinal, columnCode, @operator);
        }

        if (tableCode is not null && (string.IsNullOrWhiteSpace(tableCode) || tableCode.Length > MaxCodeLength))
        {
            Fail("err.ECR-CFG-0422.condFormatTable", ordinal, columnCode, @operator);
        }

        // «Усі колонки» без таблиці фарбувало б усе в усіх таблицях версії — лише з явною таблицею.
        if (columnCode == AnyColumn && tableCode is null)
        {
            Fail("err.ECR-CFG-0422.condFormatTable", ordinal, columnCode, @operator);
        }

        if (scope == ScopeRow)
        {
            // Ключ рядка осмислений лише в межах таблиці.
            if (tableCode is null)
            {
                Fail("err.ECR-CFG-0422.condFormatTable", ordinal, columnCode, @operator);
            }

            if (rowKeys is null || rowKeys.Count == 0 || rowKeys.Count > MaxRowKeys
                || rowKeys.Any(k => string.IsNullOrWhiteSpace(k) || k.Length > MaxCodeLength)
                || rowKeys.Distinct(StringComparer.Ordinal).Count() != rowKeys.Count)
            {
                Fail("err.ECR-CFG-0422.condFormatRowKeys", ordinal, columnCode, @operator);
            }
        }
        else if (rowKeys is not null)
        {
            Fail("err.ECR-CFG-0422.condFormatRowKeys", ordinal, columnCode, @operator);
        }

        if (refColumnCode is not null
            && (string.IsNullOrWhiteSpace(refColumnCode) || refColumnCode.Length > MaxCodeLength
                || refColumnCode == AnyColumn))
        {
            Fail("err.ECR-CFG-0422.condFormatRef", ordinal, columnCode, @operator);
        }

        if (pattern is not null && !Patterns.Contains(pattern))
        {
            Fail("err.ECR-CFG-0422.condFormatPattern", ordinal, columnCode, @operator);
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
