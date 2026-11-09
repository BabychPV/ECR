// tests/Ecr.Application.Tests/Permits/PermitTimelineTests.cs
using Ecr.Application.Permits.History;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Permits;

/// <summary>
/// Feature (j): the permit history of a row/header — spans, the months to split, and the j4 gap between permits.
/// </summary>
public sealed class PermitTimelineTests
{
    private static DateOnly D(int month, int day) => new(2026, month, day);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "ACC-1108-j1")]
    public void A_change_on_the_15th_ends_the_previous_permit_on_the_14th_and_shares_that_month()
    {
        var spans = PermitTimeline.Spans(1, [(D(2, 15), 2)]);

        Assert.Equal([new PermitSpan(1, null, D(2, 14)), new PermitSpan(2, D(2, 15), null)], spans);
        Assert.Equal([D(2, 1)], PermitTimeline.SharedMonths(spans));
        Assert.Equal(2, PermitTimeline.InMonth(spans, D(2, 20)).Count);
        Assert.Single(PermitTimeline.InMonth(spans, D(3, 1)));
        Assert.Single(PermitTimeline.InMonth(spans, D(1, 31)));
    }

    /// <summary>Human default 2: a change on the 1st needs no split — the previous permit ends on the last day before.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "ACC-1108-j1")]
    public void A_change_on_the_1st_shares_no_month()
    {
        var spans = PermitTimeline.Spans(1, [(D(3, 1), 2)]);

        Assert.Empty(PermitTimeline.SharedMonths(spans));
        Assert.Equal(D(2, 28), spans[0].Until);
        Assert.Single(PermitTimeline.InMonth(spans, D(2, 10)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "ACC-1108-j1")]
    public void Three_permits_in_one_month_give_three_parts_in_date_order()
    {
        var spans = PermitTimeline.Spans(1, [(D(2, 20), 3), (D(2, 10), 2)]);

        var inFeb = PermitTimeline.InMonth(spans, D(2, 1));
        Assert.Equal([1L, 2L, 3L], inFeb.Select(s => s.EntryId));
        Assert.Equal([D(2, 1)], PermitTimeline.SharedMonths(spans));
    }

    /// <summary>j4 (mockup): A valid until 09.02 (ValidTo 10.02 exclusive), B from 15.02 → 10–14 Feb uncovered.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "ACC-1108-j4")]
    public void A_gap_between_the_previous_permit_end_and_the_change_is_named_in_days()
    {
        var gaps = PermitTimeline.GapDays(D(2, 10), D(2, 15), D(2, 15), null, D(12, 31));

        Assert.Equal([new DayRange(D(2, 10), D(2, 14))], gaps);
        Assert.Equal("10.02–14.02.2026", gaps[0].ToString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "ACC-1108-j4")]
    public void A_new_permit_valid_only_after_the_change_date_leaves_a_gap_until_it_starts()
    {
        var gaps = PermitTimeline.GapDays(null, D(2, 15), D(2, 20), null, D(12, 31));

        Assert.Equal([new DayRange(D(2, 15), D(2, 19))], gaps);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "ACC-1108-j4")]
    public void Adjacent_validities_leave_no_gap_and_a_later_expiry_is_not_a_gap()
    {
        // A valid until 14.02 (ValidTo 15.02), B from 15.02 to 30.06 (ValidTo 01.07): no gap between them.
        Assert.Empty(PermitTimeline.GapDays(D(2, 15), D(2, 15), D(2, 15), D(7, 1), D(12, 31)));

        // An overlap (A still valid after the change) is fine.
        Assert.Empty(PermitTimeline.GapDays(D(6, 1), D(2, 15), D(1, 1), null, D(12, 31)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "ACC-1108-j4")]
    public void A_new_permit_already_expired_on_the_change_date_is_uncovered_to_the_window_end()
    {
        var gaps = PermitTimeline.GapDays(null, D(5, 10), D(1, 1), D(5, 1), D(12, 31));

        Assert.Equal([new DayRange(D(5, 10), D(12, 31))], gaps);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "ACC-1108-j4")]
    public void Both_gaps_around_the_change_merge_into_one_run()
    {
        var gaps = PermitTimeline.GapDays(D(2, 10), D(2, 15), D(2, 18), null, D(12, 31));

        Assert.Equal([new DayRange(D(2, 10), D(2, 17))], gaps);
    }
}
