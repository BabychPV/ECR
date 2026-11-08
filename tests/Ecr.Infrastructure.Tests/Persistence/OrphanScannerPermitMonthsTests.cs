// tests/Ecr.Infrastructure.Tests/Persistence/OrphanScannerPermitMonthsTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Rule (h4), human 08.11: the nightly orphan scan re-evaluates LOCKED (Closed) months, and judges a permit reference of a
/// «months in columns» row by the months with data — so the flag clears once the data or the permit is fixed.
/// </summary>
/// <remarks>
/// Live case: LAND-FULL doc 2, Water Report R7001000, January 2026, permit KZ35VTE00340032 valid from 01.03.2026,
/// flagged 28.10 and frozen when January was locked.
/// Red proof: with <c>Closed</c> back out of <c>OrphanScanPlan.IsScannable</c> and of the scanner's period filter, the
/// first test keeps the flag; without <c>PermitVerdictsAsync</c> the two coverage tests judge by 31.01 and flag the row.
/// </remarks>
[Collection("SqlServer")]
public sealed class OrphanScannerPermitMonthsTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-h4")]
    public async Task A_flag_in_a_locked_month_clears_once_the_permit_is_fixed()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: 202601, rowCount: 1, ct: CancellationToken.None);
        await using var db = Context();
        await StateAsync(db, doc, PeriodState.Open);

        var entry = await EntryAsync(db, new DateOnly(2026, 3, 1), null, permitHeader: false);
        await ReferenceAsync(db, doc, doc.ColumnDefIds[0], entry.Id);

        await ScanAsync(permitRegistry: null);
        Assert.True(await OrphanedAsync(doc));

        await StateAsync(db, doc, PeriodState.Closed);
        await db.RegistryEntries.Where(e => e.Id == entry.Id).ExecuteUpdateAsync(
            s => s.SetProperty(e => e.ValidFrom, new DateOnly(2026, 1, 1)), CancellationToken.None);

        await ScanAsync(permitRegistry: null);
        Assert.False(await OrphanedAsync(doc));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-h4")]
    public async Task A_permit_row_is_flagged_for_an_uncovered_month_and_cleared_when_that_month_is_emptied()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: 202601, rowCount: 1, monthColumns: 3, ct: CancellationToken.None);
        await using var db = Context();
        await StateAsync(db, doc, PeriodState.Open);
        await StateAsync(db, doc, PeriodState.Closed);

        var entry = await EntryAsync(db, new DateOnly(2026, 3, 1), new DateOnly(2026, 12, 1), permitHeader: true);
        await ReferenceAsync(db, doc, doc.ColumnDefIds[1], entry.Id);
        await ValueAsync(db, doc, doc.MonthColumnDefIds[0], 120m); // January — not covered
        await ValueAsync(db, doc, doc.MonthColumnDefIds[2], 80m);  // March — covered

        await ScanAsync(entry.RegistryDefId);
        Assert.True(await OrphanedAsync(doc));

        await db.CellValues
            .Where(c => c.PeriodKeyValue == doc.PeriodKey.Value && c.TableRowId == doc.RowIds[0]
                        && c.ColumnDefId == doc.MonthColumnDefIds[0])
            .ExecuteDeleteAsync(CancellationToken.None);

        await ScanAsync(entry.RegistryDefId);
        Assert.False(await OrphanedAsync(doc));
    }

    /// <summary>The instance period is January, the permit starts in March: data only in March — not an orphan.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-h4")]
    public async Task A_permit_covering_all_months_with_data_is_not_an_orphan_even_if_invalid_on_the_period_end()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: 202601, rowCount: 1, monthColumns: 3, ct: CancellationToken.None);
        await using var db = Context();
        await StateAsync(db, doc, PeriodState.Open);

        var entry = await EntryAsync(db, new DateOnly(2026, 3, 1), new DateOnly(2026, 12, 1), permitHeader: true);
        await ReferenceAsync(db, doc, doc.ColumnDefIds[1], entry.Id);
        await ValueAsync(db, doc, doc.MonthColumnDefIds[2], 80m);

        await ScanAsync(entry.RegistryDefId);

        Assert.False(await OrphanedAsync(doc));
    }

    private static async Task StateAsync(EcrDbContext db, TestDocument doc, PeriodState state)
    {
        var period = await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId, CancellationToken.None);
        period.TransitionTo(state, new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<RegistryEntry> EntryAsync(EcrDbContext db, DateOnly from, DateOnly? to, bool permitHeader)
    {
        var def = new Domain.Entities.Configuration.RegistryDef(
            EcrCode.Create($"PRMV_{(permitHeader ? "H" : "R")}_{_tag}"), Text("Permit validity"), isTemporal: true);
        db.RegistryDefs.Add(def);
        await db.SaveChangesAsync(CancellationToken.None);

        var entry = new RegistryEntry(def.Id, EcrCode.Create($"P_{_tag}"), Text("KZ35VTE00340032"), 9, DateTime.UnixEpoch);
        entry.SetValidity(from, to);
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync(CancellationToken.None);

        return entry;
    }

    private static async Task ReferenceAsync(EcrDbContext db, TestDocument doc, int columnDefId, long entryId)
    {
        db.CellValues.Add(new CellValue(
            new CellAddress(doc.PeriodKey, doc.RowIds[0], columnDefId),
            doc.TableDefId,
            new CellValueData { ValueRegistryEntryId = entryId }));
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static async Task ValueAsync(EcrDbContext db, TestDocument doc, int columnDefId, decimal value)
    {
        db.CellValues.Add(new CellValue(
            new CellAddress(doc.PeriodKey, doc.RowIds[0], columnDefId),
            doc.TableDefId,
            new CellValueData { ValueNumeric = value }));
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task ScanAsync(int? permitRegistry)
    {
        await using var scan = Context();
        var permitTypes = Substitute.For<IPermitTypeStore>();
        permitTypes.ListPublishedHeaderRegistryIdsAsync(Arg.Any<CancellationToken>())
                   .Returns(permitRegistry is { } id ? new HashSet<int> { id } : new HashSet<int>());

        var scanner = new OrphanScanner(
            scan,
            new RegistryResolver(),
            new FixedClock(new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc)),
            new OrphanScanBudget($"orphan-prmv-{_tag}", RowBudget: 1_000_000, BatchCells: 20_000, TimeSpan.FromHours(1)),
            new NormalizedCellStore(scan),
            permitTypes);

        await scanner.ScanAllAsync(CancellationToken.None);
    }

    private async Task<bool> OrphanedAsync(TestDocument doc)
    {
        await using var check = Context();
        return await check.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == doc.PeriodKey.Value && r.Id == doc.RowIds[0])
            .Select(r => r.IsOrphaned)
            .SingleAsync(CancellationToken.None);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
