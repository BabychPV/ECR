// tests/Ecr.Application.Tests/Templates/PublishYearLayoutPeriodDependenceTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// D-474: розкладка «рік, місяці колонками» не публікується, коли формула НЕмісячної колонки залежить від місяця
/// (<c>[Period]</c>, крім <c>Year</c>) або методологію прив'язано до немісячної колонки
/// (<see cref="PublishChecks.CheckYearLayoutPeriodDependence"/>). Розкладка «копія на місяць», місячні колонки
/// й таблиці «екземпляр на період» (HSE401) не зачіпаються.
/// </summary>
public sealed class PublishYearLayoutPeriodDependenceTests
{
    private const int PlainColumnId = 101;
    private const int MonthColumnId = 102;

    private static readonly IFormulaEngine Engine = new RealFormulaEngine();

    [Fact]
    [Trait("Requirement", "D-474")]
    public void Формула_немісячної_колонки_з_днями_періоду_блокує_річну_розкладку()
    {
        var version = Version(TemplateLayout.YearWithMonthColumns, TableLayoutKind.MonthsInColumns, (PlainColumnId, "[Jan] * [Period].Days"));

        var diagnostic = Assert.Single(PublishChecks.CheckYearLayoutPeriodDependence(version, Engine, []));

        Assert.Equal("ECR-TMPL-0422", diagnostic.Code);
        Assert.Equal("err.ECR-TMPL-0422.yearLayoutPeriodFormula", diagnostic.MessageKey);
        Assert.Contains("T1", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Total", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "D-474")]
    public void Залежність_від_місяця_всередині_функції_й_умови_теж_знаходиться()
    {
        var version = Version(
            TemplateLayout.YearWithMonthColumns,
            TableLayoutKind.MonthsInColumns,
            (PlainColumnId, "IF([Jan] > 0, ROUND([Jan] / [Period].Hours, 2), 0)"));

        Assert.Single(PublishChecks.CheckYearLayoutPeriodDependence(version, Engine, []));
    }

    [Fact]
    [Trait("Requirement", "D-474")]
    public void Рік_періоду_в_немісячній_колонці_дозволений()
    {
        var version = Version(TemplateLayout.YearWithMonthColumns, TableLayoutKind.MonthsInColumns, (PlainColumnId, "[Jan] + [Period].Year"));

        Assert.Empty(PublishChecks.CheckYearLayoutPeriodDependence(version, Engine, []));
    }

    [Fact]
    [Trait("Requirement", "D-474")]
    public void Формула_місячної_колонки_з_днями_періоду_дозволена()
    {
        var version = Version(TemplateLayout.YearWithMonthColumns, TableLayoutKind.MonthsInColumns, (MonthColumnId, "[Total] * [Period].Days"));

        Assert.Empty(PublishChecks.CheckYearLayoutPeriodDependence(version, Engine, []));
    }

    [Fact]
    [Trait("Requirement", "D-474")]
    public void Розкладка_копія_на_місяць_не_перевіряється()
    {
        var version = Version(TemplateLayout.PerMonthCopies, TableLayoutKind.MonthsInColumns, (PlainColumnId, "[Jan] * [Period].Days"));

        Assert.Empty(PublishChecks.CheckYearLayoutPeriodDependence(version, Engine, [Binding(PlainColumnId)]));
    }

    [Fact]
    [Trait("Requirement", "D-474")]
    [Trait("Finding", "b14-document-move-N1")]
    public void Таблиця_екземпляр_на_період_у_річній_розкладці_теж_перевіряється()
    {
        // ✎ b14 N1: у річній розкладці таблиця «екземпляр на період» — ОДНА копія на рік (якір); [Period].Days там —
        // 365 у річному проєкті й 31 у січні місячного (HSE401 після перенесення в LANDUI2026 множив би на ~11.8).
        var version = Version(TemplateLayout.YearWithMonthColumns, TableLayoutKind.PerPeriodInstance, (PlainColumnId, "[Jan] * [Period].Days"));

        var diagnostics = PublishChecks.CheckYearLayoutPeriodDependence(version, Engine, [Binding(PlainColumnId)]);

        Assert.Contains(diagnostics, d => d.MessageKey == "err.ECR-TMPL-0422.yearLayoutPeriodFormula");
        Assert.Contains(diagnostics, d => d.MessageKey == "err.ECR-TMPL-0422.yearLayoutNonMonthBinding");
    }

    [Fact]
    [Trait("Finding", "b14-document-move-N1")]
    public void Довжина_року_через_рік_періоду_в_таблиці_екземпляр_на_період_дозволена()
    {
        // HSE401 v14: [Period].Days → довжина року з [Period].Year — однакова в річному й місячному проєкті.
        var version = Version(
            TemplateLayout.YearWithMonthColumns,
            TableLayoutKind.PerPeriodInstance,
            (PlainColumnId, "[Jan] / IF([Period].Year % 4 = 0, 366, 365)"));

        Assert.Empty(PublishChecks.CheckYearLayoutPeriodDependence(version, Engine, []));
    }

    [Fact]
    [Trait("Finding", "b14-document-move-N1")]
    public void Статична_таблиця_річної_розкладки_не_перевіряється()
    {
        var version = Version(TemplateLayout.YearWithMonthColumns, TableLayoutKind.Static, (PlainColumnId, "[Jan] * [Period].Days"));

        Assert.Empty(PublishChecks.CheckYearLayoutPeriodDependence(version, Engine, [Binding(PlainColumnId)]));
    }

    [Fact]
    [Trait("Requirement", "D-474")]
    public void Методологія_на_немісячній_колонці_блокує_річну_розкладку()
    {
        var version = Version(TemplateLayout.YearWithMonthColumns, TableLayoutKind.MonthsInColumns);

        var diagnostic = Assert.Single(PublishChecks.CheckYearLayoutPeriodDependence(version, Engine, [Binding(PlainColumnId)]));

        Assert.Equal("err.ECR-TMPL-0422.yearLayoutNonMonthBinding", diagnostic.MessageKey);
        Assert.Contains("Total", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "D-474")]
    public void Методологія_на_місячній_колонці_дозволена()
    {
        var version = Version(TemplateLayout.YearWithMonthColumns, TableLayoutKind.MonthsInColumns);

        Assert.Empty(PublishChecks.CheckYearLayoutPeriodDependence(version, Engine, [Binding(MonthColumnId)]));
    }

    private static ActiveColumnBinding Binding(int columnId)
        => new(columnId, "T1", columnId == PlainColumnId ? "Total" : "Jan", "M1", "E", "{}");

    /// <summary>Версія з однією таблицею: немісячна колонка <c>Total</c> і місячна <c>Jan</c>; формули — за колонкою.</summary>
    private static TemplateVersion Version(
        TemplateLayout layout,
        TableLayoutKind tableLayout,
        params (int ColumnId, string Expression)[] formulas)
    {
        var version = new TemplateVersion(1, "1.0.0.0", 1, DateTime.UtcNow);
        SetId(version, 7);
        version.SetLayout(layout);
        var sheet = new SheetDef(7, EcrCode.Create("S1"), Text("S1"), 1);
        SetId(sheet, 1);
        var table = new TableDef(1, EcrCode.Create("T1"), Text("T1"), 1, tableLayout, TableRowMode.Dynamic);
        SetId(table, 10);

        var total = new ColumnDef(table.Id, EcrCode.Create("Total"), Text("Total"), 1, CellDataType.Decimal);
        SetId(total, PlainColumnId);
        table.AddColumn(total);

        var january = new ColumnDef(table.Id, EcrCode.Create("Jan"), Text("Jan"), 2, CellDataType.Decimal);
        SetId(january, MonthColumnId);
        january.SetMonth(true, 1);
        table.AddColumn(january);

        var formulaId = 200;
        foreach (var (columnId, expression) in formulas)
        {
            var formula = new FormulaDef(table.Id, FormulaScope.Column, expression, ExpressionDialect.Template);
            SetId(formula, formulaId++);
            formula.AssignColumn(columnId);
            table.AddFormula(formula);
        }

        sheet.AddTable(table);
        version.AddSheet(sheet);
        return version;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
