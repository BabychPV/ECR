// src/Ecr.Application/Sources/PeriodFold.cs
using Ecr.Domain.Entities.External;

namespace Ecr.Application.Sources;

/// <summary>
/// Згортання точок періоду в одне число (<c>D-118</c>).
/// </summary>
/// <remarks>
/// ⛔ Функція винесена сюди **саме тому**, що її тепер два споживачі:
/// перенесення в комірки (<c>MaterializeCollectedDataJob</c>) і попередній
/// перегляд мапінгу (<c>ФВ-13.14</c>). Друга копія згортки була б найгіршим
/// із можливих дефектів цього екрана: перегляд показував би число, якого
/// нічний перенос не запише, — і йому б вірили. Це той самий клас, що й
/// <c>A7-27</c>, де другий шлях запису в комірки будував мапу колонок інакше
/// за основний.
/// <para>
/// ⚠ Значення за замовчуванням немає: <see cref="AggregationKind"/> приходить
/// із мапінгу, і мапінг без нього доменом не приймається.
/// </para>
/// </remarks>
public static class PeriodFold
{
    /// <summary>Згортає впорядковану серію в одне значення.</summary>
    /// <param name="kind">Спосіб згортання з мапінгу.</param>
    /// <param name="ordered">Значення за <b>зростанням</b> мітки часу.</param>
    /// <returns>Число, яке лягає в комірку.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ordered"/> — <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Серія порожня.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Невідомий спосіб згортання.</exception>
    /// <remarks>
    /// ⛔ Порядок серії — частина контракту, а не деталь виклику:
    /// <see cref="AggregationKind.First"/> і <see cref="AggregationKind.Last"/>
    /// беруть край списку. Серія, впорядкована за чимось іншим, дасть ЧИСЛО, а
    /// не відмову, — показник лічильника на кінець періоду виявиться довільною
    /// точкою всередині нього.
    /// </remarks>
    public static decimal Fold(AggregationKind kind, IReadOnlyList<decimal> ordered)
    {
        ArgumentNullException.ThrowIfNull(ordered);

        if (ordered.Count == 0)
        {
            throw new ArgumentException(
                "Згортати нічого: серія порожня. Відсутність точок — це стан мапінгу, "
                + "а не результат згортання.",
                nameof(ordered));
        }

        return kind switch
        {
            AggregationKind.Sum => Total(ordered),
            AggregationKind.Avg => Total(ordered) / ordered.Count,
            AggregationKind.Min => Edge(ordered, takeSmaller: true),
            AggregationKind.Max => Edge(ordered, takeSmaller: false),
            AggregationKind.First => ordered[0],
            AggregationKind.Last => ordered[^1],

            // ⛔ Згортка за часом без міток часу — не «наближення», а інше число.
            // Відмова тут, а не мовчазне просте середнє.
            AggregationKind.TimeWeightedAvg or AggregationKind.TimeIntegral =>
                throw new ArgumentOutOfRangeException(
                    nameof(kind), kind,
                    "Згортка за часом потребує міток часу й меж вікна: викликайте перевантаження з точками."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "Невідомий спосіб згортання точок періоду."),
        };
    }

