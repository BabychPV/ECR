using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Functions;

/// <summary>
/// Каталог функцій. Набір закритий: 11 для <c>Template</c>, 24 для
/// <c>Methodology</c> (02b §7–8). Розширення — зміна контракту, тобто
/// <c>questions.md</c> і зупинка.
/// </summary>
public sealed class FunctionRegistry
{
    /// <summary>Чи дозволена функція в діалекті.</summary>
    public bool IsAllowed(string name, ExpressionDialect dialect)
        => throw new NotImplementedException(
            "TODO: Template → рівно 11 функцій зі списку 02b §7; " +
            "Methodology → ті самі 11 плюс 13 із §8. Порівняння імені — регістронезалежне.");

    /// <summary>Сигнатура функції для перевірки при публікації.</summary>
    public FunctionSignature? GetSignature(string name)
        => throw new NotImplementedException("TODO: повернути сигнатуру або null, якщо функції немає.");

    /// <summary>Викликає функцію.</summary>
    public ExpressionValue Invoke(string name, IReadOnlyList<ExpressionValue> args, IEvaluationContext context)
        => throw new NotImplementedException("TODO: диспетчеризація за іменем у TemplateFunctions/MethodologyFunctions.");
}

/// <summary>Сигнатура функції.</summary>
/// <param name="Name">Ім'я.</param>
/// <param name="MinArgs">Мінімум аргументів.</param>
/// <param name="MaxArgs">Максимум; <c>null</c> — необмежено (агрегати).</param>
/// <param name="AcceptsRange">Чи приймає діапазон замість скалярів.</param>
/// <param name="ResultType">Тип результату.</param>
public sealed record FunctionSignature(
    string Name, int MinArgs, int? MaxArgs, bool AcceptsRange, ExpressionValueType ResultType);
