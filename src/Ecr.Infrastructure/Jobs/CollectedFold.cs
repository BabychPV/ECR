// src/Ecr.Infrastructure/Jobs/CollectedFold.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Sources;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Згортка зібраних точок поля за вікно в значення комірки — ОДНЕ місце правила для матеріалізації
/// (<see cref="MaterializeCollectedDataJob"/>) і для «Звідки це значення» (<c>CellSourceStore</c>, <c>D-463</c>).
/// </summary>
/// <remarks>
/// ⛔ Винесено з тіла задачі матеріалізації без зміни поведінки: читання точок вікна (зі стелею), межові точки
/// для згорток за часом, відмова за змішаною одиницею, «очистити», «немає даних», <see cref="PeriodFold"/>,
/// конверсія ПІСЛЯ згортки (<see cref="BoundaryUnitConversion"/>) до масштабу сховища. Копія в переглядачі
/// показувала б число, якого задача не запише.
/// </remarks>
internal static class CollectedFold
{
    /// <summary>Точки поля у вікні (<c>Take(take)</c>), за зростанням часу.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="sourceEntityId">Сутність.</param>
    /// <param name="field">Шлях поля.</param>
    /// <param name="range">Вікно згортки.</param>
    /// <param name="needsTime">Згортка за часом бачить і точки без числа (прогалини).</param>
    /// <param name="take">Стеля + 1: зайва точка — ознака «довше за стелю».</param>
    /// <param name="ct">Скасування.</param>
    public static Task<List<PointRow>> LoadInsideAsync(
        EcrDbContext db, int sourceEntityId, string field, Domain.Entities.Documents.Period.UtcRange range,
        bool needsTime, int take, CancellationToken ct)
        => db.RawDataPoints
            .AsNoTracking()
            .Where(p => p.SourceEntityId == sourceEntityId
                        && p.SourcePath == field
                        && p.Timestamp >= range.StartUtc
                        && p.Timestamp < range.EndUtc
                        && (needsTime || p.ValueNumeric != null))
            .OrderBy(p => p.Timestamp)
            .Take(take)
            .Select(p => new PointRow(p.Timestamp, p.ValueNumeric, p.Quality, p.UnitId))
            .ToListAsync(ct);

    /// <summary>Межові точки для згортки за часом: остання ДО вікна й перша НА чи ПІСЛЯ кінця.</summary>
    /// <remarks>
    /// ⚠ Межові точки беруться без фільтра якості: погана межова точка робить
    /// крайній відрізок прогалиною, і це правильніше, ніж перескочити через неї
    /// до ще давнішої.
    /// </remarks>
    public static async Task<Bounds> LoadBoundsAsync(
        EcrDbContext db, int sourceEntityId, string field, Domain.Entities.Documents.Period.UtcRange range,
        CancellationToken ct)
    {
        var before = await db.RawDataPoints
            .AsNoTracking()
            .Where(p => p.SourceEntityId == sourceEntityId && p.SourcePath == field && p.Timestamp < range.StartUtc)
            .OrderByDescending(p => p.Timestamp)
            .Select(p => new PointRow(p.Timestamp, p.ValueNumeric, p.Quality, p.UnitId))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var after = await db.RawDataPoints
            .AsNoTracking()
            .Where(p => p.SourceEntityId == sourceEntityId && p.SourcePath == field && p.Timestamp >= range.EndUtc)
            .OrderBy(p => p.Timestamp)
            .Select(p => new PointRow(p.Timestamp, p.ValueNumeric, p.Quality, p.UnitId))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return new Bounds(before, after);
    }

