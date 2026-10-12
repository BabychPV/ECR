// tests/Ecr.Application.Tests/Documents/VersionMigrationTableRenamesTests.cs
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Errors;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Адитивна мапа «було → стало» кодів таблиць і колонок у плані переносу версії (ФВ-7.5): дані з таблиці зі
/// СТАРИМ кодом переїжджають у таблицю з НОВИМ кодом цільової версії; без мапи (або де відповідник за кодом є)
/// поведінка не змінюється; суперечність мапи парі версій — відмова, а не вгадування.
/// </summary>
/// <remarks>
/// ⚠ Мутаційні докази (кожен роблять червоним рівно названі тести): прибрати гілку мапи в
/// <c>VersionMigrationPlanner.Plan</c> (<c>FindRename</c> завжди <c>null</c>) — «переїжджають», «колонки»,
/// «права/мапінги» і «Renamed»; прибрати перевірку <c>sourceTableCodes.Contains</c> — «ціль уже має відповідника».
/// </remarks>
public sealed class VersionMigrationTableRenamesTests
{
    private const string Sheet = "S9a";

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Без_мапи_таблиця_з_новим_кодом_нова_а_стара_втрачає_дані()
    {
        var from = Version(1, Table("T9a_03", 100, ("A", 101), ("B", 102)));
        var to = Version(2, Table("T9a_17a", 200, ("A", 201), ("B", 202)));

        var plan = VersionMigrationPlanner.Plan(from, to, [new(101, null, 3), new(102, null, 2)], Empty);

        Assert.Empty(plan.Tables);
        Assert.Equal(5, plan.LostValues);
        Assert.Contains("dataLoss", plan.RefusalsFor(VersionMigrationMode.Safe));
        Assert.Contains(plan.Items, i => i.Kind == "Lost" && i.Path == $"{Sheet}.T9a_03");
        Assert.Contains(plan.Items, i => i.Kind == "Added" && i.Path == $"{Sheet}.T9a_17a");
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void З_мапою_дані_таблиці_переїжджають_під_новий_код()
    {
        var from = Version(1, Table("T9a_03", 100, ("A", 101), ("B", 102)));
        var to = Version(2, Table("T9a_17a", 200, ("A", 201), ("B", 202)));

        var plan = VersionMigrationPlanner.Plan(
            from, to, [new(101, null, 3), new(102, null, 2)], Empty,
            [new VersionMigrationTableRename("T9a_03", "T9a_17a")]);

        Assert.Equal(200, plan.Tables[100]);
        Assert.Equal(
            [(101, 201, 200), (102, 202, 200)],
            plan.Columns.Select(c => (c.SourceColumnDefId, c.TargetColumnDefId, c.TargetTableDefId)).ToList());
        Assert.Equal(5, plan.TransferredValues);
        Assert.Equal(0, plan.LostValues);
        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));

        var renamed = Assert.Single(plan.Items, i => i.Kind == "Renamed");
        Assert.Equal($"{Sheet}.T9a_03", renamed.Path);
        Assert.Equal("T9a_03", renamed.OldValue);
        Assert.Equal("T9a_17a", renamed.NewValue);
        Assert.Equal(5, renamed.Values);

