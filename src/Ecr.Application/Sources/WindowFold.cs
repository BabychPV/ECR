// src/Ecr.Application/Sources/WindowFold.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;

namespace Ecr.Application.Sources;

/// <summary>
/// Локальна згортка одного вікна з сирих точок — типова реалізація
/// <see cref="IExternalDataSource.ReadWindowAsync"/> (HSE301 §4.3, <c>D-172</c>).
/// </summary>
/// <remarks>
/// ⛔ Власного інтеграла тут немає: число рахує <see cref="PeriodFold"/>, той
/// самий, що й місячна матеріалізація. Друга копія розійшлася б із першою на
/// межах — клас <c>A7-27</c>. Тут лише читання точок і їхнє тлумачення.
/// <para>
/// ⚠ <b>Точка ДО вікна обов'язкова.</b> Значення на межі <c>a</c> —
/// інтерполяція відрізка від останньої точки до <c>a</c>; без неї коротка подія,
/// в якій стиснення PI не лишило жодної точки, дала б <c>null</c> замість об'єму.
/// Тому читається <c>[a − запас, b + запас)</c>.
/// </para>
/// </remarks>
public static class WindowFold
{
    /// <summary>Точок за одне читання — та сама стеля, що в збору (<c>CollectionRunner</c>).</summary>
    public const int MaxPointsPerRead = 5_000;

    /// <summary>Скільки читань на одне вікно, перш ніж решта піде в прогалини.</summary>
    public const int MaxReads = 20;

    /// <summary>
    /// Запас пошуку точок за межами вікна, коли <see cref="WindowRequest.MaxGap"/> не задано.
    /// </summary>
    /// <remarks>
    /// ⚠ Судження, не вимога: 8 год — типовий <c>CompMax</c> точки PI (28 800 с),
    /// тобто найбільший розрив між архівними точками живого тега. Число впливає
    /// лише на те, як далеко шукати сусіда межі; не знайдений — прогалина, і
    /// <see cref="WindowResult.PercentGood"/> це показує. Із заданим порогом
    /// запас дорівнює порогу: сусід далі за нього однаково дав би прогалину.
    /// </remarks>
    public static readonly TimeSpan DefaultBoundarySearch = TimeSpan.FromHours(8);

    /// <summary>Якість, яку згортка вважає придатною (§4.6).</summary>
    public const string GoodQuality = "Good";

    /// <summary>Читає сирі точки з запасом і згортає вікно.</summary>
    /// <param name="source">Адаптер джерела.</param>
    /// <param name="request">Вікно.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Значення, покриття й прогалини.</returns>
    /// <exception cref="ArgumentNullException">Немає адаптера чи запиту.</exception>
    /// <exception cref="ArgumentException">Вікно порожнє чи перевернуте.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Поріг прогалини не додатний.</exception>
    public static async Task<WindowResult> FromRawAsync(
        IExternalDataSource source, WindowRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var margin = request.MaxGap ?? DefaultBoundarySearch;
        var readFrom = request.FromUtc - margin;
        var readTo = request.ToUtc + margin;

        var points = new List<SourceDataPoint>();
        var failed = new List<TimeInterval>();
        string? errorCode = null;
        var cursor = readFrom;

        for (var read = 0; read < MaxReads && cursor < readTo; read++)
        {
            var batch = await source.ReadAsync(
                new CollectionRequest(
                    request.DataSourceId, request.SourceEntityId, request.SourcePath,
                    cursor, readTo, MaxPointsPerRead),
                ct).ConfigureAwait(false);

            points.AddRange(batch.Points);

            if (batch.ErrorCode is not null)
            {
                // Відмова джерела — не обрізаний батч: дочитувати нема чого,
                // непрочитане йде в прогалини.
                errorCode = batch.ErrorCode;
                failed.AddRange(batch.FailedIntervals);
                cursor = readTo;
                break;
            }

            // Обрізаний батч: хвіст — із першого непрочитаного інтервалу.
            var next = batch.FailedIntervals.Count == 0 ? readTo : batch.FailedIntervals.Min(i => i.FromUtc);
            if (next <= cursor)
            {
                // Прогресу немає: непрочитаний хвіст піде в прогалини нижче.
                break;
            }

            // Сусід правої межі вже є — усе, що до нього, прочитано (точки йдуть за часом).
            cursor = points.Exists(p => p.Timestamp >= request.ToUtc) ? readTo : next;
        }

        if (cursor < readTo)
        {
            failed.Add(new TimeInterval(cursor, readTo));
        }

        return Fold(request, points, failed, errorCode);
    }

