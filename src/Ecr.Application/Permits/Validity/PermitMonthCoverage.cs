// src/Ecr.Application/Permits/Validity/PermitMonthCoverage.cs
using System.Globalization;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits.Validity;

/// <summary>
/// Rule (h), human 08.11: a row bound to a permit may hold data only in months the permit covers.
/// One rule for the write path (PATCH, header), the Issues check and the nightly orphan scan.
/// </summary>
/// <remarks>
/// <para>
/// <b>Months with data.</b> A table with month columns (<see cref="ColumnDef.IsMonthColumn"/> + <see cref="ColumnDef.MonthNumber"/>):
/// month <c>m</c> of the year of the row's period has data when the month-<c>m</c> column holds a value. A table without
/// month columns: the months of the row's own period.
/// </para>
/// <para>
/// <b>Covered.</b> The permit window <c>[ValidFrom, ValidTo)</c> overlaps the calendar month by at least one day: a permit
/// valid from 15 March covers March (the same segment principle as the header window check, FV-5.20).
/// </para>
/// <para>
/// ⚠ A computed zero (<see cref="CellValueData.IsCalculated"/> with value 0) is not data: a sum row over empty children
/// shows 0 in every month, and treating that as data would refuse every partial-year permit.
/// </para>
/// </remarks>
public static class PermitMonthCoverage
{
    private static readonly string[] MonthNamesEn =
        ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    private static readonly string[] MonthNamesRu =
        ["янв", "фев", "мар", "апр", "май", "июн", "июл", "авг", "сен", "окт", "ноя", "дек"];

    private static readonly string[] MonthNamesKz =
        ["қаң", "ақп", "нау", "сәу", "мам", "мау", "шіл", "там", "қыр", "қаз", "қар", "жел"];

    /// <summary>Does the cell hold data for the rule.</summary>
    /// <param name="value">Stored or written value; <c>null</c> = no cell.</param>
    public static bool HasData(CellValueData? value)
    {
        if (value is null || value.IsEmpty)
        {
            return false;
        }

        if (value.IsCalculated && value.ValueNumeric is 0m)
        {
            return false;
        }

        return value.ValueNumeric is not null
               || !string.IsNullOrWhiteSpace(value.ValueString)
               || value.ValueDate is not null
               || value.ValueBool is not null
               || value.ValueRegistryEntryId is not null
               || value.ValueUnitId is not null;
    }

    /// <summary>Live month columns of the table (month 1..12).</summary>
    /// <param name="table">Table.</param>
    public static IReadOnlyList<ColumnDef> MonthColumns(TableDef table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return [.. table.Columns.Where(c => !c.IsDeleted && c.IsMonthColumn && c.MonthNumber is >= 1 and <= 12)];
    }

    /// <summary>First day of month <paramref name="month"/> of <paramref name="year"/>.</summary>
    /// <param name="year">Year.</param>
    /// <param name="month">Month 1..12.</param>
    public static DateOnly Month(int year, int month) => new(year, month, 1);

    /// <summary>The months (first days) a period spans: <c>[start, end]</c> inclusive.</summary>
    /// <param name="periodStart">First day of the period.</param>
    /// <param name="periodEnd">Last day of the period.</param>
    public static IReadOnlyList<DateOnly> MonthsOfPeriod(DateOnly periodStart, DateOnly periodEnd)
    {
        var months = new List<DateOnly>();
        for (var m = new DateOnly(periodStart.Year, periodStart.Month, 1); m <= periodEnd; m = m.AddMonths(1))
        {
            months.Add(m);
        }

        return months;
    }

