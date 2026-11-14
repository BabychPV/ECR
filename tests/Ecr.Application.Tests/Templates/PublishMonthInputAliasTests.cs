// tests/Ecr.Application.Tests/Templates/PublishMonthInputAliasTests.cs
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// ✎ LAND-YEAR L0a: псевдоніми місячних входів методологій (<c>cfg.MonthInputAlias</c>) публікуються, лише коли ціль —
/// місячна колонка ТІЄЇ САМОЇ таблиці з тим самим місяцем, ім'я не є кодом живої колонки, версія «рік, місяці
/// колонками», таблиця «місяці в колонках» (<see cref="PublishChecks.CheckMonthInputAliases"/>).
/// </summary>
public sealed class PublishMonthInputAliasTests
{
    private const int TableId = 10;
    private const int OtherTableId = 11;
    private const int TotalId = 101;
    private const int JanId = 102;
    private const int FebId = 103;
    private const int OtherJanId = 111;

    [Fact]
    [Trait("Requirement", "D-474")]
    public void Коректні_псевдоніми_публікуються()
    {
        var version = Version(TemplateLayout.YearWithMonthColumns, TableLayoutKind.MonthsInColumns);

        Assert.Empty(PublishChecks.CheckMonthInputAliases(
            version, [Alias(TableId, "Gas", 1, JanId), Alias(TableId, "Gas", 2, FebId), Alias(TableId, "Oil", 2, FebId)]));
    }

    [Fact]
    [Trait("Requirement", "D-474")]
    public void Ціль_іншого_місяця_відхиляється()
    {
        var version = Version(TemplateLayout.YearWithMonthColumns, TableLayoutKind.MonthsInColumns);

        var diagnostic = Assert.Single(PublishChecks.CheckMonthInputAliases(
            version, [Alias(TableId, "Gas", 1, JanId), Alias(TableId, "Gas", 2, JanId)]));

        Assert.Equal("ECR-TMPL-0422", diagnostic.Code);
        Assert.Equal("err.ECR-TMPL-0422.monthInputAliasInvalid", diagnostic.MessageKey);
        Assert.Equal("T1", diagnostic.MessageParams!["tableCode"]);
        Assert.Equal("1", diagnostic.MessageParams["count"]);
        Assert.Equal("Gas@M02", diagnostic.MessageParams["aliases"]);
    }

    [Fact]
    [Trait("Requirement", "D-474")]
    public void Ціль_у_чужій_таблиці_відхиляється()
    {
        var version = Version(TemplateLayout.YearWithMonthColumns, TableLayoutKind.MonthsInColumns);

        var diagnostic = Assert.Single(PublishChecks.CheckMonthInputAliases(version, [Alias(TableId, "Gas", 1, OtherJanId)]));

        Assert.Equal("Gas@M01", diagnostic.MessageParams!["aliases"]);
    }

    [Fact]
    [Trait("Requirement", "D-474")]
    public void Ціль_немісячна_колонка_відхиляється()
    {
        var version = Version(TemplateLayout.YearWithMonthColumns, TableLayoutKind.MonthsInColumns);

        Assert.Single(PublishChecks.CheckMonthInputAliases(version, [Alias(TableId, "Gas", 1, TotalId)]));
    }

    [Fact]
    [Trait("Requirement", "D-474")]
    public void Псевдонім_що_збігається_з_кодом_живої_колонки_відхиляється()
    {
        var version = Version(TemplateLayout.YearWithMonthColumns, TableLayoutKind.MonthsInColumns);

        var diagnostic = Assert.Single(PublishChecks.CheckMonthInputAliases(version, [Alias(TableId, "total", 1, JanId)]));

        Assert.Equal("total@M01", diagnostic.MessageParams!["aliases"]);
    }

    [Fact]
    [Trait("Requirement", "D-474")]
    public void Розкладка_копія_на_місяць_відхиляє_псевдоніми()
    {
        var version = Version(TemplateLayout.PerMonthCopies, TableLayoutKind.MonthsInColumns);

        Assert.Single(PublishChecks.CheckMonthInputAliases(version, [Alias(TableId, "Gas", 1, JanId)]));
    }

    [Fact]
    [Trait("Requirement", "D-474")]
    public void Таблиця_не_місяці_в_колонках_відхиляє_псевдоніми()
    {
        var version = Version(TemplateLayout.YearWithMonthColumns, TableLayoutKind.PerPeriodInstance);

        Assert.Single(PublishChecks.CheckMonthInputAliases(version, [Alias(TableId, "Gas", 1, JanId)]));
    }

    [Fact]
    [Trait("Requirement", "D-474")]
    public void Без_псевдонімів_перевірка_нічого_не_каже()
        => Assert.Empty(PublishChecks.CheckMonthInputAliases(
            Version(TemplateLayout.PerMonthCopies, TableLayoutKind.PerPeriodInstance), []));

    private static MonthInputAlias Alias(int tableId, string alias, byte month, int columnId)
        => new(tableId, alias, month, columnId);

    /// <summary>Таблиця T1: немісячна <c>Total</c>, місячні <c>Jan</c> (1) і <c>Feb</c> (2); таблиця T2: <c>Jan</c> (1).</summary>
    private static TemplateVersion Version(TemplateLayout layout, TableLayoutKind tableLayout)
    {
        var version = new TemplateVersion(1, "1.0.0.0", 1, DateTime.UtcNow);
        SetId(version, 7);
        version.SetLayout(layout);
        var sheet = new SheetDef(7, EcrCode.Create("S1"), Text("S1"), 1);
        SetId(sheet, 1);

        var table = new TableDef(1, EcrCode.Create("T1"), Text("T1"), 1, tableLayout, TableRowMode.Dynamic);
        SetId(table, TableId);
        table.AddColumn(Column(table.Id, TotalId, "Total", 1, null));
        table.AddColumn(Column(table.Id, JanId, "Jan", 2, 1));
        table.AddColumn(Column(table.Id, FebId, "Feb", 3, 2));
        sheet.AddTable(table);

        var other = new TableDef(1, EcrCode.Create("T2"), Text("T2"), 2, TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        SetId(other, OtherTableId);
        other.AddColumn(Column(other.Id, OtherJanId, "Jan", 1, 1));
        sheet.AddTable(other);

        version.AddSheet(sheet);
        return version;
    }

    private static ColumnDef Column(int tableId, int id, string code, int ordinal, byte? month)
    {
        var column = new ColumnDef(tableId, EcrCode.Create(code), Text(code), ordinal, CellDataType.Decimal);
        SetId(column, id);
        if (month is { } m)
        {
            column.SetMonth(true, m);
        }

        return column;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
