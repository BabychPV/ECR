// src/Ecr.Application/Integration/RawData/RawDataQuery.cs
using System.Globalization;
using System.Text;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;

namespace Ecr.Application.Integration.RawData;

/// <summary>
/// Спільне ядро читання ряду точок для обох входів <c>D-463</c>: адміністративного («Дані з PI») і
/// коміркового («Звідки це значення»). Межі запиту, курсор, згортка в кошики, проекція одиниць.
/// </summary>
/// <remarks>
/// ⛔ Одне ядро, а не дві копії: межі (вікно, сторінка, кошики) — це захист бази від запиту «за десять
/// років щосекунди», і розбіжність між входами означала б, що один із них цей захист не має.
/// Доступ тут НЕ перевіряється — його перевіряє обробник входу ДО виклику ядра.
/// </remarks>
public static class RawDataQuery
{
    /// <summary>Найдовше вікно — рік із запасом (період PI річний, плюс високосний і межі поясу).</summary>
    public const int MaxRangeDays = 400;

    /// <summary>Типове вікно, якщо межі не названо, — як у перегляді мапінгу.</summary>
    public const int DefaultRangeDays = 7;

    /// <summary>Найбільша сторінка точок.</summary>
    public const int MaxPageSize = 500;

    /// <summary>Типова сторінка точок.</summary>
    public const int DefaultPageSize = 100;

    /// <summary>Найбільше кошиків графіка — стеля розміру відповіді незалежно від вікна.</summary>
    public const int MaxBuckets = 2000;

    /// <summary>Типове число кошиків.</summary>
    public const int DefaultBuckets = 500;

    /// <summary>Найдовший шлях — як <c>ext.RawDataPoint.SourcePath nvarchar(400)</c>.</summary>
    public const int MaxPathLength = 400;

    /// <summary>Сторінка потокового експорту.</summary>
    public const int ExportPageSize = 500;

    /// <summary>Вікно запиту: <c>null</c> — типові межі; час без поясу — UTC.</summary>
    /// <param name="fromUtc">Початок; <c>null</c> — <see cref="DefaultRangeDays"/> до кінця.</param>
    /// <param name="toUtc">Кінець; <c>null</c> — «зараз».</param>
    /// <param name="now">Поточний момент.</param>
    /// <exception cref="BusinessRuleException">Вікно порожнє/перевернуте або довше за <see cref="MaxRangeDays"/>.</exception>
    public static (DateTime From, DateTime To) Window(DateTime? fromUtc, DateTime? toUtc, DateTime now)
    {
        var to = Utc(toUtc) ?? Utc(now)!.Value;
        var from = Utc(fromUtc) ?? to.AddDays(-DefaultRangeDays);

        // ⛔ Перевернуте вікно не «виправляється» обміном меж: помилка в одному з полів дала б
        // правдоподібний перегляд зовсім іншого проміжку.
        if (from >= to)
        {
            throw Invalid(
                "err.ECR-REQ-0422.rawDataRange",
                $"Вікно порожнє: початок {from:O} не раніший за кінець {to:O}.",
                ("fromUtc", from.ToString("O", CultureInfo.InvariantCulture)),
                ("toUtc", to.ToString("O", CultureInfo.InvariantCulture)));
        }

        if (to - from > TimeSpan.FromDays(MaxRangeDays))
        {
            throw Invalid(
                "err.ECR-REQ-0422.rawDataRangeTooLong",
                $"Вікно довше за {MaxRangeDays} діб.",
                ("maxDays", MaxRangeDays.ToString(CultureInfo.InvariantCulture)));
        }

        return (from, to);
    }

    /// <summary>Шлях тега: непорожній і не довший за стовпець сховища.</summary>
    /// <exception cref="BusinessRuleException">Порожній або задовгий шлях.</exception>
    public static string Path(string? path)
    {
        // ⚠ Без Trim: шлях порівнюється дослівно з тим, що записав збір; «виправлений» шлях адресував би інший ряд.
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength)
        {
            throw Invalid(
                "err.ECR-REQ-0422.rawDataPath",
                $"Шлях тега має бути від 1 до {MaxPathLength} символів.",
                ("max", MaxPathLength.ToString(CultureInfo.InvariantCulture)));
        }

