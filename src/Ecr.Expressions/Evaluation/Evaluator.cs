using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Обчислює AST. Уся арифметика — в <see cref="decimal"/>: порядок додавання
/// <c>float</c> змінює результат, і звірка з еталоном стає неможливою (D-30).
/// </summary>
public sealed class Evaluator(Functions.FunctionRegistry functions)
{
    /// <summary>Обчислює вираз у контексті.</summary>
    public ExpressionValue Evaluate(AstNode node, IEvaluationContext context)
        => throw new NotImplementedException(
            "TODO: рекурсивне обчислення з семантикою 02b §6 — це найтонше місце рушія:\n" +
            "— null в АГРЕГАТАХ ПОГЛИНАЄТЬСЯ: SUM ігнорує null, порожня множина → 0;\n" +
            "  AVERAGE не рахує null ані в сумі, ані в дільнику, порожня → null;\n" +
            "— null у БІНАРНИХ ОПЕРАТОРАХ ПОШИРЮЄТЬСЯ: null + 1 = null, null * 0 = null (НЕ 0!);\n" +
            "— виняток: конкатенація трактує null як порожній рядок;\n" +
            "— null = null → TRUE; null > 1 → null;\n" +
            "— ділення на нуль або на null → #DIV/0 як ЗНАЧЕННЯ, не виняток;\n" +
            "— помилка поширюється через операції; перехоплює лише IFERROR;\n" +
            "— IFERROR не перехоплює null (це не помилка).\n" +
            "Плутати два правила щодо null не можна: саме тут народжуються розбіжності " +
            "зі старою системою.");
}
