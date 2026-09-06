using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Functions;

/// <summary>
/// Ярус функції: чи вміє її обчислити ЧИННИЙ рушій.
/// </summary>
/// <remarks>
/// ⛔ Поділ не про зручність, а про сумісність. <c>NumericMode.Legacy</c>
/// існує, щоб відтворити числа чинного рушія; вираз, якого чинний рушій
/// обчислити не міг, за визначенням нічого не відтворює. Тому
/// <see cref="Extension"/> у <c>Legacy</c>-версії — помилка публікації
/// (<c>ECR-CALC-0433</c>), а не просто попередження.
/// </remarks>
public enum FunctionTier : byte
{
    /// <summary>Є в NCalc 1.3.8 — перевірено заміром (`docs/legacy-ncalc-1.3.8.md`).</summary>
    Core = 0,

    /// <summary>Чинний рушій цього не вміє: або немає в 1.3.8, або наше розширення.</summary>
    Extension = 1,
}

/// <summary>
/// Каталог функцій діалекту методологій (`B03` §2, `B13` §3).
/// </summary>
/// <remarks>
/// ⛔ **Це не той набір, що був у `02b` §8.** Той складався з `POWER`,
/// `TRUNC`, `MOD`, `SWITCH`, `COALESCE` і одинадцяти функцій діалекту
/// шаблонів — і був вигаданий цілком. Чинний рушій діалекту B — NCalc 1.3.8,
/// і його набір інший і за складом, і за регістром, і за арністю.
///
/// ⚠ Склад **виміряний**, а не переписаний із документа. Директива №05 §3
/// подає 24 функції як «точний набір редактора PI Vision»; замір
/// (`tests/Ecr.Legacy.Probe`) показав, що двох із них — <c>Ln</c> і
/// <c>ifs</c> — у самому рушії **немає**. Тому:
///
/// <list type="bullet">
/// <item><description><b>22 Core</b> — підтверджені заміром;</description></item>
/// <item><description><b>2 Extension</b> — <c>Ln</c>, <c>ifs</c>: є в каталозі
/// редактора, немає в рушії;</description></item>
/// <item><description><b>2 Extension</b> — <c>CONVERT</c>, <c>SUBSTANCE</c>:
/// наші, потрібні для одиниць і речовин.</description></item>
/// </list>
///
/// ⚠ Наслідок, вартий уваги методолога: **натуральний логарифм у чинній
/// системі недосяжний**. <c>Log</c> строго двоаргументний, <c>Ln</c> не
/// оголошений — отже формула на <c>Ln</c> ніколи не була чинною.
/// </remarks>
public static class DialectCatalog
{
    /// <summary>
    /// Двадцять дві функції, які вміє NCalc 1.3.8.
    /// </summary>
    /// <remarks>
    /// ⛔ Регістр значущий: <c>EvaluateOptions.IgnoreCase</c> у чинній збірці
    /// не виставлений (`Utilities.cs:42`), тому <c>POW</c> і <c>ROUND</c> —
    /// невідомі функції, а не синоніми. Арність — строга: <c>Ceiling</c>,
    /// <c>Floor</c> і <c>Truncate</c> приймають рівно один аргумент,
    /// <c>Max</c>, <c>Min</c>, <c>Log</c> і <c>Round</c> — рівно два.
    /// </remarks>
    private static readonly FunctionSignature[] CoreSet =
    [
        new("Abs", 1, 1, false, ExpressionValueType.Number),
        new("Acos", 1, 1, false, ExpressionValueType.Number),
        new("Asin", 1, 1, false, ExpressionValueType.Number),
        new("Atan", 1, 1, false, ExpressionValueType.Number),
        new("Ceiling", 1, 1, false, ExpressionValueType.Number),
        new("Cos", 1, 1, false, ExpressionValueType.Number),
        new("Exp", 1, 1, false, ExpressionValueType.Number),
        new("Floor", 1, 1, false, ExpressionValueType.Number),
        new("IEEERemainder", 2, 2, false, ExpressionValueType.Number),
        new("Log", 2, 2, false, ExpressionValueType.Number),
        new("Log10", 1, 1, false, ExpressionValueType.Number),
        new("Max", 2, 2, false, ExpressionValueType.Number),
        new("Min", 2, 2, false, ExpressionValueType.Number),
        new("Pow", 2, 2, false, ExpressionValueType.Number),
        new("Round", 2, 2, false, ExpressionValueType.Number),
        new("Sign", 1, 1, false, ExpressionValueType.Number),
        new("Sin", 1, 1, false, ExpressionValueType.Number),
        new("Sqrt", 1, 1, false, ExpressionValueType.Number),
        new("Tan", 1, 1, false, ExpressionValueType.Number),
        new("Truncate", 1, 1, false, ExpressionValueType.Number),

        // ⚠ `in` і `if` — єдині з набору, що повертають не число: `in` дає
        // булеве, `if` — тип обраної гілки, і в корпусі це буває ТЕКСТ
        // (`'В пределе норматива'`, `'Сверхнорматив'`).
        new("in", 2, null, false, ExpressionValueType.Boolean),
        new("if", 3, 3, false, ExpressionValueType.Null),
    ];

