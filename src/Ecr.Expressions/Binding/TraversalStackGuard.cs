using System.Runtime.CompilerServices;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Parsing;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Сторож стека для рекурсивних обходів дерева виразу після розбору.
/// </summary>
/// <remarks>
/// ⛔ L7-01 (аудит 2026-10-03): ланцюг <c>1+1+…+1</c> на 32 000 доданків
/// давав лівий гребінь глибиною 32 000, і рекурсивні обходи
/// (<see cref="PredicateValidator"/>, <see cref="TypeChecker"/>,
/// <see cref="UnitChecker"/>, …) вичерпували стек. <c>StackOverflowException</c>
/// у .NET не перехоплюється — падав увесь процес API разом із сеансами всіх.
///
/// ⚠ Перша лінія оборони — межі на вході (довжина виразу,
/// <see cref="Parser.MaxChainLinks"/>). Цей сторож — друга: він не залежить
/// від того, звідки прийшло дерево (розбір, кеш, побудова в коді), і від
/// розміру стека потоку, на якому йде обхід. Замість падіння — звичайне
/// зауваження з ключем каталогу, один раз на перелік.
/// </remarks>
public static class TraversalStackGuard
{
    /// <summary>Ключ каталогу зауваження «вираз занадто складний для перевірки».</summary>
    public const string MessageKey = "expr.tooComplex";

    /// <summary>
    /// Чи вистачає стека на ще один рівень обходу; якщо ні — додає зауваження (один раз).
    /// </summary>
    /// <param name="node">Вузол, на якому обхід зупиняється.</param>
    /// <param name="diagnostics">
    /// Куди складати зауваження; <c>null</c> — нікуди, і тоді замість зауваження
    /// <see cref="InsufficientExecutionStackException"/>.
    /// </param>
    /// <returns><c>true</c> — можна спускатися далі.</returns>
    /// <remarks>
    /// ⛔ Без переліку зауважень — ВИНЯТОК, а не <c>false</c>: обхід, що мовчки
    /// обірвався, віддав би неповний результат (залежності, вживання
    /// аргументів) як повний. <see cref="InsufficientExecutionStackException"/>,
    /// на відміну від переповнення стека, перехоплюється: падає запит, а не процес.
    /// </remarks>
    public static bool TryEnter(AstNode node, List<ExpressionDiagnostic>? diagnostics)
    {
        if (RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            return true;
        }

        if (diagnostics is null)
        {
            throw new InsufficientExecutionStackException(
                "The expression is too complex to traverse: split it into several formulas.");
        }

        if (!diagnostics.Exists(d => d.MessageKey == MessageKey))
        {
            diagnostics.Add(new ExpressionDiagnostic(
                ExpressionErrors.Syntax,
                "The expression is too complex to check: split it into several formulas.",
                node?.Position ?? 0,
                1,
                MessageKey));
        }

        return false;
    }
}
