using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Errors;

namespace Ecr.Application.Expressions;

/// <summary>
/// Межа довжини виразу ДО розбору — на кожній точці входу, що приймає вираз від користувача.
/// </summary>
/// <remarks>
/// ⛔ L7-01 (аудит 2026-10-03): <c>POST /expressions/validate</c> (право
/// <c>Calculation.View</c> — є у Viewer, DataEntry, Auditor) приймав вираз до
/// 64 КіБ, тобто ланцюг <c>1+1+…+1</c> на ~32 000 доданків, і рекурсивні
/// обходи дерева вичерпували стек — падав процес API. Збереження формули
/// (<c>FormulaDefHandlers</c>) і правила валідації повну перевірку робили
/// ДО власної перевірки довжини.
///
/// ⚠ Межа — <see cref="MethodologyFormula.MaxExpressionLength"/> (4000): найдовший
/// вираз, що взагалі зберігається. Довший однаково не збережеться, тож його
/// перевірка нічого не дає автору, а розбір коштує серверу.
/// </remarks>
public static class ExpressionLengthGuard
{
    /// <summary>Найбільша довжина виразу в символах.</summary>
    public const int MaxLength = MethodologyFormula.MaxExpressionLength;

    /// <summary>Відмовляє, якщо вираз довший за <see cref="MaxLength"/>.</summary>
    /// <param name="expression">Текст виразу; <c>null</c> не перевіряється.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-REQ-0422</c>, ключ <c>err.ECR-REQ-0422.expressionTooLong</c>.</exception>
    public static void Require(string? expression) => Require(expression, MaxLength);

    /// <summary>
    /// Відмовляє, якщо вираз довший за <paramref name="max"/> — межу КОЛОНКИ, у яку він
    /// зберігається (A4-03: колонки правил валідації і формул вужчі, 2000, а межа 4000
    /// пропускала 2001–4000 символів до SaveChanges, і замість 422 виходило 500).
    /// </summary>
    /// <param name="expression">Текст виразу; <c>null</c> не перевіряється.</param>
    /// <param name="max">Найбільша довжина в символах.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-REQ-0422</c>, ключ <c>err.ECR-REQ-0422.expressionTooLong</c>.</exception>
    public static void Require(string? expression, int max)
    {
        if (expression is null || expression.Length <= max)
        {
            return;
        }

        throw new BusinessRuleException(
            ErrorCodes.RequestInvalid,
            $"Вираз довший за {max} символів ({expression.Length}).",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-REQ-0422.expressionTooLong",
                ["max"] = max.ToString(CultureInfo.InvariantCulture),
                ["length"] = expression.Length.ToString(CultureInfo.InvariantCulture),
            });
    }
}
