using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;

namespace Ecr.Expressions.Functions;

/// <summary>
/// Каталог функцій діалекту <c>Template</c> — рівно дванадцять (<c>02b</c> §7).
/// Розширення — зміна контракту, тобто <c>questions.md</c> і зупинка.
/// </summary>
/// <remarks>
/// ⛔ **Діалекту методологій тут немає, і це виправлення, а не спрощення.**
/// До кроку <c>I.14</c> цей клас оголошував ще й «дванадцять функцій діалекту
/// Methodology» — <c>POWER</c>, <c>TRUNC</c>, <c>MOD</c>, <c>SWITCH</c>,
/// <c>COALESCE</c> та інші, — і саме за ним розбиралися й обчислювалися
/// вирази методологій. Той набір був вигаданий цілком: чинний рушій
/// (NCalc 1.3.8) жодного з цих імен не знає, регістр у ньому значущий, а
/// арність строга. Отже формула з <c>POWER(2,3)</c> проходила публікацію і
/// рахувалася нами, хоча в чинній системі не працювала ніколи, — і звірка на
/// ній нічого не доводила (<c>Q-082</c>).
///
/// ⚠ Склад діалекту методологій живе тепер там, звідки він виміряний, —
/// у <see cref="DialectCatalog"/> (<c>tests/Ecr.Legacy.Probe</c>). Двох
/// каталогів на одну мову більше немає.
/// </remarks>
public sealed class FunctionRegistry
{

    /// <summary>
    /// Дванадцять функцій діалекту <c>Template</c> (02b §7).
    /// </summary>
    /// <remarks>
    /// ⚠ <c>CONVERT</c> тут не за симетрією з методологіями, а за потребою
    /// (<c>Q-066</c>). У чинному шаблоні **216 формул ділять на 1000** —
    /// це конверсія одиниць магічним числом (м³ → тис. м³, кг → т). Саме її
    /// забороняє <c>D-74</c> («неявних конверсій не буває») і саме її замінює
    /// <c>CONVERT</c>. Без неї в діалекті шаблонів цим 216 формулам просто
    /// нема куди мігрувати.
    ///
    /// Те саме каже <c>ФВ-16.8</c>: «агрегація колонки з одиницею на рядок без
    /// <c>CONVERT</c> — <c>ECR-TMPL-4223</c>». Код помилки — <c>TMPL</c>, тобто
    /// перевірка публікації ШАБЛОНУ; вимога описує засіб для випадку, який
    /// існує в таблиці документа.
    /// </remarks>
    private static readonly FunctionSignature[] TemplateSet =
    [
        new("CONVERT", 3, 3, false, ExpressionValueType.Number),
        new("SUM", 1, null, true, ExpressionValueType.Number),
        new("AVERAGE", 1, null, true, ExpressionValueType.Number),
        new("MIN", 1, null, true, ExpressionValueType.Number),
        new("MAX", 1, null, true, ExpressionValueType.Number),
        new("COUNT", 1, null, true, ExpressionValueType.Number),
        new("ROUND", 2, 2, false, ExpressionValueType.Number),
        new("ABS", 1, 1, false, ExpressionValueType.Number),
        new("PRODUCT", 1, null, true, ExpressionValueType.Number),
        new("IF", 3, 3, false, ExpressionValueType.Null),
        new("IFERROR", 2, 2, false, ExpressionValueType.Null),
        new("SUMIF", 2, 3, true, ExpressionValueType.Number),
    ];

    private static readonly Dictionary<string, FunctionSignature> Template =
        TemplateSet.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Імена функцій діалекту шаблонів — для перевірок повноти набору.
    /// </summary>
    /// <remarks>
    /// ⚠ Параметра «діалект» тут НЕМАЄ навмисно. Він був, і жоден викликач не
    /// передавав у нього <c>Methodology</c> — питання «які імена є в діалекті
    /// B» має свого відповідача (<see cref="DialectCatalog"/>). Гілка, у яку
    /// ніхто не заходить, — це наступний механізм без викликача, а їх у цьому
    /// проєкті вже виловлювали двічі.
    /// </remarks>
    public static IReadOnlyCollection<string> Names => Template.Keys;

