// src/Ecr.Application/Integration/RowWindowFetch.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Sources;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;

namespace Ecr.Application.Integration;

/// <summary>Вікно рядка в UTC.</summary>
/// <param name="FromUtc">Початок, включно.</param>
/// <param name="ToUtc">Кінець, виключно.</param>
public sealed record RowWindowSpan(DateTime FromUtc, DateTime ToUtc);

/// <summary>Звідки й як рядок рахується ЗАРАЗ — те, що провенанс комірки мусить повторювати.</summary>
/// <param name="RowWindowMapId">Прив'язка.</param>
/// <param name="SourceEntityId">Сутність джерела, яку дав селектор рядка.</param>
/// <param name="SourceField">Шлях атрибута.</param>
/// <param name="Summary">Спосіб згортки прив'язки.</param>
/// <param name="TargetUnitId">Одиниця колонки-цілі.</param>
/// <remarks>
/// ⛔ Аудит I1-02: без цього <see cref="RowWindowFetch.NeedsFetch"/> порівнював лише вікно, і правка
/// селектора (FL-1 → FL-2), атрибута, згортки чи одиниці лишала в комірці число за старою
/// конфігурацією як «уже підтягнуте».
/// </remarks>
public sealed record RowWindowProvenance(
    int RowWindowMapId, int SourceEntityId, string SourceField, RowWindowSummaryKind Summary, int TargetUnitId);

/// <summary>Підсумок згортки одного вікна: статус, значення й опис конверсії.</summary>
/// <param name="Status">Статус запису провенансу.</param>
/// <param name="ValueSource">Згорнуте значення в одиниці джерела (для <c>Total</c> — «одиниця × секунда»).</param>
/// <param name="SourceUnitSymbol">UOM джерела.</param>
/// <param name="ValueTarget">Значення в одиниці колонки; <c>null</c> — писати нічого.</param>
/// <param name="Factor">Множник межі.</param>
/// <param name="ErrorCode">Код відмови джерела чи конверсії.</param>
public sealed record RowWindowFold(
    RowWindowValueStatus Status,
    decimal? ValueSource,
    string? SourceUnitSymbol,
    decimal? ValueTarget,
    decimal? Factor,
    string? ErrorCode);

/// <summary>
/// Чисті правила підтягування значення за вікном рядка (HSE301 A1, §4.4): вікно з комірок у часі
/// проєкту й перетворення відповіді джерела на статус і число для комірки.
/// </summary>
/// <remarks>
/// ⛔ Вікно береться з комірок у ЧАСІ ПРОЄКТУ (<c>D-179</c>): людина вводить «14:09:20», а PI
/// питають про UTC. Читання комірки як UTC зсунуло б вікно на зміщення поясу (Atyrau — 5 год) —
/// правдоподібне число за не той інтервал.
/// </remarks>
public static class RowWindowFetch
{
    /// <summary>Найдовше вікно, доби (§4.4 крок 1): довше — <c>InvalidWindow</c>, до PI не йдемо.</summary>
    public const int MaxWindowDays = 32;

    /// <summary>
    /// Переводить Початок і Кінець із часу проєкту в UTC.
    /// </summary>
    /// <param name="startLocal">Початок у часі проєкту; <c>null</c> — комірка порожня.</param>
    /// <param name="endLocal">Кінець (виключно) у часі проєкту; <c>null</c> — комірка порожня.</param>
    /// <param name="zone">Пояс проєкту.</param>
    /// <param name="span">Вікно в UTC; лише коли метод повернув <c>true</c>.</param>
    /// <returns><c>false</c> — вікно недійсне (<c>InvalidWindow</c>): порожня межа, <c>End ≤ Start</c>,
    /// довше <see cref="MaxWindowDays"/> діб або час у «пропущеній» годині переходу.</returns>
    public static bool TryResolveWindow(DateTime? startLocal, DateTime? endLocal, TimeZoneInfo zone, out RowWindowSpan span)
    {
        ArgumentNullException.ThrowIfNull(zone);
        span = new RowWindowSpan(default, default);

        if (startLocal is not { } start || endLocal is not { } end)
        {
            return false;
        }

        var unspecifiedStart = DateTime.SpecifyKind(start, DateTimeKind.Unspecified);
        var unspecifiedEnd = DateTime.SpecifyKind(end, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecifiedStart) || zone.IsInvalidTime(unspecifiedEnd))
        {
            return false;
        }

        var from = TimeZoneInfo.ConvertTimeToUtc(unspecifiedStart, zone);
        var to = TimeZoneInfo.ConvertTimeToUtc(unspecifiedEnd, zone);
        if (to <= from || to - from > TimeSpan.FromDays(MaxWindowDays))
        {
            return false;
        }

