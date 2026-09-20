using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Functions;

/// <summary>
/// Функції діалекту <c>Report</c> (<c>02b</c> §8a) — закритий whitelist.
/// </summary>
/// <remarks>
/// ⛔ Тут немає нічого, що бачить далі власного рядка: агрегатів по діапазонах
/// (<c>SUM</c>, <c>AVERAGE</c>, <c>COUNT</c>, <c>PRODUCT</c>, <c>SUMIF</c>) і
/// <c>CONVERT</c>. Рядок потрапляє в зріз уже правильним (<c>ФВ-10.3</c>);
/// правило звіту його перетворює або ховає, а не рахує показник наново.
///
/// ⚠ Імена регістронезалежні, як у діалекті шаблонів: мова та сама, набір вужчий.
/// <c>MIN</c>/<c>MAX</c> тут — над скалярами, діапазонів у діалекті немає.
/// </remarks>
public static class ReportFunctions
{
    private static readonly Dictionary<string, FunctionSignature> Set =
        new FunctionSignature[]
        {
            new("IF", 3, 3, false, ExpressionValueType.Null),
            new("IFERROR", 2, 2, false, ExpressionValueType.Null),
            new("IN", 2, null, false, ExpressionValueType.Boolean),
            new("ROUND", 2, 2, false, ExpressionValueType.Number),
            new("ABS", 1, 1, false, ExpressionValueType.Number),
            new("MIN", 2, null, false, ExpressionValueType.Number),
            new("MAX", 2, null, false, ExpressionValueType.Number),
        }.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Імена функцій діалекту звітів.</summary>
    public static IReadOnlyCollection<string> Names => Set.Keys;

    /// <summary>Сигнатура; <c>null</c> — такої функції в діалекті немає.</summary>
    /// <param name="name">Ім'я; регістр не важить.</param>
    public static FunctionSignature? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Set.GetValueOrDefault(name);
    }
}