    /// <summary>
    /// Згортає вже прочитані точки — чиста частина <see cref="FromRawAsync"/>.
    /// </summary>
    /// <param name="request">Вікно.</param>
    /// <param name="points">Точки в будь-якому порядку, можливо з запасом за межами вікна.</param>
    /// <param name="failed">Інтервали, які прочитати не вдалося.</param>
    /// <param name="errorCode">Код відмови джерела.</param>
    /// <returns>Значення, покриття й прогалини.</returns>
    public static WindowResult Fold(
        WindowRequest request,
        IReadOnlyList<SourceDataPoint> points,
        IReadOnlyList<TimeInterval> failed,
        string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(failed);
        Validate(request);

        var (from, to) = (request.FromUtc, request.ToUtc);
        var series = Series(points, from, to);
        var timed = series.Select(p => p.Timed).ToList();
        var inside = timed.Count(p => p.IsGood && p.Timestamp >= from && p.Timestamp < to);
        var unit = series.LastOrDefault(p => p.Unit is not null).Unit;

        var gaps = Merge(
            Uncovered(timed, from, to, request.IsStep, request.MaxGap)
                .Concat(failed.Select(i => Clip(i, from, to)).OfType<TimeInterval>()));

        decimal? value = null;
        decimal? percentGood = null;

        switch (request.Summary)
        {
            case SourceSummaryKind.Total:
            case SourceSummaryKind.Average:
                percentGood = 0m;
                if (timed.Count == 0)
                {
                    break;
                }

                var folded = PeriodFold.Fold(
                    request.Summary == SourceSummaryKind.Total
                        ? AggregationKind.TimeIntegral
                        : AggregationKind.TimeWeightedAvg,
                    timed, from, to, request.IsStep, request.MaxGap);
                value = folded.Value;
                percentGood = folded.PercentGood;
                break;

            case SourceSummaryKind.Minimum:
            case SourceSummaryKind.Maximum:
                // ⚠ Згортка точок у PeriodFold якості не бачить — погані
                // відсіюються тут (§4.6), до виклику.
                var good = timed.Where(p => p.IsGood).ToList();
                if (good.Count > 0)
                {
                    value = PeriodFold.Fold(
                        request.Summary == SourceSummaryKind.Minimum ? AggregationKind.Min : AggregationKind.Max,
                        good, from, to, request.IsStep, request.MaxGap).Value;
                }

                break;

            case SourceSummaryKind.Count:
                value = inside;
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(request), request.Summary, "Невідомий спосіб згортки вікна.");
        }

