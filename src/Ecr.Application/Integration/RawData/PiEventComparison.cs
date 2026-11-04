// src/Ecr.Application/Integration/RawData/PiEventComparison.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Application.Integration.SourceEvents;
using Ecr.Application.Ports;
using Ecr.Application.Sources;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;

namespace Ecr.Application.Integration.RawData;

/// <summary>Порівняння «PI каже» з «у комірці» для одного атрибута (<c>D-465</c>).</summary>
public enum PiValueComparison
{
    /// <summary>Комірка містить рівно те, що записав би синк.</summary>
    Match,

    /// <summary>У комірці інше значення, і останній запис — людини (D-118: синк лишає його за людиною).</summary>
    ManualEdit,

    /// <summary>Значення PI в комірку не записано — причина в <see cref="PiValueReason"/>.</summary>
    NotWritten,

    /// <summary>У комірці інше значення від машини: синк ще не записав нове прочитане (наступний прогін).</summary>
    NotUpdated,

    /// <summary>Джерело атрибута не дало — синк комірку не чіпає.</summary>
    NoSourceValue,

    /// <summary>Значення PI довше за сховище — порівняння не робиться.</summary>
    NotComparable,

    /// <summary>Документ чи колонку читач не бачить — значення комірки не показується.</summary>
    Hidden,
}

/// <summary>Чому значення PI не лягло в комірку (чи чому подія без рядка).</summary>
public enum PiValueReason
{
    /// <summary>Подія ще триває — рядка немає.</summary>
    Open,

    /// <summary>Період закритий — нічого не пишеться.</summary>
    PeriodClosed,

    /// <summary>Періоду чи екземпляра таблиці ще немає.</summary>
    PeriodNotOpen,

    /// <summary>Аркуш поданий чи затверджений — запис до повернення.</summary>
    SheetLocked,

    /// <summary>Стеля рядків таблиці.</summary>
    RowLimit,

    /// <summary>Значення без відповідника в довіднику / одиниці / типі колонки.</summary>
    Unmapped,

    /// <summary>Початок прив'язаної події переїхав в інший період; рядок лишився у старому.</summary>
    PeriodChanged,

    /// <summary>Подія ще триває, а її період уже закрито — рядка не буде.</summary>
    OpenAtPeriodClose,

    /// <summary>Комірку востаннє правила людина (D-118).</summary>
    KeptManual,

    /// <summary>Джерело при повному прочитанні подію не повернуло; рядок лишився як є.</summary>
    Missing,

    /// <summary>PI дав нове значення після останнього запису — запишеться наступним прогоном.</summary>
    Pending,
}

/// <summary>Атрибут події: що прочитано з PI, що записав би синк, що в комірці.</summary>
/// <param name="ColumnDefId">Колонка; <c>null</c> — атрибут прочитано, але не мапиться (звуження тощо).</param>
/// <param name="ColumnCode">Код колонки.</param>
/// <param name="Attribute">Атрибут або <c>$start</c>/<c>$end</c>/<c>$name</c>.</param>
/// <param name="Scope">Де лежить атрибут.</param>
/// <param name="PiValueNumeric">Число з PI (одиниця джерела).</param>
/// <param name="PiValueText">Текст із PI (для дат — ISO UTC).</param>
/// <param name="PiValueTruncated">Текст PI довший за сховище — показано початок.</param>
/// <param name="PiUnit">UOM джерела.</param>
/// <param name="PiChangedAt">Коли синк уперше прочитав це значення (UTC).</param>
/// <param name="ExpectedValue">Що записав би синк (одиниця й пояс документа); <c>null</c> — нічого.</param>
/// <param name="CellValue">Що в комірці; <c>null</c> — порожньо чи приховано.</param>
/// <param name="CellOrigin">Хто записав комірку востаннє (<c>Integration</c>, <c>UserEdit</c>…).</param>
/// <param name="Comparison"><see cref="PiValueComparison"/>; <c>null</c> — атрибут не мапиться в колонку.</param>
/// <param name="Reason"><see cref="PiValueReason"/>; <c>null</c> — без причини.</param>
public sealed record PiEventAttributeView(
    int? ColumnDefId,
    string? ColumnCode,
    string Attribute,
    SourceEventAttributeScope Scope,
    decimal? PiValueNumeric,
    string? PiValueText,
    bool PiValueTruncated,
    string? PiUnit,
    DateTime? PiChangedAt,
    string? ExpectedValue,
    string? CellValue,
    string? CellOrigin,
    string? Comparison,
    string? Reason);