    /// <summary>
    /// Згортка одного мапінгу над уже прочитаними точками вікна.
    /// </summary>
    /// <param name="kind">Спосіб згортки мапінгу.</param>
    /// <param name="isStep">Ряд ступінчастий (HSE301 §4.1).</param>
    /// <param name="sourceUnitId">Одиниця джерела, оголошена мапінгом.</param>
    /// <param name="inside">Точки вікна (без перевищення стелі).</param>
    /// <param name="bounds">Межові точки (лише для згорток за часом).</param>
    /// <param name="range">Вікно.</param>
    /// <param name="units">Довідник одиниць для межових точок іншої одиниці (Y1-01); може бути <c>null</c>.</param>
    public static FoldOutcome FoldMap(
        AggregationKind kind,
        bool isStep,
        int? sourceUnitId,
        IReadOnlyList<PointRow> inside,
        Bounds bounds,
        Domain.Entities.Documents.Period.UtcRange range,
        UnitCatalogSnapshot? units)
    {
        ArgumentNullException.ThrowIfNull(inside);

        // ⛔ X3-02 / ФВ-16.10, D-79: точка несе СВОЮ одиницю джерела; змішаний ряд не згортається
        // (лише точки ВСЕРЕДИНІ вікна, Y1-01 — межові переводяться в `Edge`).
        if (HasForeignUnit(inside, sourceUnitId))
        {
            return new FoldOutcome(FoldOutcomeKind.MixedUnits, null, null);
        }

        // Згортки точок — лише точки з числом; якість несе `IsGood`, `PeriodFold` відсіює непридатні (C1-04).
        var points = inside.Where(p => p.Value is not null).Select(p => p.ToTimed()).ToList();
        var series = IsTimeFold(kind) ? TimeSeries(bounds, inside, sourceUnitId, units) : points;

        // ⛔ D-462: у вікні НЕ лишилося жодної сирої точки (джерело видалило всі) — комірку очищують.
        if (inside.Count == 0 && (series.Count == 0
                || PeriodFold.Fold(kind, series, range.StartUtc, range.EndUtc, isStep, maxGap: null).Value is null))
        {
            return new FoldOutcome(FoldOutcomeKind.Clear, null, null);
        }

        if (series.Count == 0)
        {
            return new FoldOutcome(FoldOutcomeKind.NoData, null, null);
        }

        // ⚠ `maxGap: null` — порогу прогалини в конфігурації поки немає (HSE301 §4.1).
        var folded = PeriodFold.Fold(kind, series, range.StartUtc, range.EndUtc, isStep, maxGap: null);
        if (folded.Value is not { } value)
        {
            return new FoldOutcome(FoldOutcomeKind.NoData, null, null);
        }

        return new FoldOutcome(FoldOutcomeKind.Value, value, folded.PercentGood);
    }

    /// <summary>
    /// Згорнуте в одиниці джерела → значення для комірки: конверсія ПІСЛЯ згортки (ФВ-16.10, HSE301 §4.2), одна
    /// арифметика на всю межу, до масштабу сховища (Z1-01).
    /// </summary>
    /// <param name="kind">Спосіб згортки.</param>
    /// <param name="folded">Згорнуте значення (одиниця джерела).</param>
    /// <param name="sourceUnitId">Одиниця джерела.</param>
    /// <param name="targetUnitId">Одиниця цілі.</param>
    /// <param name="units">Довідник; <c>null</c> — без конверсії (викликач завантажує його за <see cref="NeedsCatalog"/>).</param>
    /// <returns>Значення або причина відмови (несумісні одиниці — НЕ тихе число).</returns>
    public static (decimal? Storable, string? Failure) ToStorable(
        AggregationKind kind, decimal folded, int? sourceUnitId, int? targetUnitId, UnitCatalogSnapshot? units)
    {
        try
        {
            var boundary = units is null
                ? BoundaryValue.Unchanged(folded)
                : BoundaryUnitConversion.ConvertFolded(kind, folded, sourceUnitId, targetUnitId, units);

            return (boundary.Storable, null);
        }
        catch (Exception ex) when (ex is DomainException or EcrException)
        {
            return (null, FailureReason.Of(ex));
        }
    }

    /// <summary>Ряд для згортки за часом: межа до вікна, точки вікна, межа на чи після кінця.</summary>
    private static List<TimedPoint> TimeSeries(
        Bounds bounds, IReadOnlyList<PointRow> inside, int? declaredSourceUnitId, UnitCatalogSnapshot? units)
    {
        var series = new List<TimedPoint>(inside.Count + 2);
        if (bounds.Before is { } before)
        {
            series.Add(Edge(before, declaredSourceUnitId, units).ToTimed());
        }

        series.AddRange(inside.Select(p => p.ToTimed()));

        if (bounds.After is { } after)
        {
            series.Add(Edge(after, declaredSourceUnitId, units).ToTimed());
        }

        return series;
    }

