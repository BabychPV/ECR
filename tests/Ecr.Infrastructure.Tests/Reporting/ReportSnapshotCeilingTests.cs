// tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotCeilingTests.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Reporting;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// AN-120 / L1-01: регуляторний зріз не буває мовчки неповним. Понад стелю —
/// відмова (<c>ECR-RPT-0422</c>), а попередній зріз лишається чинним; правила
/// відбору (<c>R5</c>) бачать УСЕ джерело, а стеля стоїть на їхньому виході.
/// </summary>
/// <remarks>
/// Стеля задається <see cref="ReportSnapshotBuilder.RowCeiling"/> = 10: довести
/// те саме на 200 001 рядку означало б годину наповнення бази, а механізм той
/// самий.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportSnapshotCeilingTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    private const int Ceiling = 10;

    private static readonly ReportColumnCommand[] Columns =
    [
        new("RowKey", "text"),
        new("OutputCode", "text"),
        new("Value", "number"),
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L1-01")]
    public async Task Понад_стелю_відмовляє_а_не_обрізає_і_попередній_зріз_лишається_чинним()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = chain.CreateContext();
        var seeded = await SeedResultsAsync(chain, db, [.. Enumerable.Range(1, 12).Select(n => ($"r{n:00}", "E_CO2"))]);
        var version = await PublishedAsync(db, ReportDefinitionSpec.RulesJson(new("CalculationResults"), Columns));

        // Попередній зріз тієї самої версії, проєкту й періоду — без стелі тесту.
        var previous = await Builder(db).BuildAsync(
            version.Id, seeded.ProjectId, seeded.PeriodKey, null, CancellationToken.None);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Builder(db, Ceiling).BuildAsync(
                version.Id, seeded.ProjectId, seeded.PeriodKey, null, CancellationToken.None));

        Assert.Equal(ErrorCodes.ReportInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-RPT-0422.snapshotTooLarge", error.Details!["messageKey"]);
        Assert.Equal("10", error.Details["limit"]);

        // ⛔ Мутаційний доказ: поверни `query.Take(MaxRows)` без відмови — тут
        // з'явиться другий зріз із 10 рядками з 12, `Complete` і `IsCurrent`.
        await using var check = chain.CreateContext();
        var snapshots = await check.ReportSnapshots.AsNoTracking()
            .Where(s => s.ReportVersionId == version.Id)
            .ToListAsync();

        var only = Assert.Single(snapshots);
        Assert.Equal(previous, only.Id);
        Assert.True(only.IsCurrent);
        Assert.Equal(12, await RowsOf(check, previous).Select(r => r.RowNo).Distinct().CountAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L1-01")]
    public async Task Правило_відбору_діє_до_стелі_а_не_після_обрізаного_джерела()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = chain.CreateContext();

        // 12 результатів джерела; потрібні лише 3 ОСТАННІ в порядку побудови
        // (`DocumentId, SourceRowKey, OutputCode`).
        var seeded = await SeedResultsAsync(
            chain,
            db,
            [.. Enumerable.Range(1, 12).Select(n => ($"r{n:00}", n <= 9 ? "E_DROP" : "E_KEEP"))]);

        var version = await PublishedAsync(db, ReportDefinitionSpec.RulesJson(
            new("CalculationResults", Rules: [new("[OutputCode] = 'E_DROP'", new(HideRow: true))]), Columns));

        var snapshotId = await Builder(db, Ceiling).BuildAsync(
            version.Id, seeded.ProjectId, seeded.PeriodKey, null, CancellationToken.None);

        await using var check = chain.CreateContext();

        // ⛔ Мутаційний доказ: поверни `Take(MaxRows)` ДО правил — джерело
        // обріжеться до r01…r10, і в зрізі лишиться один рядок (r10) замість трьох.
        var rowKeys = await RowsOf(check, snapshotId)
            .Where(r => r.ColumnCode == "RowKey")
            .OrderBy(r => r.RowNo)
            .Select(r => r.ValueString)
            .ToListAsync();

        Assert.Equal(["r10", "r11", "r12"], rowKeys);
    }

    private static IQueryable<ReportRow> RowsOf(EcrDbContext db, long snapshotId)
        => db.ReportRows.AsNoTracking().Where(r => r.SnapshotId == snapshotId);

    private static ReportSnapshotBuilder Builder(EcrDbContext db, int? ceiling = null)
    {
        var memory = new MemoryCache(new MemoryCacheOptions());

        return ceiling is { } limit
            ? new ReportSnapshotBuilder(db, new TestClock(Now), memory) { RowCeiling = limit }
            : new ReportSnapshotBuilder(db, new TestClock(Now), memory);
    }

    private sealed record Seeded(int ProjectId, PeriodKey PeriodKey);

    /// <summary>Чинний прогін документа з результатами (як у <c>ReportSnapshotLayoutCacheTests</c>).</summary>
    private static async Task<Seeded> SeedResultsAsync(
        TestDocumentBuilder chain, EcrDbContext db, IReadOnlyList<(string RowKey, string Output)> results)
    {
        var document = await chain.BuildAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];

        var methodology = new Methodology(
            EcrCode.Create($"RPTL_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var methodologyVersion = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.Add(methodologyVersion);

        var run = new CalculationRun(document.ProjectId, document.PeriodKey.Value, triggeredByUserId: null, Now);
        run.Complete("Succeeded", Now, "{}", errorMessage: null);
        run.MakeCurrent();
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();

        var unit = await db.Units.AsNoTracking().OrderBy(u => u.Id).FirstAsync();
        var value = 1;

        foreach (var (rowKey, output) in results)
        {
            var text = (value++).ToString(CultureInfo.InvariantCulture);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO calc.CalculationResult
                    (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
                VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {methodologyVersion.Id},
                        {document.PeriodKey.Value}, {document.DocumentId}, {rowKey}, {output},
                        CAST({text} AS decimal(34,16)), {unit.Id})
                """);
        }

        return new Seeded(document.ProjectId, document.PeriodKey);
    }

    private static async Task<ReportVersion> PublishedAsync(EcrDbContext db, string rulesJson)
    {
        var def = new ReportDef(
            EcrCode.Create($"RCL{Guid.NewGuid().ToString("N")[..8]}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Snapshot ceiling test" }),
            isRegulatory: true);
        db.ReportDefs.Add(def);
        await db.SaveChangesAsync();

        var version = new ReportVersion(def.Id, "1.0", ReportDefinitionSpec.ColumnsJson(Columns), rulesJson, Now);
        version.Publish();
        db.ReportVersions.Add(version);
        await db.SaveChangesAsync();

        return version;
    }
}
