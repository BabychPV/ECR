// src/Ecr.Application/Permits/History/PermitTimeline.cs
using System.Globalization;

namespace Ecr.Application.Permits.History;

/// <summary>One permit of a row/header history and the days it is in force for that row/header.</summary>
/// <param name="EntryId">Permit (registry entry).</param>
/// <param name="From">First day in force; <c>null</c> — the base permit (in force from the beginning).</param>
/// <param name="Until">Last day in force (inclusive); <c>null</c> — still in force (the latest permit).</param>
public sealed record PermitSpan(long EntryId, DateOnly? From, DateOnly? Until);

/// <summary>A run of days without a valid permit (inclusive).</summary>
/// <param name="First">First uncovered day.</param>
/// <param name="Last">Last uncovered day.</param>
public readonly record struct DayRange(DateOnly First, DateOnly Last)
{
    /// <summary>Days as <c>10.02–14.02.2026</c> (one day — <c>10.02.2026</c>); language-neutral, for stored parameters.</summary>
    public override string ToString()
        => First == Last
            ? First.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
            : First.ToString(First.Year == Last.Year ? "dd.MM" : "dd.MM.yyyy", CultureInfo.InvariantCulture) + "–"
              + Last.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
}

/// <summary>
/// Feature (j), human 08.11: «a permit changes mid-month — the user splits the month». The permit history of ONE row
/// (its permit cell) or ONE header field: the base permit is the cell/header value, each later permit is a segment
/// «from date» (<c>doc.PermitSegment</c>); a segment ends the day before the next one starts.
/// </summary>
/// <remarks>
/// ⚠ Pure: no clock, no store. Registry validity is <c>[ValidFrom, ValidTo)</c> — <c>ValidTo</c> exclusive, as in
/// <see cref="Validity.PermitMonthCoverage"/>.
/// </remarks>
public static class PermitTimeline
{
    /// <summary>The spans of a history: the base permit, then each segment in date order.</summary>
    /// <param name="baseEntryId">The permit cell/header value.</param>
    /// <param name="segments">Later permits: (from date, permit).</param>
    public static IReadOnlyList<PermitSpan> Spans(long baseEntryId, IEnumerable<(DateOnly From, long EntryId)> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var ordered = segments.OrderBy(s => s.From).ToList();
        var spans = new List<PermitSpan>(ordered.Count + 1);
        DateOnly? from = null;
        var entry = baseEntryId;
        foreach (var segment in ordered)
        {
            spans.Add(new PermitSpan(entry, from, segment.From.AddDays(-1)));
            from = segment.From;
            entry = segment.EntryId;
        }

        spans.Add(new PermitSpan(entry, from, null));
        return spans;
    }

    /// <summary>
    /// Months (first days) in which MORE than one permit is in force — the months the user must split (j2). A change on
    /// the 1st shares no month (human default 2: the previous permit ends on the last day of the previous month).
    /// </summary>
    /// <param name="spans">History spans (<see cref="Spans"/>).</param>
    public static IReadOnlyList<DateOnly> SharedMonths(IReadOnlyList<PermitSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        return [.. spans
            .Where(s => s.From is { Day: > 1 })
            .Select(s => new DateOnly(s.From!.Value.Year, s.From.Value.Month, 1))
            .Distinct()
            .Order()];
    }

