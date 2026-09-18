// tests/Ecr.Application.Tests/Recalculation/CountingFormulaEngine.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Graph;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// <see cref="RealFormulaEngine"/> з лічильниками викликів.
/// </summary>
/// <remarks>
/// ⛔ Обгортка, а НЕ заглушка: доказ <c>CAL-03</c> — це число викликів
/// <see cref="Evaluate"/>, і воно щось означає лише тоді, коли обчислення
/// справжнє. Підмінити <c>Evaluate</c> константою означало б рахувати виклики
/// того, що нічого не рахує, — і тест став би зеленим і на зламаному коді, бо
/// числа в комірках перестали б перевірятися взагалі.
///
/// ⚠ Лічильник <see cref="ParseCalls"/> стереже сусіднє твердження: розбір
/// винесено з <c>Evaluate</c> у передпрохід (класифікатор працює з AST), і
/// формула мусить розбиратися ОДИН раз на прогін, а не один раз на рядок.
/// </remarks>
public sealed class CountingFormulaEngine : IFormulaEngine
{
    private readonly RealFormulaEngine _inner = new();

    /// <summary>Скільки разів обчислено вираз.</summary>
    public int EvaluateCalls { get; private set; }

    /// <summary>Скільки разів розібрано вираз.</summary>
    public int ParseCalls { get; private set; }

    /// <inheritdoc />
    public ParseResult Parse(string expression, ExpressionDialect dialect)
    {
        ParseCalls++;
        return _inner.Parse(expression, dialect);
    }

    /// <inheritdoc />
    public EvaluationResult Evaluate(
        ParsedExpression expression,
        IEvaluationContext context,
        NumericMode mode = NumericMode.Strict)
    {
        EvaluateCalls++;
        return _inner.Evaluate(expression, context, mode);
    }

    /// <inheritdoc />
    public DependencyExtraction ExtractDependencies(
        ParsedExpression expression, TemplateVersionSnapshot? snapshot, DependencyContext context)
        => _inner.ExtractDependencies(expression, snapshot, context);

    /// <inheritdoc />
    public OrderingResult BuildEvaluationOrder(IReadOnlyList<FormulaNode> nodes)
        => _inner.BuildEvaluationOrder(nodes);
}
