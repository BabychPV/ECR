using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Lexing;

namespace Ecr.Expressions.Parsing;

/// <summary>
/// Парсер рекурсивного спуску. Один парсер на обидва діалекти: різниця між
/// <c>Template</c> і <c>Methodology</c> — лише в наборі дозволених посилань і
/// функцій, а не в синтаксисі (D-19).
/// </summary>
public sealed class Parser
{
    /// <summary>Розбирає вираз.</summary>
    /// <param name="expression">Текст.</param>
    /// <param name="dialect">Діалект — визначає, які посилання дозволені.</param>
    /// <returns>
    /// Результат із AST або з діагностиками. Помилка синтаксису — **результат**,
    /// а не виняток: конфігуратор має показати проблему, а не впасти.
    /// </returns>
    public ParseResult Parse(string expression, ExpressionDialect dialect)
        => throw new NotImplementedException(
            "TODO: рекурсивний спуск за EBNF (02b §1) із пріоритетами (02b §2):\n" +
            "ternary → or → and → not → comparison → concat → additive → multiplicative →\n" +
            "power (ПРАВОАСОЦІАТИВНИЙ) → unary → primary.\n" +
            "Посилання (02b §3): cell_ref у трьох скорочених формах, arg_ref '@', const_ref 'CST.',\n" +
            "formula_ref '!', header_ref 'HDR.', period_ref '[Period:±N]'.\n" +
            "Для dialect = Template заборонити @Arg/CST./!Formula → діагностика;\n" +
            "для Methodology заборонити cell_ref → діагностика (методологія працює з\n" +
            "підготовленими аргументами, а не лізе в документ сама).\n" +
            "Збирати ВСІ діагностики, не зупинятися на першій.");
}
