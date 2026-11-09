// tests/Ecr.Application.Tests/Recalculation/SheetDisplayNamesTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// <c>SHEETNAME()</c> у перерахунку документа: назва «виду» прогону за правилом <c>SheetCopyNaming</c> (те саме, що
/// <c>rpt.*._SheetDisplayName</c>) і запасна назва аркуша шаблону, коли даних документа немає.
/// </summary>
/// <remarks>
/// Мутаційні докази (локально, не комітяться): у <c>SheetDisplayNames.For</c> брати номер оригіналу й для копії —
/// червоніє <see cref="Копія_отримує_свій_номер_а_оригінал_номер_1"/>; прибрати запасну гілку
/// <c>SliceEvaluationContext.SheetName</c> — червоніє <see cref="Контекст_без_назв_документа_дає_назву_аркуша_шаблону"/>.
/// </remarks>
public sealed class SheetDisplayNamesTests
{
    private const int VersionId = 7;
    private const int VesselSheet = 11;
    private const int OtherSheet = 12;
    private const int VesselTable = 21;
    private const int OtherTable = 22;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Група_без_копій_дає_назву_без_номера()
    {
        var names = SheetDisplayNames.For(Snapshot(), [], copyId: null);

        Assert.Equal("Vessel", names[VesselSheet]);
        Assert.Equal("OTHER", names[OtherSheet]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Копія_отримує_свій_номер_а_оригінал_номер_1()
    {
        var groups = new[]
        {
            new SheetCopyGroupInfo(VesselSheet, null, [new SheetCopyOrdinal(501, 2), new SheetCopyOrdinal(502, 3)]),
        };

        Assert.Equal("Vessel 1", SheetDisplayNames.For(Snapshot(), groups, copyId: null)[VesselSheet]);
        Assert.Equal("Vessel 2", SheetDisplayNames.For(Snapshot(), groups, copyId: 501)[VesselSheet]);
        Assert.Equal("Vessel 3", SheetDisplayNames.For(Snapshot(), groups, copyId: 502)[VesselSheet]);

        // Інший аркуш виду копії — оригінал без копій.
        Assert.Equal("OTHER", SheetDisplayNames.For(Snapshot(), groups, copyId: 501)[OtherSheet]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Перейменування_групи_міняє_назву_всіх_копій_а_мова_без_en_бере_en_шаблону()
    {
        var renamed = new[]
        {
            new SheetCopyGroupInfo(
                VesselSheet, new LocalizedText(new Dictionary<string, string> { ["en"] = "Contract" }),
                [new SheetCopyOrdinal(501, 2)]),
        };
        var ruOnly = new[]
        {
            new SheetCopyGroupInfo(
                VesselSheet, new LocalizedText(new Dictionary<string, string> { ["ru"] = "Сосуд" }),
                [new SheetCopyOrdinal(501, 2)]),
        };

        Assert.Equal("Contract 2", SheetDisplayNames.For(Snapshot(), renamed, copyId: 501)[VesselSheet]);
        Assert.Equal("Vessel 2", SheetDisplayNames.For(Snapshot(), ruOnly, copyId: 501)[VesselSheet]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Гейт_читання_назв_бачить_лише_вирази_зі_SHEETNAME()
    {
        Assert.True(SheetDisplayNames.MayUse(["[A] + 1", "sheetName() + 'x'"]));
        Assert.False(SheetDisplayNames.MayUse(["[A] + 1", null, "SUM([B])"]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Контекст_перерахунку_дає_назву_аркуша_таблиці_формули()
    {
        var context = Context(new Dictionary<int, string> { [VesselSheet] = "Vessel 2" });

        context.CurrentTableDefId = VesselTable;
        Assert.Equal("Vessel 2", new RealFormulaEngine().Evaluate(Parse("SHEETNAME()"), context).Value.Value);

        // Аркуш, якого словник не має, — назва шаблону (код, бо en немає).
        context.CurrentTableDefId = OtherTable;
        Assert.Equal("OTHER", context.SheetName);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Контекст_без_назв_документа_дає_назву_аркуша_шаблону()
    {
        var context = Context(sheetNames: null);
        context.CurrentTableDefId = VesselTable;

        Assert.Equal("Vessel", context.SheetName);
    }

    private static Ecr.Expressions.Parsing.ParsedExpression Parse(string text)
        => new RealFormulaEngine().Parse(text, ExpressionDialect.Template).Expression!;

    private static SliceEvaluationContext Context(IReadOnlyDictionary<int, string>? sheetNames)
        => new(
            Snapshot(),
            new Dictionary<CellKey, ExpressionValue>(),
            new Dictionary<string, ExpressionValue>(),
            new PeriodContext(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), CalendarMode.Actual, 2026, 3),
            UnitCatalogSnapshot.Empty,
            new Dictionary<int, IReadOnlyDictionary<string, long>>(),
            sheetNames: sheetNames);

    private static TemplateVersionSnapshot Snapshot()
    {
        var vessel = new SheetDef(VersionId, EcrCode.Create("VESSEL"), Name("en", "Vessel"), 1);
        SetId(vessel, VesselSheet);
        var vesselTable = new TableDef(VesselSheet, EcrCode.Create("VT"), Name("en", "T"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(vesselTable, VesselTable);
        vessel.AddTable(vesselTable);

        // Без англійської назви: значення — код аркуша (як rpt.*._SheetDisplayName).
        var other = new SheetDef(VersionId, EcrCode.Create("OTHER"), Name("ru", "Прочее"), 2);
        SetId(other, OtherSheet);
        var otherTable = new TableDef(OtherSheet, EcrCode.Create("OT"), Name("en", "T"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(otherTable, OtherTable);
        other.AddTable(otherTable);

        return new TemplateVersionSnapshot(
            VersionId, 0, [vessel, other], new Dictionary<int, ColumnDef>(), new Dictionary<(int, string), RowDef>());
    }

    private static LocalizedText Name(string language, string value)
        => new(new Dictionary<string, string> { [language] = value });

    private static void SetId(object entity, int id)
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
