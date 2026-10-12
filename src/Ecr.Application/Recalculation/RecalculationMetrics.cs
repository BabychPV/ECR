using System.Diagnostics.Metrics;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Метрики перерахунку, що емітуються з Application. Meter <c>Ecr</c> — той самий, що в
/// <c>EcrMetrics</c> та <c>InfrastructureMetrics</c> (OpenTelemetry підписується за назвою).
/// </summary>
/// <remarks>
/// ⚠ Статичний Meter, а не <c>IMeterFactory</c>: <c>SliceEvaluationContext</c> створюється й без DI
/// (прямі <c>new</c> у тестах), а <c>Ecr.Worker</c> не має <c>EcrMetrics</c>. Тегів немає:
/// ідентифікатори формул у метриці роздули б кардинальність, а подробиця — у журналі.
/// </remarks>
public static class RecalculationMetrics
{
    /// <summary>Назва Meter — та сама, що в <c>EcrMetrics.MeterName</c>.</summary>
    public const string MeterName = "Ecr";

    /// <summary>
    /// Діапазон формули ОПУБЛІКОВАНОЇ версії з графом залежностей розкрито за поточним порядком
    /// рядків, бо в збереженому графі немає пробігу для його посилання (ФВ-0.2).
    /// </summary>
    public const string RangeFallback = "ecr.recalc.range_fallback";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Fallbacks = Meter.CreateCounter<long>(
        RangeFallback, "{range}", "Діапазони опублікованих формул, розкриті за поточним порядком рядків");

    /// <summary>Фіксує діапазон, розкритий за поточним порядком попри збережений граф.</summary>
    public static void RecordRangeFallback() => Fallbacks.Add(1);
}
