// tests/Ecr.Application.Tests/Documents/VersionMigrationLockedOverridesTests.cs
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// ✎ V-7 (аудит 2026-11, рішення PO 11.11, варіант А): ручне значення замість формули (D-461), яке перенос версії
/// зняв би, на ПОДАНОМУ чи ЗАТВЕРДЖЕНОМУ аркуші блокує перенос — <c>LockedOverrideTouches</c> рахує такі позначки.
/// </summary>
/// <remarks>
/// Мутаційний доказ: повернути з <c>LockedOverrideTouches</c> порожній результат — червоніють випадки (а) і (в);
/// прибрати перевірку пари в <c>lockedRefs</c> — червоніє (б).
/// </remarks>
public sealed class VersionMigrationLockedOverridesTests
{
    private const int Period = 202601;
    private const long DocumentId = 1;

    private static (TemplateVersionSnapshot From, int SheetId, int ColumnId) Source()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("S1");
        var table = builder.Table(sheet, "T");
        var formula = builder.Column(table, "F", CellDataType.Calculated);
        builder.Row(table, "R1", 1);
        builder.Row(table, "R2", 2);
        builder.Formula(table, "1 + 1", column: formula).SetAllowManualOverride(true);

        return (builder.Build(), sheet.Id, formula.Id);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "V-7")]
    public void Позначка_на_затвердженому_аркуші_рахується_і_називає_аркуш()
    {
        var (from, sheetId, columnId) = Source();

        var result = MigrateDocumentVersionHandler.LockedOverrideTouches(
            [Mark(columnId, 1001)],
            from,
            [new VersionMigrationLockedSheet(DocumentId, sheetId, Period, "Approved")]);

        Assert.Equal(1, result.Marks);
        var item = Assert.Single(result.Items);
        Assert.Equal(new LockedSheetItem(DocumentId, "S1", Period, "Approved", Affected: true), item);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "V-7")]
    public void Позначка_на_аркуші_в_роботі_переносу_не_блокує()
    {
        var (from, sheetId, columnId) = Source();

        // Замкнена інша пара (інший період і інший документ) — аркуш позначки в роботі.
        var result = MigrateDocumentVersionHandler.LockedOverrideTouches(
            [Mark(columnId, 1001)],
            from,
            [
                new VersionMigrationLockedSheet(DocumentId, sheetId, Period + 1, "Approved"),
                new VersionMigrationLockedSheet(DocumentId + 1, sheetId, Period, "Submitted"),
            ]);

        Assert.Equal(0, result.Marks);
        Assert.Empty(result.Items);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "V-7")]
    public void Дві_позначки_одного_аркуша_періоду_дві_позначки_один_рядок_переліку()
    {
        var (from, sheetId, columnId) = Source();

        var result = MigrateDocumentVersionHandler.LockedOverrideTouches(
            [Mark(columnId, 1001), Mark(columnId, 1002)],
            from,
            [new VersionMigrationLockedSheet(DocumentId, sheetId, Period, "Submitted")]);

        Assert.Equal(2, result.Marks);
        var item = Assert.Single(result.Items);
        Assert.Equal("Submitted", item.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "V-7")]
    public void Без_переліку_замкнених_пар_або_без_позначок_нічого_не_рахується()
    {
        var (from, sheetId, columnId) = Source();

        var noRefs = MigrateDocumentVersionHandler.LockedOverrideTouches([Mark(columnId, 1001)], from, lockedRefs: null);
        var noMarks = MigrateDocumentVersionHandler.LockedOverrideTouches(
            [], from, [new VersionMigrationLockedSheet(DocumentId, sheetId, Period, "Approved")]);

        Assert.Equal(0, noRefs.Marks);
        Assert.Empty(noRefs.Items);
        Assert.Equal(0, noMarks.Marks);
        Assert.Empty(noMarks.Items);
    }

    private static ProjectFormulaOverride Mark(int columnId, long rowId)
        => new(new CellAddress(new PeriodKey(Period), rowId, columnId), DocumentId, "R1", "100");
}
