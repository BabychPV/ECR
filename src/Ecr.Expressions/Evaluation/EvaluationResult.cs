using Ecr.Expressions.Parsing;

namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Результат обчислення виразу: значення плюс діагностики.
/// </summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — тверде.</b> Файл оголошений у дереві
/// `05-skeleton.md` §1, але секції з вмістом у `05d` немає (Q-015). Форма
/// виведена з <c>FormulaEngine.Evaluate</c>, чий <c>TODO</c> каже дослівно:
/// «делегувати evaluator; <b>загорнути результат і діагностики</b>».
/// <c>Evaluator.Evaluate</c> повертає <see cref="ExpressionValue"/>, а
/// діагностики в пакеті мають рівно один тип —
/// <see cref="ExpressionDiagnostic"/> (02b §11).
///
/// Помилка обчислення — це <b>значення</b> всередині
/// <see cref="ExpressionValue"/> (<c>#DIV/0</c>, <c>#REF</c>, <c>#VALUE</c>,
/// <c>#UNIT</c>, <c>#CYCLE</c>), а не запис у <see cref="Diagnostics"/>:
/// одна зіпсована комірка не валить перерахунок таблиці (02b §6.4).
/// У <see cref="Diagnostics"/> потрапляє те, що стосується <b>виразу</b>, а не
/// його значення — нерезолвлене посилання, невідома функція.
/// </remarks>
/// <param name="Value">Обчислене значення; може бути <c>null</c>-значенням або помилкою.</param>
/// <param name="Diagnostics">Діагностики виразу; порожній список — усе гаразд.</param>
public sealed record EvaluationResult(
    ExpressionValue Value,
    IReadOnlyList<ExpressionDiagnostic> Diagnostics);
