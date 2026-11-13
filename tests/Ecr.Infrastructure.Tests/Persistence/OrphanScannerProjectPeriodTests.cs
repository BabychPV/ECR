// tests/Ecr.Infrastructure.Tests/Persistence/OrphanScannerProjectPeriodTests.cs
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>OrphanScanner</c> бере дату й стан з періоду ПРОЄКТУ рядка, а не з
/// першого-ліпшого відкритого періоду з тим самим <c>PeriodKeyValue</c>
/// (R5-D1 / D1-01).
/// </summary>
/// <remarks>
/// ⛔ Дефект: <c>OpenPeriodsAsync</c> групував відкриті періоди за ключем і
/// брав <c>First()</c>. Ключ <c>Year*100+Sequence</c> унікальний лише в межах
/// проєкту: 202601 — це і місячний січень (31.01), і річний 2026 (31.12).
/// Обидва рядки отримували ОДНУ дату, тож один із двох тверджень першого тесту
/// падав завжди, хоч би який період виявився «першим». Другий тест ловить
/// запис у ЗАКРИТИЙ період: стан чужого відкритого періоду з тим самим ключем
/// пускав рядок закритого проєкту в <c>UPDATE</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class OrphanScannerProjectPeriodTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.13a")]
    public async Task Різні_види_періодів_з_одним_ключем_перевіряються_на_свою_дату()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        var monthly = await builder.BuildAsync(periodKey: 202601, rowCount: 1, ct: CancellationToken.None);
        var yearly = await builder.BuildAsync(periodKey: 202601, rowCount: 1, ct: CancellationToken.None);

        await using var db = Context();

        // Річний період проєкту Y: той самий ключ 202601, але кінець — 31.12.2026.
        await db.Periods
            .Where(p => p.ProjectId == yearly.ProjectId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.PeriodEnd, new DateOnly(2026, 12, 31)),
                CancellationToken.None);

        await OpenAsync(db, monthly.ProjectId, yearly.ProjectId);

        // Запис чинний у січні, але закритий з 1 липня: для місячного звіту він
        // чинний, для річного (31.12) — ні.
        var entry = await EntryAsync(db, new DateOnly(2025, 1, 1), new DateOnly(2026, 7, 1));

        await ReferenceAsync(db, monthly, entry.Id);
        await ReferenceAsync(db, yearly, entry.Id);

        await ScanAsync();

        await using var check = Context();
        Assert.False(await OrphanedAsync(check, monthly));
        Assert.True(await OrphanedAsync(check, yearly));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.13a")]
    public async Task Рядок_закритого_періоду_оцінюється_за_своїм_періодом_а_не_за_чужим_відкритим()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        // Обидва МІСЯЧНІ, той самий ключ: C уже закритий, D відкритий.
        var closed = await builder.BuildAsync(periodKey: 202601, rowCount: 1, ct: CancellationToken.None);
        var open = await builder.BuildAsync(periodKey: 202601, rowCount: 1, ct: CancellationToken.None);

        await using var db = Context();

        await OpenAsync(db, closed.ProjectId, open.ProjectId);
        var closedPeriod = await db.Periods.SingleAsync(
            p => p.ProjectId == closed.ProjectId, CancellationToken.None);
        closedPeriod.TransitionTo(PeriodState.Closed, new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync(CancellationToken.None);

        // ✎ Rule (h4), human 08.11: Closed periods are re-evaluated too. The closed project's row is judged by ITS
        // OWN period (31.01, D1-01): the entry is valid in January for C, not for D's yearly window — so C's row stays
        // valid and untouched, while D's monthly row (entry starting in June) becomes an orphan.
        var entry = await EntryAsync(db, new DateOnly(2025, 6, 1), new DateOnly(2026, 2, 1));
        var lateEntry = await EntryAsync(db, new DateOnly(2026, 6, 1), null, suffix: "L");

        await ReferenceAsync(db, closed, entry.Id);
        await ReferenceAsync(db, open, lateEntry.Id);

        var versionBefore = await RowVersionAsync(db, closed);

        await ScanAsync();

        await using var check = Context();
        Assert.True(await OrphanedAsync(check, open));
        Assert.False(await OrphanedAsync(check, closed));
        Assert.Equal(versionBefore, await RowVersionAsync(check, closed));
    }

    /// <summary>
    /// ✎ Rule (h4): a Closed («Locked») period's row is flagged by its own period when the entry is not valid — the
    /// scan no longer leaves locked months out.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-h4")]
    public async Task Рядок_закритого_періоду_з_нечинним_записом_позначається()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var closed = await builder.BuildAsync(periodKey: 202601, rowCount: 1, ct: CancellationToken.None);

        await using var db = Context();
        await OpenAsync(db, closed.ProjectId);
        var closedPeriod = await db.Periods.SingleAsync(p => p.ProjectId == closed.ProjectId, CancellationToken.None);
        closedPeriod.TransitionTo(PeriodState.Closed, new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync(CancellationToken.None);

        var entry = await EntryAsync(db, new DateOnly(2026, 6, 1), null);
        await ReferenceAsync(db, closed, entry.Id);

        await ScanAsync();

        await using var check = Context();
        Assert.True(await OrphanedAsync(check, closed));
    }

    /// <summary>
    /// B69 (G2): рядок закритого періоду АРХІВОВАНОГО проєкту прохід не чіпає — ані ознаки, ані версії рядка, хоч
    /// запис і нечинний. Контроль у тому самому проході: такий самий рядок неархівованого проєкту позначається.
    /// </summary>
    /// <remarks>
    /// Червоне без фіксу: прибрати умову проєкту в <c>OrphanScanner.NextBatchAsync</c> — рядок архіву стає сиротою
    /// (як у <see cref="Рядок_закритого_періоду_з_нечинним_записом_позначається"/>), версія рядка змінюється.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B69-G2")]
    public async Task Рядок_архівованого_проєкту_прохід_не_чіпає()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var archived = await builder.BuildAsync(periodKey: 202601, rowCount: 1, ct: CancellationToken.None);
        var control = await builder.BuildAsync(periodKey: 202601, rowCount: 1, ct: CancellationToken.None);

        await using var db = Context();
        await OpenAsync(db, archived.ProjectId, control.ProjectId);

        var closedAt = new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
        foreach (var period in await db.Periods
                     .Where(p => p.ProjectId == archived.ProjectId || p.ProjectId == control.ProjectId)
                     .ToListAsync(CancellationToken.None))
        {
            period.TransitionTo(PeriodState.Closed, closedAt);
        }

        var project = await db.Projects.SingleAsync(p => p.Id == archived.ProjectId, CancellationToken.None);
        project.Activate(closedAt);
        project.Archive(closedAt);
        await db.SaveChangesAsync(CancellationToken.None);

        // Запис нечинний у січні для обох — чинний лише з червня.
        var entry = await EntryAsync(db, new DateOnly(2026, 6, 1), null);
        await ReferenceAsync(db, archived, entry.Id);
        await ReferenceAsync(db, control, entry.Id);

        var versionBefore = await RowVersionAsync(db, archived);

        await ScanAsync();

        await using var check = Context();
        Assert.True(await OrphanedAsync(check, control));
        Assert.False(await OrphanedAsync(check, archived));
        Assert.Equal(versionBefore, await RowVersionAsync(check, archived));
    }

    private static async Task OpenAsync(EcrDbContext db, params int[] projectIds)
    {
        foreach (var projectId in projectIds)
        {
            var period = await db.Periods.SingleAsync(p => p.ProjectId == projectId, CancellationToken.None);
            period.TransitionTo(PeriodState.Open, new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc));
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<RegistryEntry> EntryAsync(EcrDbContext db, DateOnly from, DateOnly? to, string suffix = "")
    {
        var def = new Domain.Entities.Configuration.RegistryDef(
            EcrCode.Create($"ORPHPRJ{suffix}_{_tag}"), Text("Orphan project period"), isTemporal: true);
        db.RegistryDefs.Add(def);
        await db.SaveChangesAsync(CancellationToken.None);

        var entry = new RegistryEntry(def.Id, EcrCode.Create($"E_{_tag}"), Text("Entry"), 9, DateTime.UnixEpoch);
        entry.SetValidity(from, to);
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync(CancellationToken.None);

        return entry;
    }

    private static async Task ReferenceAsync(EcrDbContext db, TestDocument doc, long entryId)
    {
        db.CellValues.Add(new CellValue(
            new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[0]),
            doc.TableDefId,
            new CellValueData { ValueRegistryEntryId = entryId }));
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task ScanAsync()
    {
        await using var scan = Context();
        var scanner = new OrphanScanner(
            scan,
            new RegistryResolver(),
            new FixedClock(new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc)),
            new OrphanScanBudget($"orphan-prj-{_tag}", RowBudget: 1_000_000, BatchCells: 20_000, TimeSpan.FromHours(1)));

        await scanner.ScanAllAsync(CancellationToken.None);
    }

    private static Task<bool> OrphanedAsync(EcrDbContext db, TestDocument doc)
        => db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == doc.PeriodKey.Value && r.Id == doc.RowIds[0])
            .Select(r => r.IsOrphaned)
            .SingleAsync(CancellationToken.None);

    private static Task<byte[]> RowVersionAsync(EcrDbContext db, TestDocument doc)
        => db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == doc.PeriodKey.Value && r.Id == doc.RowIds[0])
            .Select(r => r.RowVersion)
            .SingleAsync(CancellationToken.None);

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