    /// <summary>The permits in force on any day of <paramref name="month"/>, in date order (one = no split needed).</summary>
    /// <param name="spans">History spans.</param>
    /// <param name="month">Any day of the month.</param>
    public static IReadOnlyList<PermitSpan> InMonth(IReadOnlyList<PermitSpan> spans, DateOnly month)
    {
        ArgumentNullException.ThrowIfNull(spans);
        var first = new DateOnly(month.Year, month.Month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        return [.. spans.Where(s => (s.From is null || s.From <= last) && (s.Until is null || s.Until >= first))];
    }

    /// <summary>
    /// j4: days around a change on <paramref name="changeFrom"/> that no permit covers — after the previous permit's validity
    /// ends and before the change, and from the change until the new permit's validity starts (a new permit already
    /// expired on the change date: up to <paramref name="horizon"/>). Empty — no gap.
    /// </summary>
    /// <param name="previousValidTo">Previous permit: first invalid day (exclusive); <c>null</c> — open-ended.</param>
    /// <param name="changeFrom">The new permit is in force for the row/header from this day.</param>
    /// <param name="newValidFrom">New permit: first valid day; <c>null</c> — from the beginning.</param>
    /// <param name="newValidTo">New permit: first invalid day (exclusive); <c>null</c> — open-ended.</param>
    /// <param name="horizon">Last day that matters (end of the document's reporting window).</param>
    public static IReadOnlyList<DayRange> GapDays(
        DateOnly? previousValidTo, DateOnly changeFrom, DateOnly? newValidFrom, DateOnly? newValidTo, DateOnly horizon)
    {
        var gaps = new List<DayRange>();

        // Before the change: the previous permit stops being valid before the new one takes over.
        if (previousValidTo is { } prevEnd && prevEnd < changeFrom)
        {
            gaps.Add(new DayRange(prevEnd, changeFrom.AddDays(-1)));
        }

        // From the change: the new permit is not valid yet (or no more).
        var end = horizon < changeFrom ? changeFrom : horizon;
        if (newValidTo is { } newEnd && newEnd <= changeFrom)
        {
            gaps.Add(new DayRange(changeFrom, end));
        }
        else if (newValidFrom is { } newStart && newStart > changeFrom)
        {
            gaps.Add(new DayRange(changeFrom, Min(newStart.AddDays(-1), end)));
        }

        // ⚠ The new permit ending later in the window is not a gap BETWEEN permits: months with data after its end are the
        // validity rule (h), not j4.

        return Merge(gaps);
    }

    /// <summary>
    /// j4 fix «move the start date to …»: the change date that closes the first gap. The previous permit ended BEFORE the
    /// change — start the new permit on its first uncovered day (gap start); the new permit starts AFTER the change — move
    /// the change to the new permit's first valid day (the day after the gap).
    /// </summary>
    /// <param name="gaps">Gaps from <see cref="GapDays"/> (at least one).</param>
    /// <param name="changeFrom">The requested change date.</param>
    public static DateOnly SuggestedFrom(IReadOnlyList<DayRange> gaps, DateOnly changeFrom)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        var first = gaps[0];
        return first.First >= changeFrom ? first.Last.AddDays(1) : first.First;
    }

    /// <summary>
    /// Fresh eyes 09.11: how OUR refusals and audit labels name a row — never the technical key (a hex GUID for dynamic
    /// rows): the template row's label; otherwise «row N» (1-based position in the table) and the row's permit number.
    /// The client names rows the same way (<c>rowName.ts</c>).
    /// </summary>
    /// <param name="label">The template row's label; <c>null</c> — a dynamic row.</param>
    /// <param name="position">1-based position of the row in the table; 0 — unknown.</param>
    /// <param name="permit">The row's permit number; <c>null</c> — none.</param>
    public static string RowName(string? label, int position, string? permit)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            return label;
        }

        var row = position > 0 ? $"row {position.ToString(CultureInfo.InvariantCulture)}" : "row";
        return string.IsNullOrWhiteSpace(permit) ? row : $"{row} · {permit}";
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    private static List<DayRange> Merge(List<DayRange> ranges)
    {
        var merged = new List<DayRange>();
        foreach (var r in ranges.Where(r => r.First <= r.Last).OrderBy(r => r.First))
        {
            if (merged.Count > 0 && merged[^1].Last.AddDays(1) >= r.First)
            {
                merged[^1] = new DayRange(merged[^1].First, r.Last > merged[^1].Last ? r.Last : merged[^1].Last);
            }
            else
            {
                merged.Add(r);
            }
        }

        return merged;
    }
}
