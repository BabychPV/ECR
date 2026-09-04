using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Значення виразу. Помилки — **значення**, а не винятки: одна зіпсована
/// комірка не має валити перерахунок усієї таблиці (02b §6.4).
/// </summary>
public readonly record struct ExpressionValue
{
    private ExpressionValue(ExpressionValueType type, object? value, string? errorCode)
    {
        Type = type;
        Value = value;
        ErrorCode = errorCode;
    }

    public ExpressionValueType Type { get; }
    public object? Value { get; }

    /// <summary>Код помилки (<c>#DIV/0</c>, <c>#REF</c>, <c>#VALUE</c>, <c>#UNIT</c>, <c>#CYCLE</c>).</summary>
    public string? ErrorCode { get; }

    public bool IsNull => Type == ExpressionValueType.Null;
    public bool IsError => Type == ExpressionValueType.Error;

    /// <summary>Порожнє значення.</summary>
    public static ExpressionValue Null { get; } = new(ExpressionValueType.Null, null, null);

    public static ExpressionValue Number(decimal v) => new(ExpressionValueType.Number, v, null);
    public static ExpressionValue Text(string v) => new(ExpressionValueType.Text, v, null);
    public static ExpressionValue Boolean(bool v) => new(ExpressionValueType.Boolean, v, null);
    public static ExpressionValue Date(DateTime v) => new(ExpressionValueType.Date, v, null);

    /// <summary>Помилка обчислення.</summary>
    public static ExpressionValue Error(string code) => new(ExpressionValueType.Error, null, code);

    /// <summary>Значення як <see cref="decimal"/>; <c>null</c>, якщо це не число.</summary>
    public decimal? AsNumber() => Type == ExpressionValueType.Number ? (decimal)Value! : null;
}
