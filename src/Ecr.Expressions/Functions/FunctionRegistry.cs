using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;

namespace Ecr.Expressions.Functions;

/// <summary>
/// Каталог функцій. Набір закритий: 11 для <c>Template</c>, 24 для
/// <c>Methodology</c> (02b §7–8). Розширення — зміна контракту, тобто
/// <c>questions.md</c> і зупинка.
/// </summary>
public sealed class FunctionRegistry
{


    /// <summary>Одинадцять функцій діалекту <c>Template</c> (02b §7).</summary>
    private static readonly FunctionSignature[] TemplateSet =
    [
        new("SUM", 1, null, true, ExpressionValueType.Number),
        new("AVERAGE", 1, null, true, ExpressionValueType.Number),
        new("MIN", 1, null, true, ExpressionValueType.Number),
        new("MAX", 1, null, true, ExpressionValueType.Number),
        new("COUNT", 1, null, true, ExpressionValueType.Number),
        new("ROUND", 2, 2, false, ExpressionValueType.Number),
        new("ABS", 1, 1, false, ExpressionValueType.Number),
        new("PRODUCT", 1, null, true, ExpressionValueType.Number),
        new("IF", 3, 3, false, ExpressionValueType.Null),
        new("IFERROR", 2, 2, false, ExpressionValueType.Null),
        new("SUMIF", 2, 3, true, ExpressionValueType.Number),
    ];

    /// <summary>Тринадцять функцій, які додає діалект <c>Methodology</c> (02b §8).</summary>
    private static readonly FunctionSignature[] MethodologyOnlySet =
    [
        new("CONVERT", 3, 3, false, ExpressionValueType.Number),
        new("POWER", 2, 2, false, ExpressionValueType.Number),
        new("SQRT", 1, 1, false, ExpressionValueType.Number),
        new("EXP", 1, 1, false, ExpressionValueType.Number),
        new("LN", 1, 1, false, ExpressionValueType.Number),
        new("LOG10", 1, 1, false, ExpressionValueType.Number),
        new("CEILING", 1, 2, false, ExpressionValueType.Number),
        new("FLOOR", 1, 2, false, ExpressionValueType.Number),
        new("TRUNC", 1, 2, false, ExpressionValueType.Number),
        new("MOD", 2, 2, false, ExpressionValueType.Number),
        new("COALESCE", 1, null, false, ExpressionValueType.Null),
        new("SWITCH", 3, null, false, ExpressionValueType.Null),
        new("SUBSTANCE", 1, 1, false, ExpressionValueType.Number),
    ];

    private static readonly Dictionary<string, FunctionSignature> Template =
        TemplateSet.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, FunctionSignature> Methodology =
        TemplateSet.Concat(MethodologyOnlySet)
                   .ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Імена функцій діалекту — для перевірок повноти набору.</summary>
    public static IReadOnlyCollection<string> Names(ExpressionDialect dialect)
        => dialect == ExpressionDialect.Template ? Template.Keys : Methodology.Keys;

    /// <summary>Чи дозволена функція в діалекті.</summary>
    public bool IsAllowed(string name, ExpressionDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(name);
        return dialect == ExpressionDialect.Template
            ? Template.ContainsKey(name)
            : Methodology.ContainsKey(name);
    }

    /// <summary>Сигнатура функції для перевірки при публікації.</summary>
    public FunctionSignature? GetSignature(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Methodology.GetValueOrDefault(name);
    }

    /// <summary>Викликає функцію.</summary>
    public ExpressionValue Invoke(string name, IReadOnlyList<ExpressionValue> args, IEvaluationContext context)
        => Invoke(name, [args], context);

    /// <summary>Викликає функцію, зберігаючи межі аргументів.</summary>
    /// <remarks>
    /// ⚠ Аргументи передаються ГРУПАМИ, а не пласким списком, бо один аргумент
    /// може бути діапазоном, тобто багатьма значеннями. Для агрегатів межі не
    /// важать — вони все одно зливаються; для <c>SUMIF</c> вони і є сенсом:
    /// перша група — значення, друга — паралельні їй умови.
    /// </remarks>
    /// <param name="name">Ім'я функції.</param>
    /// <param name="groups">Аргументи; кожна група — один аргумент виразу.</param>
    /// <param name="context">Контекст обчислення — потрібен лише <c>CONVERT</c>.</param>
    public ExpressionValue Invoke(
        string name, IReadOnlyList<IReadOnlyList<ExpressionValue>> groups, IEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(groups);

        if (name.Equals("SUMIF", StringComparison.OrdinalIgnoreCase))
        {
            return TemplateFunctions.SumIf(
                groups[0], groups.Count > 1 ? groups[1] : [], groups.Count > 2 ? groups[2] : null);
        }

        var args = groups.Count == 1 ? groups[0] : groups.SelectMany(g => g).ToList();

        return name.ToUpperInvariant() switch
        {
            "SUM" => TemplateFunctions.Sum(args),
            "AVERAGE" => TemplateFunctions.Average(args),
            "MIN" => TemplateFunctions.Min(args),
            "MAX" => TemplateFunctions.Max(args),
            "COUNT" => TemplateFunctions.Count(args),
            "ROUND" => TemplateFunctions.Round(args[0], args[1]),
            "ABS" => TemplateFunctions.Abs(args[0]),
            "PRODUCT" => TemplateFunctions.Product(args),
            "IF" => TemplateFunctions.If(args[0], args[1], args[2]),
            "IFERROR" => TemplateFunctions.IfError(args[0], args[1]),
            "CONVERT" => ConvertFunction.Invoke(args, context),
            "POWER" => MethodologyFunctions.Power(args),
            "SQRT" => MethodologyFunctions.Sqrt(args),
            "EXP" => MethodologyFunctions.Exp(args),
            "LN" => MethodologyFunctions.Ln(args),
            "LOG10" => MethodologyFunctions.Log10(args),
            "CEILING" => MethodologyFunctions.Ceiling(args),
            "FLOOR" => MethodologyFunctions.Floor(args),
            "TRUNC" => MethodologyFunctions.Trunc(args),
            "MOD" => MethodologyFunctions.Mod(args),
            "COALESCE" => MethodologyFunctions.Coalesce(args),
            "SWITCH" => MethodologyFunctions.Switch(args),
            "SUBSTANCE" => MethodologyFunctions.Substance(args),

            // Сюди не потрапити з розібраного виразу: парсер відхиляє невідомі
            // імена ще при публікації. Лишається як явна межа набору.
            _ => throw new InvalidOperationException($"Функція '{name}' не входить у набір діалекту."),
        };
    }
}

/// <summary>Сигнатура функції.</summary>
/// <param name="Name">Ім'я.</param>
/// <param name="MinArgs">Мінімум аргументів.</param>
/// <param name="MaxArgs">Максимум; <c>null</c> — необмежено (агрегати).</param>
/// <param name="AcceptsRange">Чи приймає діапазон замість скалярів.</param>
/// <param name="ResultType">Тип результату.</param>
public sealed record FunctionSignature(
    string Name, int MinArgs, int? MaxArgs, bool AcceptsRange, ExpressionValueType ResultType);