    /// <summary>
    /// Згортає ряд точок із мітками часу на вікні <c>[fromUtc, toUtc)</c>
    /// (HSE301 §4.1, <c>D-172</c>).
    /// </summary>
    /// <param name="kind">Спосіб згортання з мапінгу.</param>
    /// <param name="ordered">
    /// Точки за <b>строгим зростанням</b> мітки часу. Для згорток за часом —
    /// разом з останньою точкою ДО вікна й першою НА чи ПІСЛЯ його кінця: саме
    /// з них інтерполюються значення на межах.
    /// </param>
    /// <param name="fromUtc">Початок вікна (включно).</param>
    /// <param name="toUtc">Кінець вікна (виключно).</param>
    /// <param name="isStep">Ряд ступінчастий (<see cref="EntityFieldMap.IsStep"/>).</param>
    /// <param name="maxGap">
    /// Розрив між сусідніми точками, довший за який відрізок вважається
    /// прогалиною й у згортку не входить; <c>null</c> — поріг не застосовується.
    /// </param>
    /// <returns>Число і частка покриття вікна.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ordered"/> — <c>null</c>.</exception>
    /// <exception cref="ArgumentException">
    /// Серія порожня, вікно порожнє чи перевернуте, мітки часу не зростають строго.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Невідомий спосіб згортання або <paramref name="maxGap"/> не додатний.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Лінійний ряд</b> — трапеція між сусідніми точками; <b>ступінчастий</b> —
    /// значення точки тримається до наступної. Межі вікна не додають точок:
    /// відрізок, що перетинає межу, просто обрізається, і значення на межі —
    /// інтерполяція того самого відрізка.
    /// </para>
    /// <para>
    /// ⚠ <b>Без екстраполяції.</b> Частина вікна до першої точки чи після
    /// останньої — прогалина, як і відрізок понад <paramref name="maxGap"/> чи
    /// відрізок, що спирається на точку з <see cref="TimedPoint.IsGood"/> =
    /// <c>false</c> (для ступінчастого — лише ліва точка: саме її значення
    /// тримається на відрізку). Прогалина не входить ні в інтеграл, ні в
    /// знаменник середнього — середнє рахується за покритим часом, а частку
    /// покриття повертає <see cref="TimeFoldResult.PercentGood"/>. Без покриття
    /// значення — <c>null</c>: «даних не було» і «інтеграл нуль» — різні стани.
    /// </para>
    /// <para>
    /// Точні числа: секунди — <see cref="decimal"/> з тіків, ділення — останнім
    /// кроком. Інтеграл — в «одиниця × секунда»; перерахунок у цільову одиницю —
    /// на межі (§4.2).
    /// </para>
    /// <para>
    /// Згортки точок (<see cref="AggregationKind.Sum"/> … <see cref="AggregationKind.First"/>)
    /// беруть точки <c>fromUtc ≤ t &lt; toUtc</c> і згортаються тим самим
    /// <see cref="Fold(AggregationKind, IReadOnlyList{decimal})"/> — без
    /// урахування якості, як і досі; частка покриття для них не визначена.
    /// </para>
    /// </remarks>
    public static TimeFoldResult Fold(
        AggregationKind kind,
        IReadOnlyList<TimedPoint> ordered,
        DateTime fromUtc,
        DateTime toUtc,
        bool isStep,
        TimeSpan? maxGap = null)
    {
        ArgumentNullException.ThrowIfNull(ordered);

        if (ordered.Count == 0)
        {
            throw new ArgumentException(
                "Згортати нічого: серія порожня. Відсутність точок — це стан мапінгу, "
                + "а не результат згортання.",
                nameof(ordered));
        }

        if (toUtc <= fromUtc)
        {
            throw new ArgumentException(
                $"Вікно [{fromUtc:O}, {toUtc:O}) порожнє або перевернуте.", nameof(toUtc));
        }

        if (maxGap is { } gap && gap <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxGap), maxGap, "Поріг прогалини має бути додатним.");
        }

        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Timestamp <= ordered[i - 1].Timestamp)
            {
                throw new ArgumentException(
                    $"Мітки часу мають зростати строго: точка {i} ({ordered[i].Timestamp:O}) "
                    + $"не пізніша за попередню ({ordered[i - 1].Timestamp:O}).",
                    nameof(ordered));
            }
        }

        switch (kind)
        {
            case AggregationKind.TimeWeightedAvg:
            case AggregationKind.TimeIntegral:
                var (integral, covered) = Integrate(ordered, fromUtc, toUtc, isStep, maxGap);
                var window = Seconds(toUtc - fromUtc);
                var percentGood = covered * 100m / window;
                if (covered == 0m)
                {
                    return new TimeFoldResult(null, percentGood);
                }

                return new TimeFoldResult(
                    kind == AggregationKind.TimeIntegral ? integral : integral / covered,
                    percentGood);

            case AggregationKind.Sum:
            case AggregationKind.Avg:
            case AggregationKind.Min:
            case AggregationKind.Max:
            case AggregationKind.First:
            case AggregationKind.Last:
                var inside = new List<decimal>();
                foreach (var point in ordered)
                {
                    if (point.Timestamp >= fromUtc && point.Timestamp < toUtc)
                    {
                        inside.Add(point.Value);
                    }
                }

                return new TimeFoldResult(inside.Count == 0 ? null : Fold(kind, inside), null);

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(kind), kind, "Невідомий спосіб згортання точок періоду.");
        }
    }

    /// <summary>Інтеграл і покритий час на вікні, обидва — у секундах.</summary>
    private static (decimal Integral, decimal Covered) Integrate(
        IReadOnlyList<TimedPoint> ordered, DateTime from, DateTime to, bool isStep, TimeSpan? maxGap)
    {
        var integral = 0m;
        var covered = 0m;

        for (var i = 0; i + 1 < ordered.Count; i++)
        {
            var left = ordered[i];
            var right = ordered[i + 1];

            // Відрізок поза вікном — нічого; перетин — обрізається межами.
            var start = left.Timestamp > from ? left.Timestamp : from;
            var end = right.Timestamp < to ? right.Timestamp : to;
            if (end <= start)
            {
                continue;
            }

            if (maxGap is { } gap && right.Timestamp - left.Timestamp > gap)
            {
                continue;
            }

            if (!left.IsGood || (!isStep && !right.IsGood))
            {
                continue;
            }

            var width = Seconds(end - start);
            covered += width;

            if (isStep)
            {
                integral += left.Value * width;
                continue;
            }

            // Трапеція під прямою на [start, end] = значення в середині × ширина:
            //   v(mid) = v0 + (v1 − v0) · (start + end − 2·t0) / (2·(t1 − t0)).
            // Множення — до ділення: на цілих секундах результат точний.
            var span = Seconds(right.Timestamp - left.Timestamp);
            var offsets = Seconds(start - left.Timestamp) + Seconds(end - left.Timestamp);
            integral += left.Value * width
                + (right.Value - left.Value) * offsets * width / (2m * span);
        }

        return (integral, covered);
    }

    /// <summary>Тривалість у секундах без втрати точності (тіки → <see cref="decimal"/>).</summary>
    private static decimal Seconds(TimeSpan span) => span.Ticks / (decimal)TimeSpan.TicksPerSecond;

    /// <summary>Сума серії.</summary>
    private static decimal Total(IReadOnlyList<decimal> ordered)
    {
        var sum = 0m;
        foreach (var value in ordered)
        {
            sum += value;
        }

        return sum;
    }

    /// <summary>Край серії за величиною.</summary>
    private static decimal Edge(IReadOnlyList<decimal> ordered, bool takeSmaller)
    {
        var edge = ordered[0];
        foreach (var value in ordered)
        {
            if (takeSmaller ? value < edge : value > edge)
            {
                edge = value;
            }
        }

        return edge;
    }
}

/// <summary>Точка ряду з міткою часу для згортки за часом.</summary>
/// <param name="Timestamp">Мітка часу (UTC).</param>
/// <param name="Value">Значення в одиниці джерела.</param>
/// <param name="IsGood">
/// Чи придатна точка; <c>false</c> — відрізок, що на неї спирається, стає
/// прогалиною. Тлумачення якості джерела — справа викликача (§4.6).
/// </param>
public readonly record struct TimedPoint(DateTime Timestamp, decimal Value, bool IsGood = true);

/// <summary>Результат згортки на вікні.</summary>
/// <param name="Value">Число; <c>null</c> — на вікні немає чим згортати.</param>
/// <param name="PercentGood">
/// Частка вікна, покрита даними, у відсотках (0–100); <c>null</c> — для
/// згорток точок, де покриття не визначене.
/// </param>
public sealed record TimeFoldResult(decimal? Value, decimal? PercentGood);