    /// <summary>Чи є така функція в діалекті шаблонів.</summary>
    /// <param name="name">Ім'я функції; регістр не важить.</param>
    public bool IsAllowed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Template.ContainsKey(name);
    }

    /// <summary>
    /// Сигнатура функції ДІАЛЕКТУ ШАБЛОНІВ; <c>null</c> — такої там немає.
    /// </summary>
    /// <param name="name">Ім'я функції.</param>
    /// <remarks>
    /// ⛔ Саме шаблонів. Для методологій сигнатуру дає
    /// <c>DialectCatalog.Find</c>, і питати її тут не можна: регістр там
    /// значущий, а цей словник — <c>OrdinalIgnoreCase</c>, тобто відповів би
    /// «є» на <c>ROUND</c>, якого чинний рушій не знає.
    /// </remarks>
    public FunctionSignature? GetSignature(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Template.GetValueOrDefault(name);
    }

    /// <summary>Викликає функцію.</summary>
    public ExpressionValue Invoke(string name, IReadOnlyList<ExpressionValue> args, IEvaluationContext context)
        => Invoke(name, [args], context);

    /// <summary>Викликає функцію, зберігаючи межі аргументів.</summary>
    /// <remarks>
    /// ⚠ Аргументи передаються ГРУПАМИ, а не пласким списком, бо один аргумент
    /// може бути діапазоном, тобто багатьма значеннями. Для агрегатів межі не
    /// важать — вони все одно зливаються; для <c>SUMIF</c> вони і є сенсом:
    /// перша група — значення, друга — паралельні їй умови.
    /// </remarks>
    /// <param name="name">Ім'я функції.</param>
    /// <param name="groups">Аргументи; кожна група — один аргумент виразу.</param>
    /// <param name="context">Контекст обчислення — потрібен лише <c>CONVERT</c>.</param>
    public ExpressionValue Invoke(
        string name, IReadOnlyList<IReadOnlyList<ExpressionValue>> groups, IEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(groups);

        if (name.Equals("SUMIF", StringComparison.OrdinalIgnoreCase))
        {
            return TemplateFunctions.SumIf(
                groups[0], groups.Count > 1 ? groups[1] : [], groups.Count > 2 ? groups[2] : null);
        }

        var args = groups.Count == 1 ? groups[0] : groups.SelectMany(g => g).ToList();

        return name.ToUpperInvariant() switch
        {
            "SUM" => TemplateFunctions.Sum(args),
            "AVERAGE" => TemplateFunctions.Average(args),
            "MIN" => TemplateFunctions.Min(args),
            "MAX" => TemplateFunctions.Max(args),
            "COUNT" => TemplateFunctions.Count(args),
            "ROUND" => TemplateFunctions.Round(args[0], args[1]),
            "ABS" => TemplateFunctions.Abs(args[0]),
            "PRODUCT" => TemplateFunctions.Product(args),
            "IF" => TemplateFunctions.If(args[0], args[1], args[2]),
            "IFERROR" => TemplateFunctions.IfError(args[0], args[1]),
            "CONVERT" => ConvertFunction.Invoke(args, context),

            // Сюди не потрапити з розібраного виразу: парсер відхиляє невідомі
            // імена ще при публікації. Лишається як явна межа набору.
            _ => throw new InvalidOperationException($"Функція '{name}' не входить у набір діалекту шаблонів."),
        };
    }
}

/// <summary>Сигнатура функції.</summary>
/// <param name="Name">Ім'я.</param>
/// <param name="MinArgs">Мінімум аргументів.</param>
/// <param name="MaxArgs">Максимум; <c>null</c> — необмежено (агрегати).</param>
/// <param name="AcceptsRange">Чи приймає діапазон замість скалярів.</param>
/// <param name="ResultType">Тип результату.</param>
public sealed record FunctionSignature(
    string Name, int MinArgs, int? MaxArgs, bool AcceptsRange, ExpressionValueType ResultType);
