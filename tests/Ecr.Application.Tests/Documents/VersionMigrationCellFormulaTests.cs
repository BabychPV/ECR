// tests/Ecr.Application.Tests/Documents/VersionMigrationCellFormulaTests.cs
using Ecr.Application.Documents.VersionMigration;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// WW-02: нова комірочна формула в цільовій версії робить комірку обчислюваною (перший перерахунок перезапише
/// введене, редагування закрите). На порожній комірці — безпечне додавання; на комірці з непорожнім значенням —
/// <c>Modified</c>/Guarded зі скількістю значень (відмова <c>guardedWithData</c>, аркуш «зачеплений» для LP-32).
/// Зміна <c>RowDef.IsReadOnly</c> класифікується так само.
/// </summary>
/// <remarks>
/// ⚠ Мутаційні докази (червоніють названі тести): у <c>CompareCellFormulas</c> прибрати виклик — червоніють
/// «на комірці зі значенням»; прибрати перевірку <c>alreadyComputed</c> — «вже рахувалась»; у <c>CompareRows</c>
/// прибрати гілку IsReadOnly — «рядок став лише для читання».
/// </remarks>
public sealed class VersionMigrationCellFormulaTests
{
    private const string Sheet = "S02";
    private const int SourceColumn = 101;
    private const int SourceRow = 301;
    private const int OtherSourceRow = 302;

