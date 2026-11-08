using Ecr.Application.Permits.Validity;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Permits;

/// <summary>
/// Rule (h), human/coordinator 08.11: day-overlap coverage, what counts as data, partial-month days and how uncovered
/// months are named.
/// </summary>
public sealed class PermitMonthCoverageTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h")]
    public void A_permit_from_the_15th_covers_its_month_and_misses_the_months_before()
    {
        var uncovered = PermitMonthCoverage.Uncovered(D(2026, 2, 15), D(2027, 1, 1), [D(2026, 1, 1), D(2026, 2, 1), D(2026, 3, 1)]);

        Assert.Equal([D(2026, 1, 1)], uncovered);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h")]
    public void Partial_days_are_named_only_when_the_month_is_partly_covered()
    {
        Assert.Equal((D(2026, 2, 15), D(2026, 2, 28)), PermitMonthCoverage.PartialDays(D(2026, 2, 15), null, D(2026, 2, 1)));
        Assert.Equal((D(2026, 6, 1), D(2026, 6, 14)), PermitMonthCoverage.PartialDays(null, D(2026, 6, 15), D(2026, 6, 1)));
        Assert.Null(PermitMonthCoverage.PartialDays(D(2026, 1, 1), D(2027, 1, 1), D(2026, 2, 1)));
        Assert.Null(PermitMonthCoverage.PartialDays(D(2026, 3, 1), null, D(2026, 2, 1)));
        Assert.Equal("15.02–28.02.2026", PermitMonthCoverage.DescribeDays((D(2026, 2, 15), D(2026, 2, 28))));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h")]
    public void A_lone_month_is_named_and_a_run_of_months_is_a_date_range()
    {
        Assert.Equal("Jan 2026", PermitMonthCoverage.DescribeMonths([D(2026, 1, 1)], "en"));
        Assert.Equal("01.01–28.02.2026", PermitMonthCoverage.DescribeMonths([D(2026, 1, 1), D(2026, 2, 1)], "en"));
        Assert.Equal("янв 2026, 01.03–30.04.2026", PermitMonthCoverage.DescribeMonths([D(2026, 1, 1), D(2026, 3, 1), D(2026, 4, 1)], "ru"));
        Assert.Equal("01.2026, 01.03–30.04.2026", PermitMonthCoverage.DescribeMonthsNumeric([D(2026, 1, 1), D(2026, 3, 1), D(2026, 4, 1)]));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h")]
    public void Only_entered_or_imported_values_are_data()
    {
        Assert.True(PermitMonthCoverage.HasData(new CellValueData { ValueNumeric = 0m }));
        Assert.False(PermitMonthCoverage.HasData(new CellValueData { ValueNumeric = 25m, IsCalculated = true }));
        Assert.False(PermitMonthCoverage.HasData(CellValueData.Empty));
        Assert.False(PermitMonthCoverage.HasData(null));
    }
}
