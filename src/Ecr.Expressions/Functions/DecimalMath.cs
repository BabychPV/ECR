using System.Globalization;

namespace Ecr.Expressions.Functions;

/// <summary>
/// Трансцендентні функції в <see cref="decimal"/>.
/// </summary>
/// <remarks>
/// ⚠ Існує тому, що <see cref="Math"/> працює в <c>double</c>, а <c>double</c>
/// у цій системі заборонений (D-30). Різниця не теоретична: звітні числа
/// звіряються до шостого знака, і <c>Math.Pow(10, 2)</c>, що дає
/// <c>99.999999999999986</c>, ламає звірку з еталоном мовчки.
/// <para>
/// Точність рядів обмежена <see cref="Epsilon"/>: далі за нього <c>decimal</c>
/// однаково не розрізняє доданків, і додаткові ітерації лише крутять
/// процесор.
/// </para>
/// </remarks>
internal static class DecimalMath
{
    /// <summary>Поріг збіжності: за ним доданок уже не змінює суму.</summary>
    private const decimal Epsilon = 1e-27m;

    /// <summary>Стеля ітерацій ряду — захист від нескінченного циклу на межі типу.</summary>
    private const int MaxIterations = 200;

    /// <summary>Натуральний логарифм двійки; потрібен для нормалізації <c>Ln</c>.</summary>
    private const decimal Ln2 = 0.693147180559945309417232121458m;

    /// <summary>Натуральний логарифм десятки; дільник у <c>Log10</c>.</summary>
    private const decimal Ln10 = 2.302585092994045684017991454684m;

    /// <summary>Найбільший аргумент <c>Exp</c>, результат якого ще вміщується в <c>decimal</c>.</summary>
    private const decimal MaxExponent = 66m;

    /// <summary>Експонента.</summary>
    /// <param name="x">Показник.</param>
    /// <exception cref="OverflowException">Результат не вміщується в <see cref="decimal"/>.</exception>
    public static decimal Exp(decimal x)
    {
        if (x == 0m)
        {
            return 1m;
        }

        // ⚠ Перевірка ДО обчислення, а не ловля OverflowException усередині
        // ряду: переповнення там сталося б на випадковій ітерації, і частина
        // суми вже була б втрачена.
        if (x > MaxExponent)
        {
            throw new OverflowException($"exp({x}) не вміщується в decimal.");
        }

        if (x < -MaxExponent)
        {
            // Менше за найменший додатний decimal — нуль тут не наближення,
            // а єдине представлюване значення.
            return 0m;
        }

        // Ціла частина відокремлюється навмисно: ряд Тейлора збігається швидко
        // лише поблизу нуля, а на x = 60 йому потрібні сотні членів, і кожен
        // додає похибку.
        var whole = decimal.Truncate(x);
        var fraction = x - whole;

        var result = PowerInt(ExpOne, (int)whole);

        decimal term = 1m;
        decimal sum = 1m;
        for (var n = 1; n <= MaxIterations; n++)
        {
            term = term * fraction / n;
            sum += term;

            if (Math.Abs(term) < Epsilon)
            {
                break;
            }
        }

        return result * sum;
    }

    /// <summary>Основа натурального логарифма з точністю <c>decimal</c>.</summary>
    private const decimal ExpOne = 2.718281828459045235360287471353m;

    /// <summary>Натуральний логарифм.</summary>
    /// <param name="x">Аргумент; має бути додатним.</param>
    /// <exception cref="ArgumentOutOfRangeException">Аргумент не додатний.</exception>
    public static decimal Ln(decimal x)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(x, 0m);

        if (x == 1m)
        {
            return 0m;
        }

        // Нормалізація в [2/3, 4/3]: ряд atanh збігається тим швидше, чим
        // ближче аргумент до одиниці. Без цього ln(1e20) не збігається взагалі.
        var exponent = 0;
        var value = x;

        while (value > 1.333333333333333333333333333m)
        {
            value /= 2m;
            exponent++;
        }

        while (value < 0.666666666666666666666666667m)
        {
            value *= 2m;
            exponent--;
        }

        // ln(v) = 2·atanh((v−1)/(v+1)) — ряд лише з непарними степенями,
        // тобто вдвічі менше членів за той самий результат.
        var z = (value - 1m) / (value + 1m);
        var zSquared = z * z;
        var term = z;
        var sum = z;

