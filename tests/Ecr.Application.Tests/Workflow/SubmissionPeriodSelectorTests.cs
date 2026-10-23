// tests/Ecr.Application.Tests/Workflow/SubmissionPeriodSelectorTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Workflow;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// D-441 (AN-128): «Подати» подає найраніший неподаний період у стані <c>Open</c>/<c>Grace</c>, а не поточний;
/// колонка місяця k (<c>MonthsInColumns</c>) показує стан аркуша за період k.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні докази: (а) брати найпізніший неподаний замість найранішого — червоніє «у лютому з неподаним
/// січнем ціль — січень»; (б) пропустити фільтр стану аркуша — червоніє «поданий січень пропускається»;
/// (в) пропустити фільтр стану періоду — червоніє «Scheduled і Closed не пропонуються»; (г) прибрати перевірку
/// <c>PeriodKind.Monthly</c> — червоніє «квартальний календар статусів не має».
/// </remarks>
public sealed class SubmissionPeriodSelectorTests
{
    private const int Jan = 202601;
    private const int Feb = 202602;
    private const int Mar = 202603;

    private static SubmissionCalendar Calendar(
        params (int Key, PeriodState State, DocumentStatus Sheet)[] periods)
        => Calendar(PeriodKind.Monthly, ProjectStatus.Active, periods);

