using Ecr.Application.Permits.Validity;
using Ecr.Application.Validation;
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

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h4")]
    public void A_calculated_value_is_calculated_data_and_a_calculated_zero_is_not()
    {
        Assert.True(PermitMonthCoverage.HasCalculatedData(new CellValueData { ValueNumeric = 25m, IsCalculated = true }));
        Assert.False(PermitMonthCoverage.HasCalculatedData(new CellValueData { ValueNumeric = 0m, IsCalculated = true }));
        Assert.False(PermitMonthCoverage.HasCalculatedData(new CellValueData { ValueNumeric = 25m }));
        Assert.False(PermitMonthCoverage.HasCalculatedData(null));
    }

    /// <summary>
    /// Human 08.11 («Попередження», F13-p210): a month with only calculated values is named apart (Warning); a month that
    /// also holds an entered value stays a month with data (blocking) and is not repeated as calculated.
    /// </summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h4")]
    public void Calculated_only_months_are_separate_from_months_with_entered_data()
    {
        // Columns 1..4 = Jan..Apr; column 5 is a second April column.
        var monthOf = new Dictionary<int, int> { [1] = 1, [2] = 2, [3] = 3, [4] = 4, [5] = 4 };
        var cells = new Dictionary<int, CellValueData>
        {
            [1] = new() { ValueNumeric = 10m, IsCalculated = true },  // Jan: calculated only
            [2] = new() { ValueNumeric = 0m, IsCalculated = true },   // Feb: calculated zero — nothing
            [3] = new() { ValueNumeric = 7m },                         // Mar: entered
            [4] = new() { ValueNumeric = 3m, IsCalculated = true },   // Apr: calculated…
            [5] = new() { ValueNumeric = 4m },                         // …and entered — blocking
        };
        var none = new HashSet<int>();

        Assert.Equal([D(2026, 3, 1), D(2026, 4, 1)], PermitMonthCoverage.RowMonthsWithData(monthOf, 2026, cells, none, []));
        Assert.Equal([D(2026, 1, 1)], PermitMonthCoverage.RowCalculatedOnlyMonths(monthOf, 2026, cells, none, []));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h4")]
    public void Without_month_columns_the_period_is_calculated_only_unless_an_entered_value_is_there()
    {
        var noMonths = new Dictionary<int, int>();
        var permit = new HashSet<int> { 9 };
        IReadOnlyList<DateOnly> period = [D(2026, 1, 1)];
        var calculated = new Dictionary<int, CellValueData>
        {
            [1] = new() { ValueNumeric = 10m, IsCalculated = true },
            [9] = new() { ValueRegistryEntryId = 5 },
        };
        var mixed = new Dictionary<int, CellValueData>(calculated) { [2] = new() { ValueNumeric = 1m } };

        Assert.Equal(period, PermitMonthCoverage.RowCalculatedOnlyMonths(noMonths, 2026, calculated, permit, period));
        Assert.Empty(PermitMonthCoverage.RowMonthsWithData(noMonths, 2026, calculated, permit, period));
        Assert.Empty(PermitMonthCoverage.RowCalculatedOnlyMonths(noMonths, 2026, mixed, permit, period));
        Assert.Equal(period, PermitMonthCoverage.RowMonthsWithData(noMonths, 2026, mixed, permit, period));
    }

    /// <summary>
    /// The calculated-months Warning names the fix through the permit only (calculated values cannot be cleared), and one
    /// month reads «that month» (P3, 08.11).
    /// </summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h4")]
    public void The_calculated_months_warning_offers_only_the_permit_fix_and_reads_singular_for_one_month()
    {
        var p = new Dictionary<string, string>
        {
            ["permit"] = "KZ-1", ["column"] = "Permit", ["validity"] = "01.03.2026 – 30.11.2026", ["months"] = "01.2026",
        };

        foreach (var lang in new[] { "en", "ru", "kz" })
        {
            foreach (var key in new[] { ValidationMessageTemplates.PermitCalculatedNotCovering, ValidationMessageTemplates.PermitCalculatedNotCoveringOne })
            {
                var text = ValidationMessageTemplates.Render(key, lang, p);
                Assert.Contains("KZ-1", text, StringComparison.Ordinal);
                Assert.Contains("01.03.2026 – 30.11.2026", text, StringComparison.Ordinal);
                Assert.Contains("01.2026", text, StringComparison.Ordinal);
            }
        }

        var one = ValidationMessageTemplates.Render(ValidationMessageTemplates.PermitCalculatedNotCoveringOne, "en", p);
        Assert.Contains("valid in that month", one, StringComparison.Ordinal);
        Assert.DoesNotContain("clear", one, StringComparison.OrdinalIgnoreCase);
        var many = ValidationMessageTemplates.Render(ValidationMessageTemplates.PermitCalculatedNotCovering, "en", p);
        Assert.Contains("valid in those months", many, StringComparison.Ordinal);
        Assert.DoesNotContain("clear", many, StringComparison.OrdinalIgnoreCase);
    }
}