    /// <summary>Months (first days) the permit window does not touch, in calendar order.</summary>
    /// <param name="validFrom">First valid day; <c>null</c> = from the beginning.</param>
    /// <param name="validTo">First invalid day (exclusive); <c>null</c> = open-ended.</param>
    /// <param name="monthsWithData">Months with data.</param>
    public static IReadOnlyList<DateOnly> Uncovered(DateOnly? validFrom, DateOnly? validTo, IEnumerable<DateOnly> monthsWithData)
    {
        ArgumentNullException.ThrowIfNull(monthsWithData);
        var window = new ValidityWindow(validFrom, validTo);
        return [.. monthsWithData
            .Select(m => new DateOnly(m.Year, m.Month, 1))
            .Distinct()
            .Where(m => !window.OverlapsSegment(m, m.AddMonths(1)))
            .Order()];
    }

    /// <summary>
    /// Months with data of one row: month columns with a value (month table), or the row's period months when any
    /// non-permit cell holds data (table without month columns).
    /// </summary>
    /// <param name="monthOf">Month column id → month number; empty = table without month columns.</param>
    /// <param name="year">Year of the row's period.</param>
    /// <param name="cells">The row's cells: column id → value.</param>
    /// <param name="permitColumns">Permit-type columns (not data).</param>
    /// <param name="periodMonths">Months of the row's period.</param>
    public static IReadOnlyList<DateOnly> RowMonthsWithData(
        IReadOnlyDictionary<int, int> monthOf,
        int year,
        IReadOnlyDictionary<int, CellValueData> cells,
        IReadOnlySet<int> permitColumns,
        IReadOnlyList<DateOnly> periodMonths)
    {
        ArgumentNullException.ThrowIfNull(monthOf);
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(permitColumns);

        if (monthOf.Count > 0)
        {
            return [.. cells
                .Where(p => monthOf.ContainsKey(p.Key) && HasData(p.Value))
                .Select(p => Month(year, monthOf[p.Key]))
                .Distinct()
                .Order()];
        }

        return cells.Any(p => !permitColumns.Contains(p.Key) && HasData(p.Value)) ? periodMonths : [];
    }

    /// <summary>Months as <c>01.2026, 02.2026</c> — language-neutral, for stored Issues parameters.</summary>
    /// <param name="months">Months.</param>
    public static string DescribeMonthsNumeric(IReadOnlyList<DateOnly> months)
    {
        ArgumentNullException.ThrowIfNull(months);
        return string.Join(", ", months.Select(m => m.ToString("MM.yyyy", CultureInfo.InvariantCulture)));
    }

    /// <summary>Validity for the message: <c>01.03.2026 – 30.11.2026</c>, open ends as <c>…</c>.</summary>
    /// <param name="validFrom">First valid day.</param>
    /// <param name="validTo">First invalid day (exclusive).</param>
    public static string DescribeValidity(DateOnly? validFrom, DateOnly? validTo)
    {
        var from = validFrom?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "…";
        var to = validTo?.AddDays(-1).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "…";
        return $"{from} – {to}";
    }

    /// <summary>
    /// Months for the message in the reader's language (<c>Jan 2026, Feb 2026</c>); at most
    /// <paramref name="limit"/> listed, the rest as <c>+N</c>.
    /// </summary>
    /// <param name="months">Uncovered months.</param>
    /// <param name="language"><c>en</c>/<c>ru</c>/<c>kz</c>.</param>
    /// <param name="limit">How many months to name.</param>
    public static string DescribeMonths(IReadOnlyList<DateOnly> months, string? language, int limit = 12)
    {
        ArgumentNullException.ThrowIfNull(months);

        // ⚠ Own month names, not CultureInfo: the server may run with invariant globalization, where ru-RU/kk-KZ
        // either throw or fall back to English silently.
        var names = language switch
        {
            "ru" => MonthNamesRu,
            "kz" => MonthNamesKz,
            _ => MonthNamesEn,
        };

        var named = months.Take(limit).Select(m => names[m.Month - 1] + " " + m.Year.ToString(CultureInfo.InvariantCulture));
        var text = string.Join(", ", named);
        return months.Count > limit
            ? text + " +" + (months.Count - limit).ToString(CultureInfo.InvariantCulture)
            : text;
    }
}