    private static SubmissionCalendar Calendar(
        PeriodKind kind, ProjectStatus status, params (int Key, PeriodState State, DocumentStatus Sheet)[] periods)
        => new(status, IsArchiving: false, kind, [.. periods.Select(p => new SubmissionCalendarPeriod(p.Key, p.State, p.Sheet))]);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-441")]
    public void У_лютому_з_неподаним_січнем_ціль_січень_наступний_лютий()
    {
        var choice = SubmissionPeriodSelector.Choose(Calendar(
            (Jan, PeriodState.Grace, DocumentStatus.Draft),
            (Feb, PeriodState.Open, DocumentStatus.Draft),
            (Mar, PeriodState.Scheduled, DocumentStatus.Draft)));

        Assert.Equal(Jan, choice.TargetPeriodKey);
        Assert.Equal(Feb, choice.NextPeriodKey);
        Assert.Equal([Jan, Feb], choice.Periods.Select(p => p.PeriodKey));
        Assert.All(choice.Periods, p => Assert.True(p.Submittable));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-441")]
    public void Поданий_січень_пропускається_ціль_лютий_без_наступного()
    {
        var choice = SubmissionPeriodSelector.Choose(Calendar(
            (Jan, PeriodState.Grace, DocumentStatus.Submitted),
            (Feb, PeriodState.Open, DocumentStatus.Draft)));

        Assert.Equal(Feb, choice.TargetPeriodKey);
        Assert.Null(choice.NextPeriodKey);

        var jan = Assert.Single(choice.Periods, p => p.PeriodKey == Jan);
        Assert.False(jan.Submittable);
        Assert.Equal(DocumentStatus.Submitted, jan.SheetStatus);
    }

    [Theory]
    [InlineData(DocumentStatus.Approved, false)]
    [InlineData(DocumentStatus.Submitted, false)]
    [InlineData(DocumentStatus.Rejected, true)]
    [InlineData(DocumentStatus.Draft, true)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-441")]
    public void Неподаний_це_Draft_або_Rejected(DocumentStatus status, bool target)
    {
        var choice = SubmissionPeriodSelector.Choose(Calendar((Jan, PeriodState.Open, status)));

        Assert.Equal(target ? Jan : null, choice.TargetPeriodKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-441")]
    public void Scheduled_і_Closed_не_пропонуються()
    {
        var choice = SubmissionPeriodSelector.Choose(Calendar(
            (Jan, PeriodState.Closed, DocumentStatus.Draft),
            (Feb, PeriodState.Scheduled, DocumentStatus.Draft)));

        Assert.Null(choice.TargetPeriodKey);
        Assert.Null(choice.NextPeriodKey);
        Assert.Empty(choice.Periods);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-441")]
    public void Архівний_проєкт_подати_нічого()
    {
        var choice = SubmissionPeriodSelector.Choose(Calendar(
            PeriodKind.Monthly, ProjectStatus.Archived, (Jan, PeriodState.Grace, DocumentStatus.Draft)));

        Assert.Null(choice.TargetPeriodKey);
        Assert.False(Assert.Single(choice.Periods).Submittable);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-441")]
    public void Колонка_місяця_k_показує_стан_аркуша_за_період_k()
    {
        var sheet = Sheet(TableLayoutKind.MonthsInColumns, tableId: 7);
        var calendar = Calendar(
            (Jan, PeriodState.Grace, DocumentStatus.Submitted),
            (Feb, PeriodState.Open, DocumentStatus.Approved),
            (Mar, PeriodState.Scheduled, DocumentStatus.Draft));

        var months = SubmissionPeriodSelector.MonthColumns(calendar, sheet, year: 2026, _ => true);

        Assert.Equal(
            [("JAN", (byte)1, Jan, DocumentStatus.Submitted), ("FEB", (byte)2, Feb, DocumentStatus.Approved), ("MAR", (byte)3, Mar, DocumentStatus.Draft)],
            months.Select(m => (m.ColumnCode, m.MonthNumber, m.PeriodKey, m.SheetStatus)));
        Assert.All(months, m => Assert.Equal(7, m.TableDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-441")]
    public void Рік_колонок_береться_з_відкритого_періоду_а_відсутній_період_пропускається()
    {
        var sheet = Sheet(TableLayoutKind.MonthsInColumns, tableId: 7);
        var calendar = Calendar((Jan, PeriodState.Grace, DocumentStatus.Submitted));

        Assert.Empty(SubmissionPeriodSelector.MonthColumns(calendar, sheet, year: 2025, _ => true));

        var month = Assert.Single(SubmissionPeriodSelector.MonthColumns(calendar, sheet, year: 2026, _ => true));
        Assert.Equal(Jan, month.PeriodKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-441")]
    public void Квартальний_календар_статусів_місяців_не_має()
    {
        var sheet = Sheet(TableLayoutKind.MonthsInColumns, tableId: 7);
        var calendar = Calendar(PeriodKind.Quarterly, ProjectStatus.Active, (Jan, PeriodState.Open, DocumentStatus.Submitted));

        Assert.Empty(SubmissionPeriodSelector.MonthColumns(calendar, sheet, year: 2026, _ => true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-441")]
    public void Таблиця_не_MonthsInColumns_і_схована_таблиця_статусів_не_дають()
    {
        var calendar = Calendar((Jan, PeriodState.Grace, DocumentStatus.Submitted));

        Assert.Empty(SubmissionPeriodSelector.MonthColumns(
            calendar, Sheet(TableLayoutKind.PerPeriodInstance, tableId: 7), year: 2026, _ => true));
        Assert.Empty(SubmissionPeriodSelector.MonthColumns(
            calendar, Sheet(TableLayoutKind.MonthsInColumns, tableId: 7), year: 2026, id => id != 7));
    }

    /// <summary>Аркуш з однією таблицею: колонки JAN/FEB/MAR (місяці 1–3) і немісячна TOTAL.</summary>
    private static SheetDef Sheet(TableLayoutKind layout, int tableId)
    {
        var sheet = new SheetDef(1, EcrCode.Create("S07"), Name("Water"), 1);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(sheet, 3);

        var table = new TableDef(3, EcrCode.Create("T7_00"), Name("Water consolidation"), 1, layout, TableRowMode.Fixed);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, tableId);

        byte month = 0;
        foreach (var code in new[] { "JAN", "FEB", "MAR" })
        {
            var column = new ColumnDef(tableId, EcrCode.Create(code), Name(code), ++month, CellDataType.Decimal);
            column.SetMonth(true, month);
            table.AddColumn(column);
        }

        table.AddColumn(new ColumnDef(tableId, EcrCode.Create("TOTAL"), Name("Total"), 13, CellDataType.Decimal));
        sheet.AddTable(table);

        return sheet;
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