        return path;
    }

    /// <summary>Розмір сторінки 1..<see cref="MaxPageSize"/>; <c>0</c> — типовий.</summary>
    /// <exception cref="BusinessRuleException">Поза межами.</exception>
    public static int PageSize(int limit)
    {
        if (limit == 0)
        {
            return DefaultPageSize;
        }

        if (limit is < 1 or > MaxPageSize)
        {
            throw Invalid(
                "err.ECR-REQ-0422.pageSizeOutOfRange",
                $"Розмір сторінки поза межами 1..{MaxPageSize}.",
                ("max", MaxPageSize.ToString(CultureInfo.InvariantCulture)));
        }

        return limit;
    }

    /// <summary>Кількість кошиків 1..<see cref="MaxBuckets"/>; <c>0</c> — типова.</summary>
    /// <exception cref="BusinessRuleException">Поза межами.</exception>
    public static int BucketCount(int buckets)
    {
        if (buckets == 0)
        {
            return DefaultBuckets;
        }

        if (buckets is < 1 or > MaxBuckets)
        {
            throw Invalid(
                "err.ECR-REQ-0422.rawDataBuckets",
                $"Кількість кошиків графіка поза межами 1..{MaxBuckets}.",
                ("max", MaxBuckets.ToString(CultureInfo.InvariantCulture)));
        }

        return buckets;
    }

    /// <summary>Сторінка точок ряду, новіші першими.</summary>
    /// <param name="reader">Сховище.</param>
    /// <param name="units">Довідник одиниць (коди за <c>UnitId</c>).</param>
    /// <param name="key">Ряд і вікно (уже перевірені).</param>
    /// <param name="cursor">Курсор попередньої сторінки.</param>
    /// <param name="limit">Розмір сторінки (уже перевірений).</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<PagedResult<RawPointView>> PageAsync(
        IRawDataReader reader, IUnitCatalog units, RawSeriesKey key, string? cursor, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(key);

        // ⛔ Зіпсований курсор — початок, а не помилка (як `Cursor.Decode`): збережене посилання не ламається.
        var before = DecodeCursor(cursor);
        var rows = await reader.PageAsync(key, before, limit + 1, ct).ConfigureAwait(false);
        var page = rows.Take(limit).ToList();
        var codes = await UnitCodesAsync(units, page, ct).ConfigureAwait(false);

        return new PagedResult<RawPointView>(
            [.. page.Select(p => View(p, codes))],
            rows.Count > limit ? EncodeCursor(page[^1].Timestamp) : null,
            TotalCount: null);
    }

    /// <summary>Згорнутий ряд: ≤ <paramref name="buckets"/> кошиків на все вікно.</summary>
    /// <param name="reader">Сховище.</param>
    /// <param name="key">Ряд і вікно (уже перевірені).</param>
    /// <param name="buckets">Кількість кошиків (уже перевірена).</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<RawSeriesView> SeriesAsync(
        IRawDataReader reader, RawSeriesKey key, int buckets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(key);

        var bucketSeconds = BucketSeconds(key.FromUtc, key.ToUtc, buckets);
        var rows = await reader.BucketsAsync(key, bucketSeconds, ct).ConfigureAwait(false);

        return new RawSeriesView(
            key.FromUtc,
            key.ToUtc,
            bucketSeconds,
            rows.Sum(b => b.Points),
            rows.Sum(b => b.NumericPoints),
            [.. rows.Select(b => new RawSeriesBucket(
                key.FromUtc.AddSeconds(b.Index * bucketSeconds), b.Points, b.Min, b.Max, b.Avg))]);
    }

    /// <summary>Ширина кошика: ціла кількість секунд, щоб вікно лягло в ≤ <paramref name="buckets"/> кошиків.</summary>
    public static long BucketSeconds(DateTime from, DateTime to, int buckets)
    {
        var seconds = (long)Math.Ceiling((to - from).TotalSeconds);
        return Math.Max(1, (seconds + buckets - 1) / buckets);
    }

    /// <summary>Сторінка експорту за зростанням часу; курсор — мітка останньої точки.</summary>
    public static async Task<PagedResult<RawPointView>> AscendingPageAsync(
        IRawDataReader reader, IUnitCatalog units, RawSeriesKey key, string? cursor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(units);

        var rows = await reader
            .PageAscendingAsync(key, DecodeCursor(cursor), ExportPageSize + 1, ct)
            .ConfigureAwait(false);
        var page = rows.Take(ExportPageSize).ToList();
        var codes = await UnitCodesAsync(units, page, ct).ConfigureAwait(false);

        return new PagedResult<RawPointView>(
            [.. page.Select(p => View(p, codes))],
            rows.Count > ExportPageSize ? EncodeCursor(page[^1].Timestamp) : null,
            TotalCount: null);
    }

    /// <summary>Кодує курсор — мітку часу останньої точки сторінки.</summary>
    /// <remarks>Мітка унікальна в ряді (природний ключ <c>UQ_RawDataPoint</c>), тож її досить для keyset.</remarks>
    public static string EncodeCursor(DateTime at)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes("t:" + at.Ticks.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Декодує курсор; зіпсований — <c>null</c> (початок).</summary>
    public static DateTime? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 64)
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[64];
        if (!Convert.TryFromBase64String(cursor, buffer, out var written))
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(buffer[..written]);
        return text.StartsWith("t:", StringComparison.Ordinal)
               && long.TryParse(text.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
               && ticks is > 0 and <= 3155378975999999999
            ? new DateTime(ticks, DateTimeKind.Utc)
            : null;
    }

    /// <summary>Рядок CSV однієї точки.</summary>
    /// <remarks>
    /// ⛔ Текстові поля (значення-рядок, якість, одиниця — дані ЧУЖОЇ системи) йдуть через
    /// <see cref="CsvFormat.Field"/>: значення, що починається з <c>= + - @</c>, Excel виконав би як формулу.
    /// Число форматує сервер інваріантно (цифри, знак, крапка) — формулою воно бути не може, а апостроф
    /// перед від'ємним числом зробив би його текстом у Excel.
    /// </remarks>
    public static string ToCsv(RawPointView row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var number = row.ValueNumeric?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        return string.Join(
                   ',',
                   Audit.AuditExportRunner.Stamp(row.Timestamp),
                   number,
                   CsvFormat.Field(row.ValueString),
                   CsvFormat.Field(row.UnitCode),
                   CsvFormat.Field(row.Quality),
                   Audit.AuditExportRunner.Stamp(row.RetrievedAt),
                   row.CollectionRunId.ToString(CultureInfo.InvariantCulture))
               + CsvFormat.NewLine;
    }

    /// <summary>Заголовок CSV.</summary>
    public static readonly string[] CsvColumns =
        ["timestampUtc", "valueNumeric", "valueString", "unit", "quality", "retrievedAtUtc", "collectionRunId"];

    internal static DateTime? Utc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Unspecified } v => DateTime.SpecifyKind(v, DateTimeKind.Utc),
        { } v => v.ToUniversalTime(),
    };

    private static RawPointView View(RawPoint p, IReadOnlyDictionary<int, string> codes)
        => new(
            DateTime.SpecifyKind(p.Timestamp, DateTimeKind.Utc),
            p.ValueNumeric,
            p.ValueString,
            p.UnitId is { } id && codes.TryGetValue(id, out var code) ? code : null,
            p.Quality,
            DateTime.SpecifyKind(p.RetrievedAt, DateTimeKind.Utc),
            p.CollectionRunId);

    private static async Task<IReadOnlyDictionary<int, string>> UnitCodesAsync(
        IUnitCatalog units, IReadOnlyList<RawPoint> page, CancellationToken ct)
    {
        // ⚠ Довідник кешований сховищем: без одиниць на сторінці — без звернення взагалі.
        if (!page.Any(p => p.UnitId is not null))
        {
            return new Dictionary<int, string>();
        }

        var catalogue = await units.GetAsync(ct).ConfigureAwait(false);
        return catalogue.Units.Values.ToDictionary(u => u.Id, u => u.Code);
    }

    internal static BusinessRuleException Invalid(string messageKey, string message, params (string Key, string Value)[] details)
    {
        var map = new Dictionary<string, object?> { ["messageKey"] = messageKey };
        foreach (var (detailKey, value) in details)
        {
            map[detailKey] = value;
        }

        return new BusinessRuleException(ErrorCodes.RequestInvalid, message, map);
    }
}
