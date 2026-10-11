// tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotBuildRobustnessTests.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Reporting;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Побудова зрізу: відмова після вставки не лишає «зрізу-сироти» (Y1-04), а запис рядків не тримає
/// у трекері змін увесь зріз (Z5-03 / L1-05).
/// </summary>
[Collection("SqlServer")]
public sealed class ReportSnapshotBuildRobustnessTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    private static readonly ReportColumnCommand[] Columns =
    [
        new("RowKey", "text"),
        new("OutputCode", "text"),
        new("Value", "number"),
    ];

    /// <remarks>
    /// Y1-04 (аудит R11): слот зайнятий (замок не взято за <c>SlotLockTimeoutMs</c>) — 409 «зайнято», а
    /// щойно вставлений непоточний зріз із рядками прибирається одразу, а не лишається до нічної
    /// ретенції. Мутація: прибрати `catch` з `DiscardOrphanAsync` — лишається зріз і його рядки, червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "Y1-04")]
    public async Task Відмова_слоту_зайнятий_не_лишає_зрізу_сироти()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = chain.CreateContext();
        var seeded = await SeedResultsAsync(chain, db, [("r01", "E_CO2"), ("r02", "E_CO2")]);
        var version = await PublishedAsync(db, ReportDefinitionSpec.RulesJson(new("CalculationResults"), Columns));

        // Інше підключення тримає замок слоту (проєкт × період) до кінця своєї транзакції.
        await using var holder = new SqlConnection(sql.ConnectionString);
        await holder.OpenAsync();
        await using var holderTransaction = (SqlTransaction)await holder.BeginTransactionAsync();
        await using (var command = holder.CreateCommand())
        {
            command.Transaction = holderTransaction;
            command.CommandText =
                "DECLARE @rc int; EXEC @rc = sp_getapplock @Resource = @res, @LockMode = N'Exclusive', "
                + "@LockOwner = N'Transaction', @LockTimeout = 0; SELECT @rc;";
            command.Parameters.AddWithValue(
                "@res", string.Create(CultureInfo.InvariantCulture, $"ecr.rpt-slot.{seeded.ProjectId}.{seeded.PeriodKey.Value}"));
            Assert.True((int)(await command.ExecuteScalarAsync())! >= 0, "Не вдалося взяти замок слоту для сценарію.");
        }

        var builder = new ReportSnapshotBuilder(db, new TestClock(Now), new MemoryCache(new MemoryCacheOptions()))
        {
            SlotLockTimeoutMs = 300,
        };

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => builder.BuildAsync(version.Id, seeded.ProjectId, seeded.PeriodKey, null, CancellationToken.None));

        await holderTransaction.RollbackAsync();

        await using var check = chain.CreateContext();
        var left = await check.ReportSnapshots.AsNoTracking()
            .Where(s => s.ReportVersionId == version.Id).Select(s => s.Id).ToListAsync();
        Assert.Empty(left);
        Assert.Equal(0, await check.ReportRows.AsNoTracking().CountAsync(r => left.Contains(r.SnapshotId)));
    }

    /// <remarks>
    /// Z5-03 / L1-05 (аудит R11): комірки зрізу зберігаються порціями й відчіплюються від трекера —
    /// на кожному <c>SaveChanges</c> трекер тримає не більше порції, а сума збереженого вмісту
    /// збігається з порахованою (порціювання не змінило нічого, окрім пам'яті). Мутація: повернути
    /// один `AddRange` на всі рядки — на `SaveChanges` у трекері 36 комірок замість ≤ 10, червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "Z5-03")]
    public async Task Рядки_зрізу_зберігаються_порціями_і_не_накопичуються_в_трекері()
    {
        const int Chunk = 10;

        var chain = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = chain.CreateContext();
        var seeded = await SeedResultsAsync(chain, db, [.. Enumerable.Range(1, 12).Select(n => ($"r{n:00}", "E_CO2"))]);
        var version = await PublishedAsync(db, ReportDefinitionSpec.RulesJson(new("CalculationResults"), Columns));

        var maxTracked = 0;
        db.SavingChanges += (_, _) => maxTracked = Math.Max(maxTracked, db.ChangeTracker.Entries<ReportRow>().Count());

        var builder = new ReportSnapshotBuilder(db, new TestClock(Now), new MemoryCache(new MemoryCacheOptions()))
        {
            RowSaveChunkSize = Chunk,
        };

        var snapshotId = await builder.BuildAsync(version.Id, seeded.ProjectId, seeded.PeriodKey, null, CancellationToken.None);

        Assert.True(maxTracked <= Chunk, $"Трекер тримав {maxTracked} комірок зрізу при порції {Chunk}.");
        Assert.Empty(db.ChangeTracker.Entries<ReportRow>());

        // 12 рядків × 3 колонки; збережений вміст дає ту саму суму, що порахована при побудові.
        await using var check = chain.CreateContext();
        Assert.Equal(36, await check.ReportRows.AsNoTracking().CountAsync(r => r.SnapshotId == snapshotId));

        var hashes = await builder.VerifyAsync(snapshotId, CancellationToken.None);
        Assert.NotNull(hashes);
        Assert.Equal(hashes!.Stored, hashes.Actual);
    }

    private sealed record Seeded(int ProjectId, PeriodKey PeriodKey);

    /// <summary>Чинний прогін документа з результатами (як у <c>ReportSnapshotCeilingTests</c>).</summary>
    private static async Task<Seeded> SeedResultsAsync(
        TestDocumentBuilder chain, EcrDbContext db, IReadOnlyList<(string RowKey, string Output)> results)
    {
        var document = await chain.BuildAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];

        var methodology = new Methodology(
            EcrCode.Create($"RBR_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
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
            EcrCode.Create($"RBR{Guid.NewGuid().ToString("N")[..8]}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Snapshot robustness test" }),
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