    /// <summary>
    /// Чотири функції, яких чинний рушій не обчислює.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>Ln</c> і <c>ifs</c> потрапили сюди **за заміром**, а не за
    /// задумом: директива №05 §3 називає їх серед 24, але в NCalc 1.3.8 обидві
    /// дають «Function not found». Отже формула з ними ніколи не рахувалася
    /// чинною системою — і в <c>Legacy</c>-версії їй нічого відтворювати.
    ///
    /// ⚠ <c>CONVERT</c> і <c>SUBSTANCE</c> — наші за походженням: зміна
    /// одиниці має бути єдиним явним механізмом, а не множенням на магічний
    /// коефіцієнт у тексті формули (`D-74`).
    /// </remarks>
    private static readonly FunctionSignature[] ExtensionSet =
    [
        new("Ln", 1, 1, false, ExpressionValueType.Number),
        new("ifs", 2, null, false, ExpressionValueType.Null),
        new("CONVERT", 3, 3, false, ExpressionValueType.Number),
        new("SUBSTANCE", 1, 1, false, ExpressionValueType.Number),
    ];

    /// <summary>
    /// Пошук у діалекті методологій — <b>з урахуванням регістру</b>.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>StringComparer.Ordinal</c>, а не <c>OrdinalIgnoreCase</c>. Це не
    /// суворість заради суворості: у чинній збірці <c>IgnoreCase</c> не
    /// виставлений, тож <c>POW(2,3)</c> там — помилка. Прийняти його в нас
    /// означало б, що формула, яку ми приймаємо, у чинній системі не
    /// рахувалася, — і звірка на ній нічого не доводить.
    /// </remarks>
    private static readonly Dictionary<string, FunctionSignature> Methodology =
        CoreSet.Concat(ExtensionSet).ToDictionary(f => f.Name, StringComparer.Ordinal);

    private static readonly HashSet<string> ExtensionNames =
        ExtensionSet.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>Імена функцій діалекту методологій.</summary>
    public static IReadOnlyCollection<string> Names => Methodology.Keys;

    /// <summary>Сигнатура функції; <c>null</c> — такої немає в діалекті.</summary>
    /// <param name="name">Ім'я з урахуванням регістру.</param>
    public static FunctionSignature? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Methodology.GetValueOrDefault(name);
    }

    /// <summary>Ярус функції.</summary>
    /// <param name="name">Ім'я з урахуванням регістру.</param>
    /// <returns>
    /// <see cref="FunctionTier.Extension"/> — чинний рушій цього не вміє.
    /// </returns>
    public static FunctionTier TierOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return ExtensionNames.Contains(name) ? FunctionTier.Extension : FunctionTier.Core;
    }

    /// <summary>
    /// Чи дозволена функція у версії з цим числовим режимом.
    /// </summary>
    /// <param name="name">Ім'я функції.</param>
    /// <param name="mode">Числовий режим версії методології.</param>
    /// <remarks>
    /// ⛔ Імпортовані з <c>AF_*</c> методології цього обмеження не зачеплять
    /// ніколи: вони складаються з <see cref="FunctionTier.Core"/> за
    /// побудовою — інакше чинна система їх не рахувала б.
    /// </remarks>
    public static bool IsAllowedIn(string name, NumericMode mode)
        => TierOf(name) == FunctionTier.Core || mode == NumericMode.Strict;
}
