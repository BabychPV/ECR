// tests/Ecr.Application.Tests/Permits/PermitTimelineTests.cs
using Ecr.Application.Permits.History;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Permits;

/// <summary>
/// Feature (j): the permit history of a row/header — spans, the months to split, and the j4 gap between permits.
/// </summary>
public sealed class PermitTimelineTests
{
    private static DateOnly D(int month, int day) => new(2026, month, day);

    /// <summary>Q2 2026 у проєкті з кварталами: ключ 202602 — другий КВАРТАЛ, а не лютий.</summary>
    private static PermitSplitUnit Q2() => PermitSplitUnit.OfPeriod(new PeriodKey(202602), new PeriodBounds(D(4, 1), D(6, 30)));

    private static IEnumerable<IReadOnlyList<PermitSpan>> Histories() =>
    [
        PermitTimeline.Spans(1, []),
        PermitTimeline.Spans(1, [(D(2, 15), 2)]),
        PermitTimeline.Spans(1, [(D(2, 1), 2)]),
        PermitTimeline.Spans(1, [(D(2, 15), 2), (D(2, 20), 3), (D(4, 10), 4)]),
        PermitTimeline.Spans(1, [(D(1, 31), 2), (D(3, 1), 3), (D(3, 31), 4)]),
    ];

    /// <summary>C-10: для календарного місяця нова одиниця дає те саме, що колишні <c>InMonth</c> і <c>SharedMonths</c>.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "C-10")]
    public void Одиниця_місяць_дорівнює_InMonth_і_SharedMonths()
    {
        foreach (var spans in Histories())
        {
            for (var m = 1; m <= 6; m++)
            {
                var unit = PermitSplitUnit.Month(D(m, 17));
                Assert.Equal(D(m, 1), unit.First);
                Assert.Equal(new DateOnly(2026, m, 1).AddMonths(1).AddDays(-1), unit.Last);
                Assert.Equal(202600 + m, unit.PartKey);
                Assert.Equal(PermitTimeline.InMonth(spans, D(m, 1)), PermitTimeline.InUnit(spans, unit));
                Assert.Equal(PermitTimeline.SharedMonths(spans).Contains(D(m, 1)), PermitTimeline.IsShared(spans, unit));
            }
        }
    }

    /// <summary>C-10: квартал ділиться зміною ВСЕРЕДИНІ, не ділиться зміною поза ним чи з його першого дня.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "C-10")]
    public void Квартал_ділиться_зміною_всередині_і_не_ділиться_зміною_поза()
    {
        var q2 = Q2();
        Assert.Equal((D(4, 1), D(6, 30), 202602), (q2.First, q2.Last, q2.PartKey));
        Assert.False(q2.IsSingleMonth);
        Assert.Equal("04.2026–06.2026", q2.Label);

        var inside = PermitTimeline.Spans(1, [(D(5, 15), 2)]);
        Assert.True(PermitTimeline.IsShared(inside, q2));
        Assert.Equal(2, PermitTimeline.InUnit(inside, q2).Count);

        // Зміна 15.02 — «лютий» за Sequence, але Q2 вона не стосується: одна чинна історія.
        var outside = PermitTimeline.Spans(1, [(D(2, 15), 2)]);
        Assert.False(PermitTimeline.IsShared(outside, q2));
        Assert.Single(PermitTimeline.InUnit(outside, q2));

        var firstDay = PermitTimeline.Spans(1, [(D(4, 1), 2)]);
        Assert.False(PermitTimeline.IsShared(firstDay, q2));
    }

    /// <summary>C-10: місячний період (також з обрізаним початком проєкту) дає ту саму одиницю, що й місяць.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "C-10")]
    public void Місячний_період_дає_ту_саму_одиницю_що_місяць()
    {
        var february = PermitSplitUnit.OfPeriod(new PeriodKey(202602), new PeriodBounds(D(2, 1), D(2, 28)));
        Assert.Equal(PermitSplitUnit.Month(D(2, 1)), february);
        Assert.True(february.IsSingleMonth);
        Assert.Equal("02.2026", february.Label);

        var truncated = PermitSplitUnit.OfPeriod(new PeriodKey(202601), new PeriodBounds(D(1, 15), D(1, 31)));
        Assert.Equal(PermitSplitUnit.Month(D(1, 1)), truncated);
    }

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

    /// <summary>
    /// j4 «move the start date to …» (CI 42610809703 e2e: change 01.10, new permit from 15.10 — the suggestion was 01.10,
    /// i.e. the same refused date). Previous ended early → its first uncovered day; new permit starts late → its first day.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "ACC-1108-j4")]
    public void The_suggested_start_date_closes_the_gap_on_the_side_that_causes_it()
    {
        var late = PermitTimeline.GapDays(null, D(10, 1), D(10, 15), null, D(12, 31));
        Assert.Equal(D(10, 15), PermitTimeline.SuggestedFrom(late, D(10, 1), D(10, 15), null, D(12, 31)));

        var early = PermitTimeline.GapDays(D(2, 10), D(2, 15), D(1, 1), null, D(12, 31));
        Assert.Equal(D(2, 10), PermitTimeline.SuggestedFrom(early, D(2, 15), D(1, 1), null, D(12, 31)));
    }

    /// <summary>
    /// F13 p220 j4: the new permit expired 09.04, added from 15.10 — the suggestion was «01.01.2027» (after the window, and
    /// still refused). No start date can close such a gap, nor one where the new permit is not valid on the previous
    /// permit's first uncovered day: no suggestion (only «add a permit for those days»).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "F13-p220-j4")]
    public void No_start_date_is_suggested_when_none_would_close_the_gap()
    {
        var expired = PermitTimeline.GapDays(null, D(10, 15), D(1, 1), D(4, 10), D(12, 31));
        Assert.Null(PermitTimeline.SuggestedFrom(expired, D(10, 15), D(1, 1), D(4, 10), D(12, 31)));

        var bothSides = PermitTimeline.GapDays(D(2, 10), D(2, 15), D(2, 15), null, D(12, 31));
        Assert.Null(PermitTimeline.SuggestedFrom(bothSides, D(2, 15), D(2, 15), null, D(12, 31)));

        var afterWindow = PermitTimeline.GapDays(null, D(10, 1), new DateOnly(2027, 1, 5), null, D(12, 31));
        Assert.Null(PermitTimeline.SuggestedFrom(afterWindow, D(10, 1), new DateOnly(2027, 1, 5), null, D(12, 31)));
    }
}