        for (var n = 1; n <= MaxIterations; n++)
        {
            term *= zSquared;
            var addition = term / ((2 * n) + 1);
            sum += addition;

            if (Math.Abs(addition) < Epsilon)
            {
                break;
            }
        }

        return (2m * sum) + (exponent * Ln2);
    }

    /// <summary>Десятковий логарифм.</summary>
    /// <param name="x">Аргумент; має бути додатним.</param>
    public static decimal Log10(decimal x) => Ln(x) / Ln10;

    /// <summary>Піднесення до степеня.</summary>
    /// <param name="value">Основа.</param>
    /// <param name="exponent">Показник.</param>
    /// <exception cref="ArgumentException">Дробовий степінь від'ємної основи.</exception>
    /// <exception cref="DivideByZeroException">Від'ємний степінь нуля.</exception>
    public static decimal Pow(decimal value, decimal exponent)
    {
        if (exponent == 0m)
        {
            // ⚠ 0^0 = 1. Спірне в математиці, однозначне тут: так поводяться
            // і Excel, і чинна система, а звіт має сходитися з обома.
            return 1m;
        }

        if (value == 0m)
        {
            return exponent > 0m
                ? 0m
                : throw new DivideByZeroException("Нуль у від'ємному степені не визначений.");
        }

        // ⚠ Цілий показник — МНОЖЕННЯМ, без жодного ряду. Це і точно, і швидко,
        // і саме цей випадок трапляється в методологіях майже завжди
        // (квадрати, куби, 10^n).
        if (exponent == decimal.Truncate(exponent) && Math.Abs(exponent) <= 1000m)
        {
            return PowerInt(value, (int)exponent);
        }

        if (value < 0m)
        {
            throw new ArgumentException(
                $"Дробовий степінь від'ємної основи ({value}^{exponent}) не є дійсним числом.",
                nameof(value));
        }

        return Exp(exponent * Ln(value));
    }

    /// <summary>Квадратний корінь методом Ньютона.</summary>
    /// <param name="x">Аргумент; має бути невід'ємним.</param>
    /// <exception cref="ArgumentOutOfRangeException">Аргумент від'ємний.</exception>
    public static decimal Sqrt(decimal x)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);

        if (x == 0m)
        {
            return 0m;
        }

        // Ньютон, а не exp(ln(x)/2): для кореня він дає точну збіжність за
        // десяток ітерацій і не накопичує похибку двох рядів.
        //
        // ⚠ Початкове наближення теж рахується в decimal. Взяти його з
        // Math.Sqrt було б зручно і нешкідливо — його все одно уточнює
        // ітерація, — але double у цьому проєкті не з'являється взагалі, і
        // виняток «лише для затравки» наступний читач сприйме як дозвіл.
        var guess = InitialSqrtGuess(x);

        for (var i = 0; i < MaxIterations; i++)
        {
            var next = (guess + (x / guess)) / 2m;
            if (Math.Abs(next - guess) < Epsilon)
            {
                return next;
            }

            guess = next;
        }

        return guess;
    }

    /// <summary>Початкове наближення кореня: степінь двійки того самого порядку.</summary>
    /// <remarks>
    /// Точність тут не важлива — важливо, щоб число було того ж порядку:
    /// Ньютон подвоює кількість вірних знаків на ітерацію, і від будь-якого
    /// розумного старту сходиться за десяток кроків.
    /// </remarks>
    private static decimal InitialSqrtGuess(decimal x)
    {
        var guess = 1m;

        while (guess * guess < x && guess < 1e14m)
        {
            guess *= 2m;
        }

        while (guess > 1m && guess * guess > x * 4m)
        {
            guess /= 2m;
        }

        return guess;
    }

    /// <summary>Піднесення до цілого степеня множенням.</summary>
    private static decimal PowerInt(decimal value, int exponent)
    {
        if (exponent < 0)
        {
            return 1m / PowerInt(value, -exponent);
        }

        // Швидке піднесення: log₂(n) множень замість n. На 10^18 різниця між
        // 60 множеннями й 18 — не швидкість, а накопичена похибка.
        decimal result = 1m;
        var factor = value;
        var power = exponent;

        while (power > 0)
        {
            if ((power & 1) == 1)
            {
                result *= factor;
            }

            power >>= 1;
            if (power > 0)
            {
                factor *= factor;
            }
        }

        return result;
    }

    /// <summary>Текстове подання без залежності від локалі — для повідомлень.</summary>
    public static string Format(decimal value)
        => value.ToString(CultureInfo.InvariantCulture);
}
