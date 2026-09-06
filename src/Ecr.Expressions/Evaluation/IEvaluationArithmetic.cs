using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Правило округлення на середині.
/// </summary>
/// <remarks>
/// ⛔ Два діалекти округлюють ПО-РІЗНОМУ, і сплутати їх — це різні числа у
/// вже поданих формах:
///
/// <list type="bullet">
/// <item><description><c>Template</c> (A) — <see cref="AwayFromZero"/>, бо
/// шаблон є спадком Excel, а Excel округлює половину від нуля;</description></item>
/// <item><description><c>Methodology</c> (B) — <see cref="ToEven"/>, бо чинна
/// збірка створює <c>NCalc.Expression</c> **без** <c>EvaluateOptions</c>
/// (<c>Utilities.cs:42</c>), отже прапорець <c>RoundAwayFromZero</c> не
/// виставлений і діє банківське за замовчуванням.</description></item>
/// </list>
///
/// ⚠ Це не «поки не виміряно». Виміряно: <c>Round(2.5,0) = 2</c>,
/// <c>Round(0.5,0) = 0</c> (<c>docs/legacy-ncalc-1.3.8.md</c>).
/// </remarks>
public enum RoundingMode : byte
{
    /// <summary>Банківське — до парного. Діалект методологій.</summary>
    ToEven = 0,

    /// <summary>Від нуля. Діалект шаблонів (спадок Excel).</summary>
    AwayFromZero = 1,
}

/// <summary>
/// Арифметика режиму обчислення.
/// </summary>
/// <remarks>
/// ⛔ Порт існує заради одного твердження: <c>NumericMode.Legacy</c> має
/// рахувати **так само**, як чинна система, а не краще за неї. Уже подані
/// регулятору форми містять числа, пораховані в <c>double</c>; <c>ER-C-11</c>
/// («зміна останніх знаків у вже поданих формах неприпустима») виконується
/// не точністю, а **збігом**.
///
/// ⚠ Покращена арифметика вмикається окремим рішенням, з нової дати дії, як
/// <c>Breaking</c>-зміна з <c>ChangeReason</c> — і ніколи заднім числом.
///
/// ⛔ <c>D-30</c> не порушено: він про тип **колонки**
/// (<c>decimal(28,10)</c>), а не про арифметику. Порушенням було б
/// протилежне — рахувати <c>Legacy</c> у <c>decimal</c> і заявляти, що це
/// відтворює чинну систему.
/// </remarks>
public interface IEvaluationArithmetic
{
    /// <summary>Правило округлення цього режиму.</summary>
    public RoundingMode Rounding { get; }

    /// <summary>Бінарна операція над двома числами.</summary>
    /// <param name="left">Ліве значення.</param>
    /// <param name="right">Праве значення.</param>
    /// <param name="op">Операція.</param>
    /// <returns>Результат або значення-помилка.</returns>
    public ExpressionValue Binary(ExpressionValue left, ExpressionValue right, BinaryOperator op);

    /// <summary>Округлення до заданої кількості знаків.</summary>
    /// <param name="value">Значення.</param>
    /// <param name="digits">Скільки знаків лишити.</param>
    public ExpressionValue Round(ExpressionValue value, int digits);

    /// <summary>Одномісна числова функція каталогу.</summary>
    /// <param name="value">Аргумент.</param>
    /// <param name="function">Ім'я з <c>DialectCatalog</c>, з урахуванням регістру.</param>
    /// <returns>Результат; помилка, якщо функція невідома цьому режиму.</returns>
    public ExpressionValue Unary(ExpressionValue value, string function);

    /// <summary>Двомісна числова функція каталогу.</summary>
    /// <param name="left">Перший аргумент.</param>
    /// <param name="right">Другий аргумент.</param>
    /// <param name="function">Ім'я з <c>DialectCatalog</c>, з урахуванням регістру.</param>
    public ExpressionValue Binary(ExpressionValue left, ExpressionValue right, string function);
}
