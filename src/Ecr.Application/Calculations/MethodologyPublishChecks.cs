// src/Ecr.Application/Calculations/MethodologyPublishChecks.cs
using System.Globalization;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;

namespace Ecr.Application.Calculations;

/// <summary>
/// Перевірки типів версії методології **при публікації** (директива ПК-1 №05,
/// поправка 2-біс; 02b §12).
/// </summary>
/// <remarks>
/// ⛔ Чому саме публікація, а не рантайм: константа-мітка, підставлена у вираз
/// під час нічного перерахунку, дає не аварію, а число — і його побачать через
/// місяць на звірці. Виявлена при публікації, вона коштує рядка на екрані
/// конфігуратора.
/// <para>
/// ⚠ Перевірки віддають **перелік**, а не кидають на першій. Публікація
/// проходить цілком або відхиляється з усіма проблемами одразу: методолог, який
/// виправляє їх по одній за прогін, робить це стільки разів, скільки їх є.
/// </para>
/// </remarks>
public static class MethodologyPublishChecks
{
    /// <summary>
    /// Арифметичні оператори: у їхніх операндах текстова константа — помилка.
    /// </summary>
    private static readonly HashSet<BinaryOperator> Arithmetic =
    [
        BinaryOperator.Add, BinaryOperator.Subtract, BinaryOperator.Multiply,
        BinaryOperator.Divide, BinaryOperator.Modulo, BinaryOperator.Power,
    ];

    /// <summary>
    /// Функції діалекту B, що повертають НЕ число: решта 20 із 22 числові
    /// (02b §8).
    /// </summary>
    /// <remarks>
    /// ⚠ Перелік від протилежного — двох імен, а не двадцяти: нова числова
    /// функція інакше мовчки випала б із перевірки, а нова текстова помітна
    /// одразу, бо її треба сюди дописати.
    /// </remarks>
    private static readonly HashSet<string> NonNumericFunctions =
        new(StringComparer.OrdinalIgnoreCase) { "if", "in" };

    /// <summary>Перевіряє типи констант і результатів формул версії.</summary>
    /// <param name="formulas">Формули версії з розібраними деревами.</param>
    /// <param name="constants">Усі константи версії, включно з мітками.</param>
    /// <param name="outputs">Оголошені виходи версії.</param>
    /// <returns>Перелік проблем; порожній — публікація за типами проходить.</returns>
    /// <exception cref="ArgumentNullException">Будь-який аргумент — <c>null</c>.</exception>
    public static IReadOnlyList<string> Check(
        IReadOnlyList<ParsedFormula> formulas,
        IReadOnlyList<MethodologyConstant> constants,
        IReadOnlyList<MethodologyOutput> outputs)
    {
        ArgumentNullException.ThrowIfNull(formulas);
        ArgumentNullException.ThrowIfNull(constants);
        ArgumentNullException.ThrowIfNull(outputs);

        var problems = new List<string>();
        var byCode = new Dictionary<string, MethodologyConstant>(StringComparer.OrdinalIgnoreCase);

        foreach (var constant in constants)
        {
            byCode[constant.Code] = constant;
        }

        problems.AddRange(UnresolvedConstants(constants));

        var outputCodes = new HashSet<string>(outputs.Select(o => o.Code), StringComparer.OrdinalIgnoreCase);

        foreach (var formula in formulas)
        {
            if (formula.Root is null)
            {
                continue;
            }

            problems.AddRange(ConstantsInExpression(formula, byCode));
            problems.AddRange(ResultTypeProblems(formula, byCode, outputCodes));
        }

        return problems;
    }

    /// <summary>
    /// Константи, які не мають придатного значення (три відомі дефекти корпусу).
    /// </summary>
    /// <remarks>
    /// ⛔ Це і є місце, де рядок «ані число, ані мітка» перетворюється на
    /// рішення людини. Три такі рядки відомі поіменно:
    /// <c>n_ECW_C11_13_ = '-'</c> (16 формул), <c>Kp_ECW_C11_13_ = '-'</c> (4),
    /// <c>k22_HSE30X_Int_FG_ = ''</c> (5). Мовчазний нуль дав би 25 формул із
    /// правдоподібними і неправильними числами.
    /// </remarks>
    private static IEnumerable<string> UnresolvedConstants(IReadOnlyList<MethodologyConstant> constants)
    {
        foreach (var constant in constants.Where(c => !c.IsResolved))
        {
            // ⚠ TODO: потрібен окремий код `ECR-CALC-0434` («значення константи
            // не є ані числом, ані текстом»). Поки — найближчий наявний.
            yield return constant.Kind == ConstantKind.Numeric
                ? $"Константа «{constant.Code}» оголошена числовою, але значення "
                  + $"«{constant.TextValue ?? "—"}» не є числом: потрібне рішення методолога, "
                  + "а не нуль за замовчуванням."
                : $"Константа «{constant.Code}» виду {constant.Kind} не має тексту.";
        }
    }