/// <summary>
/// Порівнює прочитане з PI з коміркою рядка події — ТИМ САМИМ <see cref="SourceEventRowBuilder"/>, що пише синк
/// (одиниці, довідники, пояс проєкту, «без вгадування»), тож «що записав би синк» — не друга реалізація правил.
/// </summary>
/// <remarks>Чиста функція: усе приходить готовим (контекст мапінгу, значення, комірки).</remarks>
public static class PiEventComparison
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Формат дати комірки/очікуваного значення (пояс проєкту, до секунди).</summary>
    public const string DateFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Причина на рівні події: чому рядка немає чи чому він не оновлюється.</summary>
    /// <param name="link">Зв'язок.</param>
    /// <param name="context">Контекст мапінгу (періоди, подані аркуші).</param>
    public static PiValueReason? LinkReason(PiEventLink link, PiEventMapContext context)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(context);

        var target = SourceEventPeriods.Locate(link.StartUtc, context.Periods);
        if (link.TableInstanceId is null)
        {
            if (target is not null && context.LockedPeriods.Contains(target.PeriodKey)
                && target.State is PeriodState.Open or PeriodState.Grace && link.Status != SourceEventLinkStatus.Open)
            {
                return PiValueReason.SheetLocked;
            }

            return link.Status switch
            {
                SourceEventLinkStatus.Open => target is { State: PeriodState.Closed }
                    ? PiValueReason.OpenAtPeriodClose
                    : PiValueReason.Open,
                SourceEventLinkStatus.PeriodClosed => PiValueReason.PeriodClosed,
                SourceEventLinkStatus.PeriodNotOpen => PiValueReason.PeriodNotOpen,
                SourceEventLinkStatus.RowLimit => PiValueReason.RowLimit,
                _ => null,
            };
        }

        if (link.Status == SourceEventLinkStatus.PeriodChanged)
        {
            return PiValueReason.PeriodChanged;
        }

        if (link.Status == SourceEventLinkStatus.Missing)
        {
            return PiValueReason.Missing;
        }

        var rowPeriod = context.Periods.FirstOrDefault(p => p.PeriodKey == link.PeriodKey);
        if (link.PeriodKey is { } key && context.LockedPeriods.Contains(key))
        {
            return PiValueReason.SheetLocked;
        }

        if (rowPeriod is not null && rowPeriod.State is not (PeriodState.Open or PeriodState.Grace))
        {
            return PiValueReason.PeriodClosed;
        }

        return link.Status switch
        {
            SourceEventLinkStatus.Unmapped => PiValueReason.Unmapped,
            SourceEventLinkStatus.PeriodClosed => PiValueReason.PeriodClosed,
            _ => null,
        };
    }

    /// <summary>Порівнює кожне поле мапінгу; плюс прочитані атрибути без поля (без порівняння).</summary>
    /// <param name="link">Зв'язок.</param>
    /// <param name="context">Контекст мапінгу.</param>
    /// <param name="timeZone">Пояс проєкту.</param>
    /// <param name="values">Значення атрибутів зв'язку за останнім прочитанням.</param>
    /// <param name="cellOf">Комірка рядка за колонкою; <c>null</c> — документ читачеві не видно.</param>
    /// <param name="columnReadable">Чи видно читачеві колонку в періоді рядка.</param>
    public static IReadOnlyList<PiEventAttributeView> Compare(
        PiEventLink link,
        PiEventMapContext context,
        TimeZoneInfo timeZone,
        IReadOnlyList<PiObservedValue> values,
        Func<int, PiCellValue?>? cellOf,
        Func<int, bool> columnReadable)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(columnReadable);

        var ev = new SourceEvent(
            link.SourceEventId,
            string.Empty,
            link.EventName,
            DateTime.SpecifyKind(link.StartUtc, DateTimeKind.Utc),
            link.EndUtc is { } end ? DateTime.SpecifyKind(end, DateTimeKind.Utc) : null,
            link.SourceModifiedUtc,
            link.PrimaryElement,
            null,
            [.. values.Select(v => new SourceEventAttribute(v.Attribute, v.Scope, v.Numeric, v.Text, v.Unit))]);
        var built = SourceEventRowBuilder.Build(ev, context.Fields, timeZone, context.Units);
        var expected = built.Cells.GroupBy(c => c.ColumnDefId).ToDictionary(g => g.Key, g => g.First().Value);
        var builderUnmapped = built.Unmapped.Select(u => u.Column).ToHashSet(StringComparer.Ordinal);
        var kept = ParseList<string>(link.KeptManualJson).ToHashSet(StringComparer.Ordinal);
        var unmapped = ParseList<UnmappedItem>(link.UnmappedJson).Select(u => u.Column).OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var linkReason = LinkReason(link, context);

        var used = new HashSet<PiObservedValue>();
        var result = new List<PiEventAttributeView>(context.Fields.Count + values.Count);
        foreach (var field in context.Fields)
        {
            var observed = Observed(field, values);
            if (observed is not null)
            {
                used.Add(observed);
            }

            var (piNumber, piText) = PiValueOf(field, link, observed);
            var hasPi = piNumber is not null || piText is not null;
            var expectedValue = expected.GetValueOrDefault(field.ColumnDefId);
            var cell = cellOf?.Invoke(field.ColumnDefId);
            var visible = cellOf is not null && link.TableInstanceId is not null && columnReadable(field.ColumnDefId);

            var (comparison, reason) = Decide(
                field, link, hasPi, observed?.Truncated == true, expectedValue, cell, visible, linkReason,
                kept.Contains(field.ColumnCode) || (cell?.LastOrigin is { } origin && CellChangeOrigins.IsHuman(origin)),
                builderUnmapped.Contains(field.ColumnCode) || unmapped.Contains(field.ColumnCode),
                observed,
                context.ColumnScales.GetValueOrDefault(field.ColumnDefId));

            result.Add(new PiEventAttributeView(
                field.ColumnDefId,
                field.ColumnCode,
                field.SourceAttribute,
                field.Scope,
                piNumber,
                piText,
                observed?.Truncated == true,
                observed?.Unit,
                observed?.ChangedAt is { } changed ? DateTime.SpecifyKind(changed, DateTimeKind.Utc) : null,
                expectedValue is null ? null : Display(expectedValue, field),
                visible && cell is { HasValue: true } ? Display(cell, field) : null,
                visible ? cell?.LastOrigin : null,
                comparison.ToString(),
                reason?.ToString()));
        }

        // Прочитані атрибути без поля (атрибут звуження мапінгу тощо) — лише значення PI, без порівняння.
        foreach (var value in values.Where(v => !used.Contains(v)).OrderBy(v => v.Attribute, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(new PiEventAttributeView(
                null, null, value.Attribute, value.Scope, value.Numeric, value.Text, value.Truncated, value.Unit,
                DateTime.SpecifyKind(value.ChangedAt, DateTimeKind.Utc), null, null, null, null, null));
        }

        return result;
    }

    private static (PiValueComparison, PiValueReason?) Decide(
        SourceEventFieldPlan field,
        PiEventLink link,
        bool hasPi,
        bool truncated,
        IntegrationValue? expected,
        PiCellValue? cell,
        bool visible,
        PiValueReason? linkReason,
        bool manual,
        bool unmapped,
        PiObservedValue? observed,
        byte? scale)
    {
        if (!hasPi)
        {
            return (PiValueComparison.NoSourceValue, null);
        }

        if (link.TableInstanceId is null)
        {
            return (PiValueComparison.NotWritten, linkReason ?? PiValueReason.PeriodNotOpen);
        }

        if (!visible)
        {
            return (PiValueComparison.Hidden, null);
        }

        if (truncated)
        {
            return (PiValueComparison.NotComparable, null);
        }

        if (unmapped || expected is null)
        {
            return (PiValueComparison.NotWritten, PiValueReason.Unmapped);
        }

        if (Equal(expected, cell, field.ColumnType, scale))
        {
            return (PiValueComparison.Match, null);
        }

        if (manual)
        {
            return (PiValueComparison.ManualEdit, PiValueReason.KeptManual);
        }

        if (linkReason is PiValueReason.SheetLocked or PiValueReason.PeriodClosed or PiValueReason.PeriodChanged
            or PiValueReason.Missing)
        {
            return (PiValueComparison.NotWritten, linkReason);
        }

        // Значення PI прочитано після останнього запису синку — запишеться наступним прогоном.
        return (PiValueComparison.NotUpdated,
            observed is not null && observed.ChangedAt >= link.LastSyncAt ? PiValueReason.Pending : null);
    }

    /// <summary>Чи комірка містить рівно очікуване значення (число — після округлення до масштабу колонки).</summary>
    /// <param name="expected">Що записав би синк.</param>
    /// <param name="cell">Комірка; <c>null</c> — її немає.</param>
    /// <param name="columnType">Тип колонки.</param>
    /// <param name="scale">Масштаб колонки.</param>
    public static bool Equal(IntegrationValue expected, PiCellValue? cell, CellDataType columnType, byte? scale)
    {
        ArgumentNullException.ThrowIfNull(expected);

        if (cell is not { HasValue: true })
        {
            return false;
        }

        return expected.Kind switch
        {
            IntegrationValueKind.Number => cell.Numeric is { } stored
                && IntegrationValueScale.Round((decimal)expected.Raw, columnType, scale)
                   == IntegrationValueScale.Round(stored, columnType, scale),
            IntegrationValueKind.Text => string.Equals((string)expected.Raw, cell.Text, StringComparison.Ordinal),
            IntegrationValueKind.Date => cell.Date is { } date
                && Truncate((DateTime)expected.Raw) == Truncate(date),
            IntegrationValueKind.Lookup => cell.RegistryEntryId == (long)expected.Raw,
            _ => false,
        };
    }

    private static long Truncate(DateTime value) => value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond);

    private static PiObservedValue? Observed(SourceEventFieldPlan field, IReadOnlyList<PiObservedValue> values)
        => field.SourceAttribute.StartsWith('$')
            ? null
            : values.FirstOrDefault(v => v.Scope == field.Scope
                                         && string.Equals(v.Attribute.Trim(), field.SourceAttribute.Trim(), StringComparison.OrdinalIgnoreCase));

    private static (decimal? Number, string? Text) PiValueOf(SourceEventFieldPlan field, PiEventLink link, PiObservedValue? observed)
        => field.SourceAttribute switch
        {
            SourceEventMap.StartAttribute => (null, Iso(link.StartUtc)),
            SourceEventMap.EndAttribute => (null, link.EndUtc is { } end ? Iso(end) : null),
            SourceEventMap.NameAttribute => (null, string.IsNullOrWhiteSpace(link.EventName) ? null : link.EventName),
            _ => observed is null
                ? (null, null)
                : (observed.Numeric, string.IsNullOrWhiteSpace(observed.Text) ? null : observed.Text),
        };

    private static string Iso(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Display(IntegrationValue value, SourceEventFieldPlan field) => value.Kind switch
    {
        IntegrationValueKind.Number => ((decimal)value.Raw).ToString("G29", CultureInfo.InvariantCulture),
        IntegrationValueKind.Date => ((DateTime)value.Raw).ToString(DateFormat, CultureInfo.InvariantCulture),
        IntegrationValueKind.Lookup => EntryCode(field, (long)value.Raw),
        _ => Convert.ToString(value.Raw, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static string? Display(PiCellValue cell, SourceEventFieldPlan field)
    {
        if (cell.RegistryEntryId is { } entry)
        {
            return EntryCode(field, entry);
        }

        if (cell.Numeric is { } number)
        {
            return number.ToString("G29", CultureInfo.InvariantCulture);
        }

        if (cell.Date is { } date)
        {
            return date.ToString(DateFormat, CultureInfo.InvariantCulture);
        }

        return cell.Text;
    }

    private static string EntryCode(SourceEventFieldPlan field, long id)
        => field.Entries.FirstOrDefault(e => e.Id == id)?.Code ?? "#" + id.ToString(CultureInfo.InvariantCulture);

    private static List<T> ParseList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record UnmappedItem(string? Column, string? Value);
}