    [Fact]
    [Trait("Requirement", "WW-02")]
    public void Нова_формула_комірки_на_порожній_комірці_безпечна()
    {
        var plan = Plan(source: Version(1, cellFormula: false), target: Version(2, cellFormula: true), values: []);

        Assert.Equal(0, plan.GuardedValues);
        Assert.Equal(0, plan.LostValues);
        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));
        Assert.Empty(plan.TouchedSheets);

        var added = Assert.Single(plan.Items, i => i.Field == "CellFormulas");
        Assert.Equal((("Added"), ChangeClass.Safe, "1"), (added.Kind, added.ChangeClass, added.NewValue));
        Assert.DoesNotContain(plan.Items, i => i.Kind == "Modified");
    }

    [Fact]
    [Trait("Requirement", "WW-02")]
    public void Нова_формула_комірки_на_комірці_зі_значенням_Guarded_із_лічильником_і_зачіпає_аркуш()
    {
        var plan = Plan(
            source: Version(1, cellFormula: false),
            target: Version(2, cellFormula: true),
            values: [new VersionMigrationCellUsage(SourceColumn, SourceRow, 5)]);

        Assert.Equal(5, plan.GuardedValues);
        Assert.Contains("guardedWithData", plan.RefusalsFor(VersionMigrationMode.Safe));

        var item = Assert.Single(plan.Items, i => i.Field == "CellFormula");
        Assert.Equal("Modified", item.Kind);
        Assert.Equal(ChangeClass.Guarded, item.ChangeClass);
        Assert.Equal(5, item.Values);
        Assert.Equal($"{Sheet}.T02#R1.A", item.Path);
        Assert.Contains(1 * 10, plan.TouchedSheets); // SheetDef.Id вихідної версії = versionId * 10
    }

    [Fact]
    [Trait("Requirement", "WW-02")]
    public void Значення_в_іншій_комірці_того_ж_рядка_чи_колонки_формулу_не_зачіпає()
    {
        var plan = Plan(
            source: Version(1, cellFormula: false),
            target: Version(2, cellFormula: true),
            values:
            [
                new VersionMigrationCellUsage(SourceColumn, OtherSourceRow, 9), // та сама колонка, інший рядок
                new VersionMigrationCellUsage(102, SourceRow, 4),               // той самий рядок, інша колонка
            ]);

        Assert.Equal(0, plan.GuardedValues);
        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));
        Assert.Equal(13, plan.TransferredValues);
    }

    [Fact]
    [Trait("Requirement", "WW-02")]
    public void Комірка_яку_вихідна_версія_вже_рахувала_формулою_не_дає_відмінності()
    {
        var plan = Plan(
            source: Version(1, cellFormula: true),
            target: Version(2, cellFormula: true),
            values: [new VersionMigrationCellUsage(SourceColumn, SourceRow, 5)]);

        Assert.Equal(0, plan.GuardedValues);
        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));
        Assert.DoesNotContain(plan.Items, i => i.Field is "CellFormula" or "CellFormulas");
    }

    [Fact]
    [Trait("Requirement", "WW-02")]
    public void Комірка_обчислюваної_колонки_вихідної_версії_вже_рахувалась()
    {
        var plan = Plan(
            source: Version(1, cellFormula: false, sourceColumnType: CellDataType.Formula),
            target: Version(2, cellFormula: true, sourceColumnType: CellDataType.Formula),
            values: [new VersionMigrationCellUsage(SourceColumn, SourceRow, 5)]);

        Assert.Equal(0, plan.GuardedValues);
        Assert.DoesNotContain(plan.Items, i => i.Field is "CellFormula" or "CellFormulas");
    }

    [Fact]
    [Trait("Requirement", "WW-02")]
    public void Рядок_став_лише_для_читання_зі_значеннями_це_Guarded()
    {
        var plan = Plan(
            source: Version(1, cellFormula: false),
            target: Version(2, cellFormula: false, rowReadOnly: true),
            values:
            [
                new VersionMigrationCellUsage(SourceColumn, SourceRow, 3),
                new VersionMigrationCellUsage(102, SourceRow, 2),
            ]);

        Assert.Equal(5, plan.GuardedValues);
        Assert.Contains("guardedWithData", plan.RefusalsFor(VersionMigrationMode.Safe));
        var item = Assert.Single(plan.Items, i => i.Field == "IsReadOnly");
        Assert.Equal(("Modified", ChangeClass.Guarded, "editable", "readOnly"), (item.Kind, item.ChangeClass, item.OldValue, item.NewValue));
    }

    [Fact]
    [Trait("Requirement", "WW-02")]
    public void Зняття_лише_читання_з_рядка_розширює_права_і_нічого_не_ламає()
    {
        var plan = Plan(
            source: Version(1, cellFormula: false, rowReadOnly: true),
            target: Version(2, cellFormula: false),
            values: [new VersionMigrationCellUsage(SourceColumn, SourceRow, 3)]);

        Assert.Equal(0, plan.GuardedValues);
        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));
        var item = Assert.Single(plan.Items, i => i.Field == "IsReadOnly");
        Assert.Equal(ChangeClass.Safe, item.ChangeClass);
        Assert.Empty(plan.TouchedSheets);
    }

    [Fact]
    [Trait("Requirement", "WW-02")]
    public void Режим_Presentation_формулу_комірки_не_пропускає_як_структурну_зміну()
    {
        var plan = Plan(source: Version(1, cellFormula: false), target: Version(2, cellFormula: true), values: []);

        Assert.Contains("structural", plan.RefusalsFor(VersionMigrationMode.Presentation));
    }

    // ── ACC-OW: прапор acceptGuardedOverwrite знімає guardedWithData ЛИШЕ для нової комірочної формули ──

    [Fact]
    [Trait("Requirement", "ACC-OW")]
    public void Прапор_знімає_guardedWithData_для_нової_формули_комірки_зі_значенням()
    {
        var plan = Plan(
            source: Version(1, cellFormula: false),
            target: Version(2, cellFormula: true),
            values: [new VersionMigrationCellUsage(SourceColumn, SourceRow, 5)]);

        Assert.Equal(5, plan.GuardedValues);
        Assert.Equal(5, plan.GuardedOverwritableValues);
        Assert.Contains("guardedWithData", plan.RefusalsFor(VersionMigrationMode.Safe));
        Assert.Contains("guardedWithData", plan.RefusalsFor(VersionMigrationMode.Safe, acceptGuardedOverwrite: false));
        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe, acceptGuardedOverwrite: true));

        // Перелік — адреса комірки шаблону і кількість значень, без самих значень.
        var item = Assert.Single(plan.GuardedOverwrites);
        Assert.Equal((Sheet, "T02", "R1", "A", 5L), (item.SheetCode, item.TableCode, item.RowKey, item.ColumnCode, item.Values));
    }

    [Fact]
    [Trait("Requirement", "ACC-OW")]
    public void Прапор_не_знімає_інші_класи_guarded_навіть_поруч_із_формулою_комірки()
    {
        // Формула комірки R1×A (5 значень) + звуження Scale колонки B (3 значення).
        var plan = Plan(
            source: Version(1, cellFormula: false, columnBScale: 12),
            target: Version(2, cellFormula: true, columnBScale: 10),
            values:
            [
                new VersionMigrationCellUsage(SourceColumn, SourceRow, 5),
                new VersionMigrationCellUsage(102, SourceRow, 3),
            ]);

        Assert.Equal(8, plan.GuardedValues);
        Assert.Equal(5, plan.GuardedOverwritableValues);
        Assert.Contains("guardedWithData", plan.RefusalsFor(VersionMigrationMode.Safe, acceptGuardedOverwrite: true));
    }

    [Fact]
    [Trait("Requirement", "ACC-OW")]
    public void Прапор_не_знімає_guarded_рядка_лише_для_читання()
    {
        var plan = Plan(
            source: Version(1, cellFormula: false),
            target: Version(2, cellFormula: false, rowReadOnly: true),
            values: [new VersionMigrationCellUsage(SourceColumn, SourceRow, 3)]);

        Assert.Equal(0, plan.GuardedOverwritableValues);
        Assert.Contains("guardedWithData", plan.RefusalsFor(VersionMigrationMode.Safe, acceptGuardedOverwrite: true));
    }

    [Fact]
    [Trait("Requirement", "ACC-OW")]
    public void Прапор_без_формули_комірки_нічого_не_міняє_і_Presentation_лишається_structural()
    {
        var plain = Plan(source: Version(1, cellFormula: false), target: Version(2, cellFormula: false), values: []);
        Assert.Empty(plain.RefusalsFor(VersionMigrationMode.Safe, acceptGuardedOverwrite: true));
        Assert.Equal(0, plain.GuardedOverwritableValues);
        Assert.Empty(plain.GuardedOverwrites);

        var formula = Plan(source: Version(1, cellFormula: false), target: Version(2, cellFormula: true), values: []);
        Assert.Contains(
            "structural", formula.RefusalsFor(VersionMigrationMode.Presentation, acceptGuardedOverwrite: true));
    }

    private static VersionMigrationPlan Plan(
        TemplateVersionSnapshot source, TemplateVersionSnapshot target, IReadOnlyList<VersionMigrationCellUsage> values)
        => VersionMigrationPlanner.Plan(source, target, values, new Dictionary<int, long>());

    /// <summary>Версія: аркуш S02 → таблиця T02 → колонки A (інпут) і B; рядки R1 і R2; опційно формула комірки R1×A.</summary>
    private static TemplateVersionSnapshot Version(
        int versionId, bool cellFormula, bool rowReadOnly = false, CellDataType sourceColumnType = CellDataType.Decimal,
        byte? columnBScale = null)
    {
        var baseId = versionId * 100;
        var sheet = new SheetDef(versionId, EcrCode.Create(Sheet), Name("Sheet"), 1);
        SetId(sheet, versionId * 10);

        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("T02"), Name("T02"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(table, baseId);

        var columnA = new ColumnDef(table.Id, EcrCode.Create("A"), Name("A"), 1, sourceColumnType);
        SetId(columnA, baseId + 1);
        var columnB = new ColumnDef(table.Id, EcrCode.Create("B"), Name("B"), 2, CellDataType.Decimal);
        SetId(columnB, baseId + 2);
        if (columnBScale is { } scale)
        {
            columnB.SetNumericFormat(28, scale);
        }

        table.AddColumn(columnA);
        table.AddColumn(columnB);

        var row1 = new RowDef(table.Id, RowKey.Create("R1"), 1, Name("R1"), RowKind.Item);
        SetId(row1, baseId + 201);
        row1.SetReadOnly(rowReadOnly);
        var row2 = new RowDef(table.Id, RowKey.Create("R2"), 2, Name("R2"), RowKind.Item);
        SetId(row2, baseId + 202);
        table.AddRow(row1);
        table.AddRow(row2);

        if (cellFormula)
        {
            var formula = new FormulaDef(table.Id, FormulaScope.Cell, "[B]", ExpressionDialect.Template);
            formula.AssignCell(row1.Id, columnA.Id);
            table.AddFormula(formula);
        }

        sheet.AddTable(table);

        return new TemplateVersionSnapshot(
            versionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef>(),
            RowsByKey: new Dictionary<(int, string), RowDef>());
    }

    private static void SetId(object entity, int id)
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