    /// <summary>Мітки категорій і текст у арифметиці — по одному виразу.</summary>
    private static IEnumerable<string> ConstantsInExpression(
        ParsedFormula formula, Dictionary<string, MethodologyConstant> byCode)
    {
        var referenced = new List<string>();
        var inArithmetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Walk(formula.Root!, isArithmetic: false, referenced, inArithmetic);

        foreach (var code in referenced.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!byCode.TryGetValue(code, out var constant))
            {
                continue;
            }

            // ⛔ Мітка категорії у виразі — сплутаний ключ звуження зі значенням.
            // ⚠ TODO: потрібен окремий код `ECR-CALC-0434`.
            if (!constant.IsAllowedInExpression)
            {
                yield return $"Формула «{formula.Code}» посилається на «CST.{code}» — "
                    + "це мітка категорії, а не значення: у виразах вона не бере участі.";
                continue;
            }

            // Текстова константа в множенні або діленні — те саме `'-'`, лише
            // класифіковане як текст: результат буде помилкою обчислення, а не
            // числом, і виявиться під час нічного прогону.
            if (constant.Kind == ConstantKind.Text && inArithmetic.Contains(code))
            {
                yield return $"Формула «{formula.Code}»: текстова константа «CST.{code}» "
                    + $"(«{constant.TextValue}») стоїть в арифметичній позиції.";
            }
        }
    }

    /// <summary>Оголошений тип результату проти того, що вираз може повернути.</summary>
    /// <remarks>
    /// ⛔ Обидва напрями, бо мовчазні вони по-різному: текст у числовій колонці
    /// падає конверсією або обнуляється, а число, оголошене текстом, тихо
    /// проходить у звіт рядком і ламає сортування й підсумки.
    /// </remarks>
    private static IEnumerable<string> ResultTypeProblems(
        ParsedFormula formula,
        Dictionary<string, MethodologyConstant> byCode,
        HashSet<string> outputCodes)
    {
        var textOnly = ProducesTextOnly(formula.Root!, byCode);
        var surelyNumber = ProducesNumber(formula.Root!, byCode);

        // ⚠ TODO: потрібен окремий код `ECR-CALC-0436` («тип результату формули
        // не відповідає колонці-приймачу»).
        if (formula.ResultType == FormulaResultType.Number && textOnly)
        {
            yield return $"Формула «{formula.Code}» оголошена числовою, але повертає лише текст.";
        }

        if (formula.ResultType == FormulaResultType.Text && surelyNumber)
        {
            yield return $"Формула «{formula.Code}» оголошена текстовою, але повертає число.";
        }

        // ⛔ Оголошений вихід лягає в `calc.CalculationResult.Value
        // decimal(28,10)` — колонка числова, іншої там немає. Текстовий
        // результат методології сьогодні нікуди подіти (див. `H-24b`, крок I.15),
        // і мовчазна спроба записати його дала б нуль у звіті.
        if (formula.ResultType == FormulaResultType.Text && outputCodes.Contains(formula.Code))
        {
            yield return $"Формула «{formula.Code}» повертає текст і оголошена виходом методології: "
                + "результат зберігається в числовій колонці calc.CalculationResult.Value.";
        }
    }

    /// <summary>Збирає імена констант і позначає ті, що стоять в арифметиці.</summary>
    private static void Walk(
        AstNode node, bool isArithmetic, List<string> referenced, HashSet<string> inArithmetic)
    {
        switch (node)
        {
            case SymbolReferenceNode { Kind: SymbolKind.Constant } symbol:
                referenced.Add(symbol.Name);
                if (isArithmetic)
                {
                    inArithmetic.Add(symbol.Name);
                }

                break;

            case BinaryNode binary:
                var operands = Arithmetic.Contains(binary.Operator);
                Walk(binary.Left, operands, referenced, inArithmetic);
                Walk(binary.Right, operands, referenced, inArithmetic);
                break;

            case UnaryNode unary:
                Walk(
                    unary.Operand,
                    unary.Operator != UnaryOperator.Not,
                    referenced,
                    inArithmetic);
                break;

            // ⚠ Гілки успадковують контекст, а умова — ні: `if` усередині
            // множення зобов'язує обидві гілки бути числом, а сама умова
            // порівнює тексти і це нормально.
            case ConditionalNode conditional:
                Walk(conditional.Condition, false, referenced, inArithmetic);
                Walk(conditional.WhenTrue, isArithmetic, referenced, inArithmetic);
                Walk(conditional.WhenFalse, isArithmetic, referenced, inArithmetic);
                break;

            case FunctionNode function:
                WalkFunction(function, isArithmetic, referenced, inArithmetic);
                break;

            default:
                break;
        }
    }

    /// <summary>Аргументи функції: у <c>if</c> і <c>in</c> вони не арифметичні.</summary>
    private static void WalkFunction(
        FunctionNode function, bool isArithmetic, List<string> referenced, HashSet<string> inArithmetic)
    {
        if (string.Equals(function.Name, "if", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count == 3)
        {
            Walk(function.Arguments[0], false, referenced, inArithmetic);
            Walk(function.Arguments[1], isArithmetic, referenced, inArithmetic);
            Walk(function.Arguments[2], isArithmetic, referenced, inArithmetic);
            return;
        }

        var argumentsAreNumbers = !NonNumericFunctions.Contains(function.Name);

        foreach (var argument in function.Arguments)
        {
            Walk(argument, argumentsAreNumbers, referenced, inArithmetic);
        }
    }

    /// <summary>Чи вираз здатний повернути **лише** текст.</summary>
    /// <remarks>
    /// ⚠ Саме «лише»: форма корпусу — вкладені <c>if</c> із текстовими
    /// літералами в усіх гілках (<c>'В пределе норматива'</c>,
    /// <c>'Сверхнорматив'</c>, <c>'Превышение!!!'</c>). Одна числова гілка
    /// робить відповідь «не знаємо», і перевірка мовчить — здогадуватися тут
    /// дорожче, ніж пропустити.
    /// </remarks>
    private static bool ProducesTextOnly(
        AstNode node, Dictionary<string, MethodologyConstant> byCode)
        => node switch
        {
            LiteralNode { Type: ExpressionValueType.Text } => true,
            BinaryNode { Operator: BinaryOperator.Concat } => true,
            SymbolReferenceNode { Kind: SymbolKind.Constant } symbol
                => byCode.TryGetValue(symbol.Name, out var constant)
                   && constant.Kind == ConstantKind.Text,
            ConditionalNode conditional
                => ProducesTextOnly(conditional.WhenTrue, byCode)
                   && ProducesTextOnly(conditional.WhenFalse, byCode),
            FunctionNode { Arguments.Count: 3 } function
                when string.Equals(function.Name, "if", StringComparison.OrdinalIgnoreCase)
                => ProducesTextOnly(function.Arguments[1], byCode)
                   && ProducesTextOnly(function.Arguments[2], byCode),
            _ => false,
        };

    /// <summary>Чи вираз напевно повертає число.</summary>
    private static bool ProducesNumber(
        AstNode node, Dictionary<string, MethodologyConstant> byCode)
        => node switch
        {
            LiteralNode { Type: ExpressionValueType.Number } => true,
            BinaryNode binary => Arithmetic.Contains(binary.Operator),
            UnaryNode unary => unary.Operator != UnaryOperator.Not,
            SymbolReferenceNode { Kind: SymbolKind.Constant } symbol
                => byCode.TryGetValue(symbol.Name, out var constant)
                   && constant.Kind == ConstantKind.Numeric,
            ConditionalNode conditional
                => ProducesNumber(conditional.WhenTrue, byCode)
                   && ProducesNumber(conditional.WhenFalse, byCode),
            FunctionNode { Arguments.Count: 3 } function
                when string.Equals(function.Name, "if", StringComparison.OrdinalIgnoreCase)
                => ProducesNumber(function.Arguments[1], byCode)
                   && ProducesNumber(function.Arguments[2], byCode),
            FunctionNode function => !NonNumericFunctions.Contains(function.Name),
            _ => false,
        };

    /// <summary>Текст переліку проблем для повідомлення про відмову.</summary>
    /// <param name="problems">Перелік проблем.</param>
    /// <returns>Рядок, придатний для повідомлення користувачеві.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="problems"/> — <c>null</c>.</exception>
    public static string Describe(IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);

        return string.Format(
            CultureInfo.InvariantCulture,
            "Перевірка типів версії не пройдена ({0}): {1}",
            problems.Count,
            string.Join(" | ", problems));
    }
}

/// <summary>Формула разом із розібраним деревом — вхід перевірок публікації.</summary>
/// <remarks>
/// ⚠ Дерево приходить ПАРАМЕТРОМ, а не розбирається тут: розбір уже зроблено
/// при побудові графа залежностей, і другий прохід над тим самим текстом дав би
/// другу відповідь на питання «що написано у виразі».
/// </remarks>
/// <param name="Code">Код формули.</param>
/// <param name="ResultType">Оголошений тип результату.</param>
/// <param name="Root">
/// Корінь дерева; <c>null</c> — вираз не розібрався, і типи тут не предмет
/// (синтаксис ловить окрема перевірка).
/// </param>
public sealed record ParsedFormula(string Code, FormulaResultType ResultType, AstNode? Root);
