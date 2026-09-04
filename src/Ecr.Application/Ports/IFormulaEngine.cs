// src/Ecr.Application/Ports/IFormulaEngine.cs
namespace Ecr.Application.Ports;

using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Graph;
using Ecr.Expressions.Parsing;

/// <summary>
/// Рушій виразів. Один парсер на обидва діалекти; NCalc використовується як
/// обчислювач, а не як парсер нашої мови (D-19, D-20).
/// Граматика — <see href="02b-expressions.md">02b-expressions.md</see>.
/// </summary>
public interface IFormulaEngine
{
    /// <summary>Розбирає вираз. Помилка синтаксису — результат, а не виняток.</summary>
    ParseResult Parse(string expression, ExpressionDialect dialect);

    /// <summary>
    /// Витягує залежності виразу. Діапазони рядків розкриваються в явний список
    /// <c>RowKey</c> на момент <c>Publish</c> — у рантаймі діапазонів не існує (B03 §4).
    /// </summary>
    IReadOnlyList<FormulaDependencyRef> ExtractDependencies(ParsedExpression expression, DependencyContext context);

    /// <summary>Обчислює вираз.</summary>
    EvaluationResult Evaluate(ParsedExpression expression, IEvaluationContext context);

    /// <summary>
    /// Топологічний порядок обчислення. Цикл повертається як помилка публікації,
    /// а не як тихо неправильне число (ФВ-9.4).
    /// </summary>
    OrderingResult BuildEvaluationOrder(IReadOnlyList<FormulaNode> nodes);
}
