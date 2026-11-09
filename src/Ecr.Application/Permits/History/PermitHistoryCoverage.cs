// src/Ecr.Application/Permits/History/PermitHistoryCoverage.cs
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;

namespace Ecr.Application.Permits.History;

/// <summary>
/// Feature (j) P2: the validity rule (h) for a row/header WITH permit history — each month is checked against the permits
/// in force in it (base permit + segments), each on its own days.
/// </summary>
public static class PermitHistoryCoverage
{
    /// <summary>
    /// Months (first days) of <paramref name="months"/> in which some permit in force is not valid on ANY of its days of
    /// that month (registry validity <c>[ValidFrom, ValidTo)</c>). A permit valid on part of its days still covers the
    /// month, as rule (h) does for a single permit.
    /// </summary>
    /// <param name="spans">History spans.</param>
    /// <param name="entries">Registry entries of the spans.</param>
    /// <param name="months">Months to check.</param>
    public static IReadOnlyList<DateOnly> Uncovered(
        IReadOnlyList<PermitSpan> spans, IReadOnlyDictionary<long, RegistryEntry> entries, IEnumerable<DateOnly> months)
    {
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(months);
        return [.. months
            .Select(m => new DateOnly(m.Year, m.Month, 1))
            .Distinct()
            .Where(m => !PermitTimeline.InMonth(spans, m).All(s => entries.TryGetValue(s.EntryId, out var e) && Covers(e, s, m)))
            .Order()];
    }

    /// <summary>The first permit in force in <paramref name="month"/> that does not cover its days; <c>null</c> — all do.</summary>
    public static long? FirstUncoveredEntry(
        IReadOnlyList<PermitSpan> spans, IReadOnlyDictionary<long, RegistryEntry> entries, DateOnly month)
    {
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(entries);
        return PermitTimeline.InMonth(spans, month)
            .FirstOrDefault(s => !(entries.TryGetValue(s.EntryId, out var e) && Covers(e, s, month)))?.EntryId;
    }

    /// <summary>
    /// The permit credited with a CALCULATED month value (coordinator 09.11): calculated months are never split — the
    /// permit in force on the month's last day.
    /// </summary>
    public static PermitSpan OnLastDay(IReadOnlyList<PermitSpan> spans, DateOnly month)
    {
        ArgumentNullException.ThrowIfNull(spans);
        var last = new DateOnly(month.Year, month.Month, 1).AddMonths(1).AddDays(-1);
        return spans.Last(s => s.From is null || s.From <= last);
    }

    /// <summary>The spans of a row's history from stored segments (base permit = the cell value).</summary>
    public static IReadOnlyList<PermitSpan> SpansOf(long baseEntryId, IEnumerable<PermitSegment> segments)
        => PermitTimeline.Spans(baseEntryId, segments.Select(s => (s.ValidFrom, s.RegistryEntryId)));

    private static bool Covers(RegistryEntry entry, PermitSpan span, DateOnly month)
    {
        var first = month;
        var last = month.AddMonths(1).AddDays(-1);
        var from = span.From is { } f && f > first ? f : first;
        var to = span.Until is { } u && u < last ? u : last;
        return (entry.ValidFrom is null || entry.ValidFrom <= to) && (entry.ValidTo is null || entry.ValidTo > from);
    }
}

/// <summary>
/// Marks the month-total write of a SPLIT (<see cref="SaveCellSplitsHandler"/>) for <c>PatchCellsHandler</c>: inside it
/// a value into a month shared by two permits is the sum of the parts and is not refused with <c>ECR-CELL-4225</c>.
/// </summary>
/// <remarks>⚠ Async-local: only the split handler's own call chain sees it; a request cannot set it.</remarks>
public static class PermitSplitWrite
{
    private static readonly AsyncLocal<bool> Flag = new();

    /// <summary>The current call is the split handler writing month totals.</summary>
    public static bool Active => Flag.Value;

    /// <summary>Opens the scope; dispose to close.</summary>
    public static IDisposable Begin()
    {
        Flag.Value = true;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => Flag.Value = false;
    }
}
