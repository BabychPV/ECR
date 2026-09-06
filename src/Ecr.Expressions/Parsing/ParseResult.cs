// src/Ecr.Expressions/Parsing/ParseResult.cs

using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Parsing;

/// <summary>
/// Результат розбору. Помилка синтаксису — це <b>результат</b>, а не виняток:
/// конфігуратор має показати проблему користувачеві, а не впасти.
/// </summary>
public sealed record ParseResult(
    bool IsSuccess,
    ParsedExpression? Expression,
    IReadOnlyList<ExpressionDiagnostic> Diagnostics);

/// <summary>Розібраний вираз, готовий до обчислення.</summary>
public sealed record ParsedExpression(
    string SourceText,
    Ecr.Domain.Enums.ExpressionDialect Dialect,
    AstNode Root,
    ExpressionValueType ResultType);

/// <summary>Діагностика розбору або перевірки.</summary>
/// <param name="Code">
/// Код із каталогу помилок: <c>ECR-TMPL-*</c> для шаблону, <c>ECR-CALC-*</c>
/// для того, що ламається лише в діалекті методологій.
/// </param>
/// <param name="Message">Локалізоване повідомлення.</param>
/// <param name="Position">Позиція в тексті виразу.</param>
/// <param name="Length">Довжина проблемного фрагмента.</param>
public sealed record ExpressionDiagnostic(string Code, string Message, int Position, int Length);
