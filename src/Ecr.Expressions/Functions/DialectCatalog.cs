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

    /// <summary>
    /// Той самий каталог, але для пошуку БЕЗ урахування регістру.
    /// </summary>
    /// <remarks>
    /// ⚠ Служить рівно одному: назвати правильне написання в тексті помилки.
    /// Розбір ним не користується — інакше <c>POW(2,3)</c> був би прийнятий,
    /// а чинна система його не рахувала ніколи.
    /// </remarks>
    private static readonly Dictionary<string, string> ByLowerCase =
        CoreSet.Concat(ExtensionSet)
               .ToDictionary(f => f.Name, f => f.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Чим заміняти те, чого в діалекті немає (<c>02b</c> §8).
    /// </summary>
    /// <remarks>
    /// ⛔ Перелік не декоративний. Усі шість імен були в нашому ж
    /// <c>FunctionRegistry</c> і **розбиралися** до кроку <c>I.14</c>: формула
    /// з <c>POWER(2,3)</c> чи <c>SWITCH(…)</c> проходила публікацію, хоча
    /// чинний рушій обох не знає (<c>Q-082</c>). Тому методолог, який їх уже
    /// написав, має отримати не «невідома функція», а рядок, який каже, що
    /// саме поставити замість.
    ///
    /// ⚠ <c>COALESCE</c> заміни не має і не потребує: у діалекті B <c>null</c>
    /// під час прогону не буває — відсутній <c>@Arg</c> це
    /// <c>ARGUMENT_MISSING</c>, нечислова константа — <c>CONSTANT_NOT_NUMERIC</c>,
    /// і обидві виявляються при публікації.
    /// </remarks>
    private static readonly Dictionary<string, string> Replacements =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["POWER"] = "Pow(a, b)",
            ["TRUNC"] = "Truncate(a) — лише до цілого; до знаків: Truncate(a * 10^n) / 10^n",
            ["MOD"] = "оператор %",
            ["SWITCH"] = "вкладені if(умова, тоді, інакше)",
            ["COALESCE"] = "нічого: null під час прогону в діалекті методологій не буває",
            ["IFERROR"] = "нічого: помилка обчислення в діалекті методологій не перехоплюється",
        };

    /// <summary>
    /// Що написати замість імені, якого в діалекті немає; <c>null</c> — поради
    /// немає.
    /// </summary>
    /// <param name="name">Ім'я, яке не знайшлося в каталозі.</param>
    /// <remarks>
    /// ⚠ Спершу перевіряється РЕГІСТР: <c>POW</c>, <c>ROUND</c> і <c>abs</c> —
    /// не вигадані функції, а правильні з неправильним написанням, і порада
    /// «використайте Pow(a, b)» на <c>POW</c> звучала б як знущання.
    /// </remarks>
    public static string? Advice(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (ByLowerCase.TryGetValue(name, out var exact))
        {
            return $"регістр значущий, і пишеться воно '{exact}'";
        }

        return Replacements.TryGetValue(name, out var replacement)
            ? $"замість неї — {replacement}"
            : null;
    }

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
    /// <remarks>
    /// ⛔ Невідоме ім'я дає <see cref="FunctionTier.Core"/>, і це НЕ твердження
    /// «така функція є в NCalc 1.3.8»: метод відповідає лише на питання «чи
    /// наше це розширення». Питання «чи існує таке ім'я взагалі» ставиться
    /// <see cref="Find"/>, і ставити його треба ПЕРШИМ — саме на цьому
    /// спіткнувся <c>Q-082</c>: перевірка публікації, побудована на самому
    /// <c>TierOf</c>, пропустила б <c>SWITCH</c> як «ядро».
    /// Тому <see cref="IsAllowedIn"/> питає обидва.
    /// </remarks>
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
    ///
    /// ⛔ Ім'я, якого в діалекті немає, недозволене в ОБОХ режимах, а не лише
    /// в <c>Legacy</c>. Без цієї гілки метод був би зеленим з хибної причини
    /// (<c>Q-082</c>): <c>SWITCH</c> у <c>Legacy</c>-версії проходив би як
    /// ядро, бо <see cref="TierOf"/> віддає <see cref="FunctionTier.Core"/> на
    /// будь-яке невідоме слово. Первинний сторож — усе одно розбір
    /// (<c>Parser.ParseFunctionCall</c>), який такого імені не пропускає;
    /// тут — друга межа на випадок, якщо перевірку викличуть у обхід розбору.
    /// </remarks>
    public static bool IsAllowedIn(string name, NumericMode mode)
        => Find(name) is not null
           && (TierOf(name) == FunctionTier.Core || mode == NumericMode.Strict);
}