        span = new RowWindowSpan(from, to);
        return true;
    }

    /// <summary>Перетворює відповідь джерела на статус і значення для комірки.</summary>
    /// <param name="summary">Спосіб згортки прив'язки.</param>
    /// <param name="result">Відповідь <c>ReadWindowAsync</c>.</param>
    /// <param name="minPercentGood">Покриття, нижче якого значення — <c>Partial</c>.</param>
    /// <param name="sourceUnitId">Одиниця джерела з <c>RowWindowSource</c>.</param>
    /// <param name="targetUnitId">Одиниця колонки з прив'язки.</param>
    /// <param name="units">Знімок довідника одиниць.</param>
    /// <returns>Підсумок; відмова конверсії — <c>SourceError</c> з кодом, а не виняток задачі.</returns>
    public static RowWindowFold Fold(
        RowWindowSummaryKind summary,
        WindowResult result,
        decimal minPercentGood,
        int sourceUnitId,
        int targetUnitId,
        UnitCatalogSnapshot units)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(units);

        if (result.ErrorCode is { } error)
        {
            return new RowWindowFold(RowWindowValueStatus.SourceError, null, null, null, null, error);
        }

        if (result.Value is not { } value)
        {
            return new RowWindowFold(RowWindowValueStatus.NoData, null, result.SourceUnitSymbol, null, null, null);
        }

        // ⛔ ФВ-16.9 (L3-06): фактична одиниця джерела мусить збігатися з оголошеною.
        // Інакше конверсія йде за ОГОЛОШЕНОЮ, і зміна UOM атрибута в PI
        // (Sm3/h → Sm3/d) тихо дає ×24 у комірці. Count — число точок, без одиниці.
        if (summary != RowWindowSummaryKind.Count
            && !BoundaryUnitConversion.IsDeclaredUnit(sourceUnitId, result.SourceUnitSymbol, units))
        {
            return new RowWindowFold(
                RowWindowValueStatus.SourceError, value, result.SourceUnitSymbol, null, null, IExternalDataSource.QueryRefusedCode);
        }

        BoundaryValue boundary;
        try
        {
            // ⚠ Лише Total — інтеграл «одиниця × секунда»; решта згорток лишають розмірність джерела.
            // Count — число точок, без одиниці: конвертувати нічого.
            boundary = summary switch
            {
                RowWindowSummaryKind.Total => BoundaryUnitConversion.ConvertFolded(
                    AggregationKind.TimeIntegral, value, sourceUnitId, targetUnitId, units),
                RowWindowSummaryKind.Count => BoundaryValue.Unchanged(value),
                _ => BoundaryUnitConversion.ConvertFolded(AggregationKind.Avg, value, sourceUnitId, targetUnitId, units),
            };
        }
        catch (EcrException ex)
        {
            return new RowWindowFold(RowWindowValueStatus.SourceError, value, result.SourceUnitSymbol, null, null, ex.ErrorCode);
        }
        catch (DomainException ex)
        {
            return new RowWindowFold(RowWindowValueStatus.SourceError, value, result.SourceUnitSymbol, null, null, ex.ErrorCode);
        }

        var partial = result.PercentGood is { } good && good < minPercentGood;

        return new RowWindowFold(
            partial ? RowWindowValueStatus.Partial : RowWindowValueStatus.Fetched,
            value,
            result.SourceUnitSymbol,
            // ⛔ Z1-01: до масштабу сховища, інакше рядок отримує `SourceError ECR-CELL-0422`.
            boundary.Storable,
            boundary.Factor,
            null);
    }

    /// <summary>
    /// Чи треба підтягувати рядок знову (§4.4, «повтор»).
    /// </summary>
    /// <param name="current">Чинний запис провенансу комірки; <c>null</c> — ще не підтягували.</param>
    /// <param name="span">Вікно рядка зараз.</param>
    /// <param name="refetchWithinDays">Скільки діб повторювати за пізніми даними PI.</param>
    /// <param name="utcNow">Зараз.</param>
    /// <param name="expected">
    /// Звідки рядок рахується зараз; <c>null</c> — не порівнювати (лише вікно й статус).
    /// </param>
    /// <returns>
    /// <c>true</c> — записів немає; вікно, джерело, атрибут, згортка, одиниця чи прив'язка змінилися;
    /// попередній запис — «немає джерела для селектора»; або значення неповне (<c>NoData</c>, <c>Partial</c>,
    /// <c>SourceError</c>) і вікно закрилося не раніше, ніж <paramref name="refetchWithinDays"/> діб тому;
    /// або вікно на момент читання ще не закрилося (<c>ToUtc &gt; RetrievedAt</c>).
    /// </returns>
    public static bool NeedsFetch(
        RowWindowValue? current, RowWindowSpan span, int refetchWithinDays, DateTime utcNow, RowWindowProvenance? expected = null)
    {
        ArgumentNullException.ThrowIfNull(span);

        if (current is null || current.FromUtc != span.FromUtc || current.ToUtc != span.ToUtc)
        {
            return true;
        }

        // ⛔ Аудит I1-02: те саме вікно, але інше «звідки й як» — число в комірці належить старому
        // джерелу чи старій конфігурації прив'язки, і «вже підтягнуто» тут неправда.
        if (expected is not null
            && (current.RowWindowMapId != expected.RowWindowMapId
                || current.SourceEntityId != expected.SourceEntityId
                || !string.Equals(current.SourceField, expected.SourceField, StringComparison.OrdinalIgnoreCase)
                || current.Summary != expected.Summary
                || current.TargetUnitId != expected.TargetUnitId))
        {
            return true;
        }

        // Селектор рядка раніше не мав джерела, а тепер має (його виправили чи додали джерело).
        if (current.Status is RowWindowValueStatus.NotApplicable)
        {
            return true;
        }

        // ⛔ Вікно недочитане: читали, коли воно ще тривало, — значення було неповним.
        if (current.ToUtc > current.RetrievedAt)
        {
            return true;
        }

        return current.Status is RowWindowValueStatus.NoData or RowWindowValueStatus.Partial or RowWindowValueStatus.SourceError
               && span.ToUtc >= utcNow.AddDays(-refetchWithinDays);
    }
}
