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
/// <param name="Message">
/// Повідомлення англійською — запасний варіант, якщо <paramref
/// name="MessageKey"/> відсутній або клієнт не може його розв'язати
/// (`Q-303`: раніше тут завжди лежало готове УКРАЇНСЬКЕ речення, що
/// суперечило власній обіцянці цього поля — «локалізоване» — і доїжджало до
/// екрана незалежно від мови інтерфейсу користувача, en/ru/kz).
/// </param>
/// <param name="Position">Позиція в тексті виразу.</param>
/// <param name="Length">Довжина проблемного фрагмента.</param>
/// <param name="MessageKey">
/// Ключ каталогу рядків (<c>err.*</c>-подібний, префікс <c>expr.</c>) для
/// СПРАВЖНЬОЇ локалізації клієнтом через <c>t()</c>; <c>null</c> — діагностика
/// цього рівня (звірка типів/одиниць/посилань — `TypeChecker`,
/// `UnitChecker`, `ReferenceResolver` і сусіди) ключа поки не несе, і клієнт
/// показує <see cref="Message"/> як є (`Q-303` навмисно обмежена синтаксисом
/// парсера/лексера — семантична перевірка залишається поза межею картки).
/// </param>
/// <param name="MessageParams">
/// Підстановки для <paramref name="MessageKey"/> — той самий формат
/// <c>{name}</c>, який уже читає клієнтський <c>t()</c>
/// (`shared/i18n/index.ts`); <c>null</c>, коли повідомлення не має змінних
/// частин.
/// </param>
public sealed record ExpressionDiagnostic(
    string Code,
    string Message,
    int Position,
    int Length,
    string? MessageKey = null,
    IReadOnlyDictionary<string, string>? MessageParams = null);
