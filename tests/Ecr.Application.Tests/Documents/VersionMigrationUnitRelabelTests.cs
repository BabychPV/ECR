// tests/Ecr.Application.Tests/Documents/VersionMigrationUnitRelabelTests.cs
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// ✎ P-1123-4 (рішення координатора 23.11): зміна одиниці колонки зі значеннями проходить Safe ЛИШЕ як заявлене
/// перепідписання без перерахунку «безрозмірна → підтверджена» (Density one → kg_per_Sm3); значення переносяться як є
/// (у плані — колонка 1:1, нічого не втрачено й не «змінило тлумачення»). Без заяви, з іншою одиницею, ніж заявлено, або
/// з фізичної одиниці — <c>guardedWithData</c>, як і раніше.
/// </summary>
/// <remarks>
/// Мутаційний доказ: у <c>CompareColumns</c> прибрати гілку <c>unitRelabels</c> (лишити лише <c>Guarded(...UnitId...)</c>) —
/// червоніє «заявлене_перепідписання…»; прибрати перевірку <c>column.UnitId == relabel.FromUnitId</c> — червоніє
/// «заява_з_іншої_одиниці…»; прибрати перевірку розмірності в <c>VersionMigrationUnitRelabels</c> — червоніє «фізичну…».
/// </remarks>
public sealed class VersionMigrationUnitRelabelTests
{
    private const int One = 7;
    private const int KgPerSm3 = 35;
    private const int Kg = 3;

    private static readonly UnitCatalogSnapshot Catalog = new(
        new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
        {
            ["one"] = new(One, "one", 1, DimensionCode: "Dimensionless"),
            ["kg_per_Sm3"] = new(KgPerSm3, "kg_per_Sm3", 16, DimensionCode: "MassPerStdVolume"),
            ["kg"] = new(Kg, "kg", 2, DimensionCode: "Mass"),
        },
        new Dictionary<string, int>());

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Заявлене_перепідписання_безрозмірної_одиниці_проходить_Safe_і_переносить_значення_як_є()
    {
        var from = Version(1, One, 100, 101);
        var to = Version(2, KgPerSm3, 200, 201);
        var renames = Declare("one", "kg_per_Sm3");

        var plan = await PlanAsync(from, to, renames, values: 8);

        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));
        Assert.Equal(0, plan.GuardedValues);
        Assert.Equal(8, plan.TransferredValues);
        Assert.Equal(0, plan.LostValues);
        var column = Assert.Single(plan.Columns);
        Assert.Equal((101, 201), (column.SourceColumnDefId, column.TargetColumnDefId));
        var item = Assert.Single(plan.Items, i => i.Kind == VersionMigrationResolvedRelabel.Kind);
        Assert.Equal(("S3.T3_00.Density", ChangeClass.Safe, 8L, "UnitId", "one", "kg_per_Sm3"),
            (item.Path, item.ChangeClass, item.Values, item.Field, item.OldValue, item.NewValue));
        Assert.DoesNotContain(plan.Items, i => i.Kind == "Modified");
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Без_заяви_зміна_одиниці_з_даними_лишається_guardedWithData()
    {
        var plan = await PlanAsync(Version(1, One, 100, 101), Version(2, KgPerSm3, 200, 201), renames: null, values: 8);

        Assert.Contains("guardedWithData", plan.RefusalsFor(VersionMigrationMode.Safe));
        Assert.Contains(plan.Items, i => i.Kind == "Modified" && i.Field == "UnitId");
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Заява_з_іншої_одиниці_ніж_у_версії_не_знімає_охорони()
    {
        // Заявлено one → kg_per_Sm3, а цільова колонка — kg: не те, що заявлено.
        var plan = await PlanAsync(Version(1, One, 100, 101), Version(2, Kg, 200, 201), Declare("one", "kg_per_Sm3"), values: 3);

        Assert.Contains("guardedWithData", plan.RefusalsFor(VersionMigrationMode.Safe));
        Assert.DoesNotContain(plan.Items, i => i.Kind == VersionMigrationResolvedRelabel.Kind);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Перепідписати_фізичну_одиницю_не_можна()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            PlanAsync(Version(1, Kg, 100, 101), Version(2, KgPerSm3, 200, 201), Declare("kg", "kg_per_Sm3"), values: 3));

        Assert.Equal("err.ECR-SCHM-0422.migrateRenameMapInvalid", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Заява_на_колонку_якої_немає_відхиляється_а_не_мовчки_пропускається()
    {
        var renames = new[]
        {
            new VersionMigrationTableRename("T3_00", "T3_00", "S3", UnitRelabels: [new("Nope", "one", "kg_per_Sm3")]),
        };

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            PlanAsync(Version(1, One, 100, 101), Version(2, KgPerSm3, 200, 201), renames, values: 1));
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Форма_заяви_перевіряється_й_запис_лише_з_перепідписанням_допустимий()
    {
        VersionMigrationRenames.Validate(Declare("one", "kg_per_Sm3"));
        Assert.Throws<BusinessRuleException>(() => VersionMigrationRenames.Validate(Declare("one", "one")));
        Assert.Throws<BusinessRuleException>(() => VersionMigrationRenames.Validate(
        [
            new VersionMigrationTableRename("T3_00", "T3_00", "S3", UnitRelabels: [new("Density", "one", "kg_per_Sm3"), new("Density", "one", "kg_per_Sm3")]),
        ]));
    }

    private static VersionMigrationTableRename[] Declare(string fromUnit, string toUnit)
        => [new VersionMigrationTableRename("T3_00", "T3_00", "S3", UnitRelabels: [new("Density", fromUnit, toUnit)])];

    private static async Task<VersionMigrationPlan> PlanAsync(
        TemplateVersionSnapshot from, TemplateVersionSnapshot to, IReadOnlyList<VersionMigrationTableRename>? renames, long values)
    {
        var relabels = await VersionMigrationUnitRelabels.ResolveAsync(renames, from, new FixedCatalog(), CancellationToken.None);
        return VersionMigrationPlanner.Plan(
            from, to, [new VersionMigrationCellUsage(101, null, values)], new Dictionary<int, long>(), renames,
            unitRelabels: relabels);
    }

    private static TemplateVersionSnapshot Version(int versionId, int unitId, int tableId, int columnId)
    {
        var sheet = new SheetDef(versionId, EcrCode.Create("S3"), Name("Sheet"), 1);
        SetId(sheet, versionId * 10);
        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("T3_00"), Name("T3_00"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(table, tableId);
        var column = new ColumnDef(tableId, EcrCode.Create("Density"), Name("Density"), 1, CellDataType.Decimal);
        SetId(column, columnId);
        column.SetUnit(unitId);
        table.AddColumn(column);
        sheet.AddTable(table);

        return new TemplateVersionSnapshot(
            versionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef>(),
            RowsByKey: new Dictionary<(int, string), RowDef>());
    }

    private static void SetId(object entity, int id)
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed class FixedCatalog : IUnitCatalog
    {
        public Task<UnitCatalogSnapshot> GetAsync(CancellationToken ct) => Task.FromResult(Catalog);
    }
}
