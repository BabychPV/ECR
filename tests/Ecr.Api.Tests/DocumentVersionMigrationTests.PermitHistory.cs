// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.PermitHistory.cs
using System.Net;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-7 (аудит 2026-11): історія дозволів (J01) — сегменти «дозвіл від дати» (<c>doc.PermitSegment</c>) і частини
/// поділеного місяця (<c>doc.CellPermitPart</c>) — переїжджає з переносом версії за тими самими мапами, що й комірки та
/// шапка.
/// </summary>
/// <remarks>
/// Читання — справжнім <see cref="PermitHistoryStore"/> (той, що за Id ПОТОЧНОЇ версії бере історію для діалогу, PATCH
/// і поділу місяця). Червоне до фіксу (крок D-7 у <c>WorkflowSql</c> прибрано): читач за Id цільової версії не бачить
/// нічого, рядки лишаються на колонках вихідної.
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    private static readonly DateOnly PermitFrom = new(2026, 1, 15);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-7")]
    public async Task Permit_history_and_month_parts_follow_columns_to_target_version()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var h = await ArrangePermitHistoryAsync(s, s.Doc.ColumnDefIds[1]).ConfigureAwait(true);

        await MigrateSafeAsync(s).ConfigureAwait(true);

        await AssertHistoryOnTargetAsync(s, h).ConfigureAwait(true);

        // ⛔ На колонках/полі вихідної версії нічого не лишилось.
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.Equal(0, await db.PermitSegments.AsNoTracking().CountAsync(
            x => x.DocumentId == s.Doc.DocumentId && ((x.ColumnDefId != null && s.Doc.ColumnDefIds.Contains(x.ColumnDefId.Value)) || x.HeaderFieldDefId == h.SourceHeaderId)).ConfigureAwait(true));
        Assert.Equal(0, await db.CellPermitParts.AsNoTracking().CountAsync(
            x => x.DocumentId == s.Doc.DocumentId && s.Doc.ColumnDefIds.Contains(x.ColumnDefId)).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-7")]
    public async Task Permit_history_of_a_removed_column_goes_with_its_values_and_the_rest_moves()
    {
        // Ціль без C3 (там лише порожня комірка — Safe переносить).
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var removedColumn = s.Doc.ColumnDefIds[2];
        var keptColumn = s.Doc.ColumnDefIds[0];
        var h = await ArrangePermitHistoryAsync(s, removedColumn).ConfigureAwait(true);

        await using (var arrange = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            arrange.PermitSegments.Add(PermitSegment.ForRow(
                s.Doc.DocumentId, s.Doc.TableDefId, null, h.RowKey, keptColumn, PermitFrom, h.EntryId, s.UserId, Now));
            await arrange.SaveChangesAsync().ConfigureAwait(true);
        }

        await MigrateSafeAsync(s).ConfigureAwait(true);

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.Equal(0, await db.PermitSegments.AsNoTracking().CountAsync(x => x.DocumentId == s.Doc.DocumentId && x.ColumnDefId == removedColumn).ConfigureAwait(true));
        Assert.Equal(0, await db.CellPermitParts.AsNoTracking().CountAsync(x => x.DocumentId == s.Doc.DocumentId && x.ColumnDefId == removedColumn).ConfigureAwait(true));

        var store = new PermitHistoryStore(db);
        var kept = await store.RowSegmentsAsync(s.Doc.DocumentId, s.TargetTableDefId, null, h.RowKey, s.TargetColumns["C1"], default).ConfigureAwait(true);
        Assert.Equal([PermitFrom], kept.Select(x => x.ValidFrom));
        Assert.Single(await store.HeaderSegmentsAsync(s.Doc.DocumentId, h.TargetHeaderId, default).ConfigureAwait(true));
    }

    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    private async Task MigrateSafeAsync(Scenario s)
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(false);
        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");
    }

    /// <summary>Історія, яку мусить бачити читач за Id ЦІЛЬОВОЇ версії: сегмент рядка, дві частини січня, сегмент шапки.</summary>
    private async Task AssertHistoryOnTargetAsync(Scenario s, PermitHistoryArrange h)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var store = new PermitHistoryStore(db);

        var row = await store.RowSegmentsAsync(s.Doc.DocumentId, s.TargetTableDefId, null, h.RowKey, s.TargetColumns["C2"], default).ConfigureAwait(false);
        Assert.Equal([(PermitFrom, h.EntryId)], row.Select(x => (x.ValidFrom, x.RegistryEntryId)));

        var parts = await store.CellPartsAsync(202601, s.Doc.DocumentId, s.TargetTableDefId, null, h.RowKey, s.TargetColumns["C2"], default).ConfigureAwait(false);
        Assert.Equal([((byte)1, 20m), ((byte)2, 22m)], parts.Select(p => (p.PartNo, p.ValueNumeric ?? 0m)));

        var header = await store.HeaderSegmentsAsync(s.Doc.DocumentId, h.TargetHeaderId, default).ConfigureAwait(false);
        Assert.Equal([PermitFrom], header.Select(x => x.ValidFrom));
    }

    /// <summary>
    /// Історія дозволів документа на вихідній версії: сегмент рядка першого рядка на <paramref name="column"/> від 15.01,
    /// січень цієї комірки поділено на дві частини, сегмент поля шапки від 15.01; поле шапки з тим самим кодом — і в цілі.
    /// </summary>
    private async Task<PermitHistoryArrange> ArrangePermitHistoryAsync(Scenario s, int column)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var registry = new RegistryDef(EcrCode.Create("PRM_MG" + s.Tag), Name("Permit"), isTemporal: true);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var entry = new RegistryEntry(registry.Id, EcrCode.Create("MIDJAN"), Name("KZ-MIDJAN"));
        entry.SetValidity(PermitFrom, new DateOnly(2027, 1, 1));
        db.RegistryEntries.Add(entry);

        var headerCode = EcrCode.Create("PRMH" + s.Tag);
        var sourceHeader = new HeaderFieldDef(s.Doc.TemplateVersionId, headerCode, Name("Permit"), 0, CellDataType.Lookup);
        sourceHeader.SetLookup(registry.Id);
        var targetHeader = new HeaderFieldDef(s.TargetVersionId, headerCode, Name("Permit v2"), 0, CellDataType.Lookup);
        targetHeader.SetLookup(registry.Id);
        db.HeaderFieldDefs.AddRange(sourceHeader, targetHeader);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var rowKey = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == s.Doc.PeriodKey.Value && r.Id == s.Doc.RowIds[0])
            .Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(false);

        db.PermitSegments.Add(PermitSegment.ForRow(
            s.Doc.DocumentId, s.Doc.TableDefId, null, rowKey, column, PermitFrom, entry.Id, s.UserId, Now));
        db.PermitSegments.Add(PermitSegment.ForHeader(s.Doc.DocumentId, sourceHeader.Id, PermitFrom, entry.Id, s.UserId, Now));
        db.CellPermitParts.Add(new CellPermitPart(
            202601, s.Doc.DocumentId, s.Doc.TableDefId, null, rowKey, column, 1, entry.Id,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 14), 20m));
        db.CellPermitParts.Add(new CellPermitPart(
            202601, s.Doc.DocumentId, s.Doc.TableDefId, null, rowKey, column, 2, entry.Id,
            PermitFrom, new DateOnly(2026, 1, 31), 22m));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new PermitHistoryArrange(rowKey, entry.Id, sourceHeader.Id, targetHeader.Id);
    }

    private sealed record PermitHistoryArrange(string RowKey, long EntryId, int SourceHeaderId, int TargetHeaderId);
}