        return new WindowResult(value, unit, inside, percentGood, WindowComputedBy.Local, gaps, errorCode);
    }

    /// <summary>Чи придатна точка: число і якість <see cref="GoodQuality"/> (або не вказана).</summary>
    /// <param name="point">Точка джерела.</param>
    /// <remarks>
    /// ⚠ Якість не вказана — придатна: адаптери без колонки якості й так пишуть
    /// <c>Good</c> (<c>SqlDataSource</c>, <c>PiSqlClientDataSource</c>). Текстове
    /// значення (системний стан PI, <c>I/O Timeout</c>) — непридатне: у число
    /// воно не перетворюється (§4.6).
    /// </remarks>
    public static bool IsGood(SourceDataPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);

        return point.ValueNumeric is not null
               && (point.Quality is null
                   || string.Equals(point.Quality, GoodQuality, StringComparison.OrdinalIgnoreCase));
    }

    private static void Validate(WindowRequest request)
    {
        if (request.ToUtc <= request.FromUtc)
        {
            throw new ArgumentException(
                $"Вікно [{request.FromUtc:O}, {request.ToUtc:O}) порожнє або перевернуте.", nameof(request));
        }

        if (request.MaxGap is { } gap && gap <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), gap, "Поріг прогалини має бути додатним.");
        }
    }

    /// <summary>
    /// Ряд для <see cref="PeriodFold"/>: за строгим зростанням часу, лише
    /// остання точка до вікна, точки вікна й перша на чи після його кінця.
    /// </summary>
    /// <remarks>
    /// ⚠ Дві точки з однаковою міткою (повторне читання межі батча, правка в
    /// архіві PI) — лишається пізніше прочитана: PeriodFold вимагає строгого
    /// зростання, а відмова тут коштувала б вікна.
    /// </remarks>
    private static List<(TimedPoint Timed, string? Unit)> Series(
        IReadOnlyList<SourceDataPoint> points, DateTime from, DateTime to)
    {
        var ordered = points
            .Select((p, i) => (Point: p, Order: i))
            .GroupBy(x => x.Point.Timestamp)
            .Select(g => g.MaxBy(x => x.Order).Point)
            .OrderBy(p => p.Timestamp)
            .ToList();

        var before = ordered.FindLastIndex(p => p.Timestamp < from);
        var after = ordered.FindIndex(p => p.Timestamp >= to);
        var first = before < 0 ? 0 : before;
        var last = after < 0 ? ordered.Count - 1 : after;

        var series = new List<(TimedPoint, string?)>();
        for (var i = first; i <= last && i < ordered.Count; i++)
        {
            var p = ordered[i];
            series.Add((new TimedPoint(p.Timestamp, p.ValueNumeric ?? 0m, IsGood(p)), p.SourceUnitSymbol));
        }

        return series;
    }

    /// <summary>
    /// Непокриті відрізки вікна за тими самими правилами, що покриття в
    /// <see cref="PeriodFold"/> (без екстраполяції, поріг, якість).
    /// </summary>
    /// <remarks>
    /// ⛔ Сума їхніх тривалостей мусить дорівнювати
    /// <c>(100 − PercentGood)%</c> вікна — це звіряє <c>WindowFoldTests</c>: розбіжність
    /// з <see cref="PeriodFold"/> стане червоним тестом, а не тихою суперечністю.
    /// </remarks>
    private static List<TimeInterval> Uncovered(
        List<TimedPoint> series, DateTime from, DateTime to, bool isStep, TimeSpan? maxGap)
    {
        var covered = new List<TimeInterval>();
        for (var i = 0; i + 1 < series.Count; i++)
        {
            var (left, right) = (series[i], series[i + 1]);
            var start = left.Timestamp > from ? left.Timestamp : from;
            var end = right.Timestamp < to ? right.Timestamp : to;
            var usable = end > start
                         && !(maxGap is { } gap && right.Timestamp - left.Timestamp > gap)
                         && left.IsGood && (isStep || right.IsGood);
            if (usable)
            {
                covered.Add(new TimeInterval(start, end));
            }
        }

        var gaps = new List<TimeInterval>();
        var cursor = from;
        foreach (var piece in Merge(covered))
        {
            if (piece.FromUtc > cursor)
            {
                gaps.Add(new TimeInterval(cursor, piece.FromUtc));
            }

            cursor = piece.ToUtc > cursor ? piece.ToUtc : cursor;
        }

        if (cursor < to)
        {
            gaps.Add(new TimeInterval(cursor, to));
        }

        return gaps;
    }

    private static TimeInterval? Clip(TimeInterval interval, DateTime from, DateTime to)
    {
        var start = interval.FromUtc > from ? interval.FromUtc : from;
        var end = interval.ToUtc < to ? interval.ToUtc : to;

        return end > start ? new TimeInterval(start, end) : null;
    }

    /// <summary>Об'єднує інтервали, що перетинаються чи торкаються.</summary>
    private static List<TimeInterval> Merge(IEnumerable<TimeInterval> intervals)
    {
        var merged = new List<TimeInterval>();
        foreach (var interval in intervals.OrderBy(i => i.FromUtc))
        {
            if (merged.Count > 0 && interval.FromUtc <= merged[^1].ToUtc)
            {
                var lastEnd = merged[^1].ToUtc > interval.ToUtc ? merged[^1].ToUtc : interval.ToUtc;
                merged[^1] = merged[^1] with { ToUtc = lastEnd };
                continue;
            }

            merged.Add(interval);
        }

        return merged;
    }
}
