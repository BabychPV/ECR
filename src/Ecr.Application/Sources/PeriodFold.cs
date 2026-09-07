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
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "Невідомий спосіб згортання точок періоду."),
        };
    }

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