    /// <summary>Межова точка в оголошеній одиниці джерела (Y1-01); не переводиться — точка без числа (прогалина).</summary>
    private static PointRow Edge(PointRow point, int? declaredSourceUnitId, UnitCatalogSnapshot? units)
    {
        if (point.UnitId is not { } actual || declaredSourceUnitId is not { } declared || actual == declared)
        {
            return point;
        }

        if (point.Value is not { } value || units is null)
        {
            return point with { Value = null };
        }

        try
        {
            return point with { Value = BoundaryUnitConversion.Convert(value, actual, declared, units) };
        }
        catch (Exception ex) when (ex is DomainException or EcrException)
        {
            return point with { Value = null };
        }
    }

    /// <summary>Чи є межова точка з одиницею джерела, іншою за оголошену (Y1-01).</summary>
    public static bool HasForeignEdge(Bounds bounds, int? declaredSourceUnitId)
        => declaredSourceUnitId is { } declared
           && ((bounds.Before?.UnitId is { } b && b != declared) || (bounds.After?.UnitId is { } a && a != declared));

    /// <summary>Чи є в ряді точка з одиницею джерела, іншою за оголошену мапінгом (X3-02).</summary>
    public static bool HasForeignUnit(IReadOnlyList<PointRow> rows, int? declaredSourceUnitId)
        => declaredSourceUnitId is { } declared
           && rows.Any(p => p.UnitId is { } unit && unit != declared);

    /// <summary>Чи згортка за часом (потребує міток часу й меж вікна).</summary>
    public static bool IsTimeFold(AggregationKind? kind)
        => kind is AggregationKind.TimeWeightedAvg or AggregationKind.TimeIntegral;

    /// <summary>Чи потрібен довідник одиниць: інтеграл або дві різні оголошені одиниці.</summary>
    public static bool NeedsCatalog(AggregationKind kind, int? sourceUnitId, int? targetUnitId)
        => kind == AggregationKind.TimeIntegral
           || (sourceUnitId is { } from && targetUnitId is { } to && from != to);

    /// <summary>Сира точка вікна, як її бачить згортка.</summary>
    /// <remarks>Названий тип, а не анонімний: архітектурне правило «<c>ToListAsync</c> без <c>Take</c>» (`D1-08`).</remarks>
    internal sealed record PointRow(DateTime Timestamp, decimal? Value, string? Quality, int? UnitId)
    {
        /// <summary>
        /// Точка для згортки: придатна, лише якщо має число і якість <see cref="WindowFold.GoodQuality"/>
        /// (або не вказана) — те саме тлумачення, що й у вікна рядка (HSE301 §4.6).
        /// </summary>
        public TimedPoint ToTimed()
            => new(
                Timestamp,
                Value ?? 0m,
                Value is not null
                && (Quality is null || string.Equals(Quality, WindowFold.GoodQuality, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Межові точки вікна; <c>null</c> — точки за межею немає.</summary>
    internal readonly record struct Bounds(PointRow? Before, PointRow? After);
}

/// <summary>Чим закінчилася згортка мапінгу.</summary>
internal enum FoldOutcomeKind
{
    /// <summary>Число для комірки.</summary>
    Value,

    /// <summary>У вікні не лишилося сирих точок — комірку очищують (D-462).</summary>
    Clear,

    /// <summary>Придатних точок немає — комірку не чіпають.</summary>
    NoData,

    /// <summary>У вікні точки різних одиниць джерела (X3-02).</summary>
    MixedUnits,
}

/// <summary>Результат згортки мапінгу.</summary>
/// <param name="Kind">Вид результату.</param>
/// <param name="Folded">Згорнуте значення в одиниці джерела (лише <see cref="FoldOutcomeKind.Value"/>).</param>
/// <param name="PercentGood">Частка покриття, якщо згортка її дала.</param>
internal sealed record FoldOutcome(FoldOutcomeKind Kind, decimal? Folded, decimal? PercentGood);