        // Нова таблиця цільової версії — не «Added»: вона відповідник, а не додана.
        Assert.DoesNotContain(plan.Items, i => i.Kind == "Added");
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Мапа_з_аркушем_діє_лише_на_цьому_аркуші()
    {
        var from = Version(1, Table("T9a_03", 100, ("A", 101)));
        var to = Version(2, Table("T9a_17a", 200, ("A", 201)));

        var other = VersionMigrationPlanner.Plan(
            from, to, [new(101, null, 3)], Empty,
            [new VersionMigrationTableRename("T9a_03", "T9a_17a", SheetCode: "other")]);
        var same = VersionMigrationPlanner.Plan(
            from, to, [new(101, null, 3)], Empty,
            [new VersionMigrationTableRename("T9a_03", "T9a_17a", SheetCode: Sheet)]);

        Assert.Empty(other.Tables);
        Assert.Single(same.Tables);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Де_відповідник_за_кодом_є_мапа_не_втручається()
    {
        var from = Version(1, Table("T9a_03", 100, ("A", 101)));
        var to = Version(2, Table("T9a_03", 300, ("A", 301)), Table("T9a_17a", 200, ("A", 201)));

        var plan = VersionMigrationPlanner.Plan(
            from, to, [new(101, null, 3)], Empty,
            [new VersionMigrationTableRename("T9a_03", "T9a_17a")]);

        Assert.Equal(300, plan.Tables[100]);
        Assert.DoesNotContain(plan.Items, i => i.Kind == "Renamed");
        Assert.Equal(0, plan.RenameConflictCount);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Ціль_уже_має_відповідника_за_кодом_у_вихідній_версії_це_конфлікт_а_не_злиття()
    {
        // Вихідна має і T9a_03, і T9a_17a; ціль має лише T9a_17a. Мапа 03 → 17a влила б дві таблиці в одну.
        var from = Version(1, Table("T9a_03", 100, ("A", 101)), Table("T9a_17a", 150, ("A", 151)));
        var to = Version(2, Table("T9a_17a", 200, ("A", 201)));

        var plan = VersionMigrationPlanner.Plan(
            from, to, [new(101, null, 3), new(151, null, 4)], Empty,
            [new VersionMigrationTableRename("T9a_03", "T9a_17a")]);

        Assert.Equal(1, plan.RenameConflictCount);
        Assert.Contains("renameConflict", plan.RefusalsFor(VersionMigrationMode.Safe));
        Assert.Equal(200, plan.Tables[150]);
        Assert.False(plan.Tables.ContainsKey(100));
        Assert.Contains(plan.Items, i => i.Kind == "RenameConflict" && i.Path == $"{Sheet}.T9a_03");
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Двоє_джерел_в_одну_ціль_це_конфлікт()
    {
        var from = Version(1, Table("T9a_03", 100, ("A", 101)), Table("T9a_06", 110, ("A", 111)));
        var to = Version(2, Table("T9a_17a", 200, ("A", 201)));

        var plan = VersionMigrationPlanner.Plan(
            from, to, [new(101, null, 3), new(111, null, 4)], Empty,
            [new VersionMigrationTableRename("T9a_03", "T9a_17a"), new VersionMigrationTableRename("T9a_06", "T9a_17a")]);

        Assert.Equal(1, plan.RenameConflictCount);
        Assert.Contains("renameConflict", plan.RefusalsFor(VersionMigrationMode.Safe));
        Assert.Single(plan.Tables);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Запис_мапи_що_не_стосується_пари_версій_мовчки_пропускається()
    {
        var from = Version(1, Table("T1", 100, ("A", 101)));
        var to = Version(2, Table("T1", 200, ("A", 201)));

        var plan = VersionMigrationPlanner.Plan(
            from, to, [new(101, null, 3)], Empty,
            [new VersionMigrationTableRename("T9a_03", "T9a_17a"), new VersionMigrationTableRename("T1", "Nope")]);

        Assert.Equal(200, plan.Tables[100]);
        Assert.DoesNotContain(plan.Items, i => i.Kind is "Renamed" or "RenameConflict");
        Assert.Equal(3, plan.TransferredValues);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Колонки_перейменованої_таблиці_мапляться_за_мапою_колонок()
    {
        var from = Version(1, Table("T9a_03", 100, ("A", 101), ("OLD", 102)));
        var to = Version(2, Table("T9a_17a", 200, ("A", 201), ("NEW", 202)));

        var plan = VersionMigrationPlanner.Plan(
            from, to, [new(101, null, 3), new(102, null, 7)], Empty,
            [new VersionMigrationTableRename(
                "T9a_03", "T9a_17a", Columns: [new VersionMigrationColumnRename("OLD", "NEW")])]);

        Assert.Equal(202, plan.Columns.Single(c => c.SourceColumnDefId == 102).TargetColumnDefId);
        Assert.Equal(10, plan.TransferredValues);
        Assert.Equal(0, plan.LostValues);
        Assert.Contains(plan.Items, i => i.Kind == "Renamed" && i.Path == $"{Sheet}.T9a_03.OLD" && i.NewValue == "NEW");
        Assert.DoesNotContain(plan.Items, i => i.Kind == "Added");
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Перейменування_колонок_у_таблиці_з_тим_самим_кодом()
    {
        var from = Version(1, Table("T1", 100, ("OLD", 101)));
        var to = Version(2, Table("T1", 200, ("NEW", 201)));

        var plan = VersionMigrationPlanner.Plan(
            from, to, [new(101, null, 4)], Empty,
            [new VersionMigrationTableRename("T1", "T1", Columns: [new VersionMigrationColumnRename("OLD", "NEW")])]);

        Assert.Equal(201, Assert.Single(plan.Columns).TargetColumnDefId);
        Assert.Equal(4, plan.TransferredValues);
        Assert.DoesNotContain(plan.Items, i => i.Kind == "Renamed" && i.Path == $"{Sheet}.T1");
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Колонка_ціль_якої_має_відповідника_за_кодом_це_конфлікт()
    {
        var from = Version(1, Table("T1", 100, ("OLD", 101), ("NEW", 102)));
        var to = Version(2, Table("T1", 200, ("NEW", 201)));

        var plan = VersionMigrationPlanner.Plan(
            from, to, [new(101, null, 4), new(102, null, 1)], Empty,
            [new VersionMigrationTableRename("T1", "T1", Columns: [new VersionMigrationColumnRename("OLD", "NEW")])]);

        Assert.Equal(1, plan.RenameConflictCount);
        Assert.Equal(201, Assert.Single(plan.Columns).TargetColumnDefId);
        Assert.Equal(102, plan.Columns[0].SourceColumnDefId);
        Assert.Equal(4, plan.LostValues);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Режим_Presentation_відмовляє_на_мапі_як_на_структурну_різницю()
    {
        var from = Version(1, Table("T9a_03", 100, ("A", 101)));
        var to = Version(2, Table("T9a_17a", 200, ("A", 201)));

        var plan = VersionMigrationPlanner.Plan(
            from, to, [], Empty, [new VersionMigrationTableRename("T9a_03", "T9a_17a")]);

        Assert.Contains("structural", plan.RefusalsFor(VersionMigrationMode.Presentation));
        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));
    }

    [Theory]
    [InlineData("", "T2", null, false)]
    [InlineData("T1", " ", null, false)]
    [InlineData("T1", "T1", null, false)]
    [InlineData("T1", "T2", "", false)]
    [InlineData("T1", "T2", null, true)]
    [Trait("Requirement", "ФВ-7.5")]
    public void Форма_мапи_перевіряється(string from, string to, string? sheet, bool valid)
    {
        var map = new[] { new VersionMigrationTableRename(from, to, sheet) };

        if (valid)
        {
            VersionMigrationRenames.Validate(map);
        }
        else
        {
            var ex = Assert.Throws<BusinessRuleException>(() => VersionMigrationRenames.Validate(map));
            Assert.Equal("err.ECR-SCHM-0422.migrateRenameMapInvalid", ex.Details!["messageKey"]);
        }
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Повтор_джерела_і_перевищення_стелі_відхиляються()
    {
        Assert.Throws<BusinessRuleException>(() => VersionMigrationRenames.Validate(
            [new("T1", "T2"), new("T1", "T3")]));
        Assert.Throws<BusinessRuleException>(() => VersionMigrationRenames.Validate(
            [.. Enumerable.Range(0, VersionMigrationRenames.MaxTableRenames + 1).Select(i => new VersionMigrationTableRename($"A{i}", $"B{i}"))]));
        VersionMigrationRenames.Validate(null);
        VersionMigrationRenames.Validate([]);
    }

    private static IReadOnlyDictionary<int, long> Empty { get; } = new Dictionary<int, long>();

    private static TemplateVersionSnapshot Version(int versionId, params TableDef[] tables)
    {
        var sheet = new SheetDef(versionId, EcrCode.Create(Sheet), Name("Sheet"), 1);
        SetId(sheet, versionId * 10);
        foreach (var table in tables)
        {
            sheet.AddTable(table);
        }

        return new TemplateVersionSnapshot(
            versionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef>(),
            RowsByKey: new Dictionary<(int, string), RowDef>());
    }

    private static TableDef Table(string code, int id, params (string Code, int Id)[] columns)
    {
        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create(code), Name(code), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(table, id);
        foreach (var (columnCode, columnId) in columns)
        {
            var column = new ColumnDef(id, EcrCode.Create(columnCode), Name(columnCode), 1, CellDataType.Decimal);
            SetId(column, columnId);
            table.AddColumn(column);
        }

        return table;
    }

    private static void SetId(object entity, int id)
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
