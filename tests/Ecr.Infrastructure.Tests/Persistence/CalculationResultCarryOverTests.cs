using Ecr.Application.Errors;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Перенос результатів у новий прогін (<see cref="CalculationResultStore.CarryOverResultsAsync"/>) на справжній базі:
/// N2-04 — стеля без мовчазного обрізання.
/// </summary>
/// <remarks>
/// Мутаційні докази: повернути <c>Take(MaxResults)</c> замість <c>CountAsync</c> і відмови —
/// <see cref="Понад_стелю_відмова_а_не_обрізання"/> червоніє (відмови немає, рядки обрізано).
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationResultCarryOverTests(SqlServerFixture sql)
{
    private static readonly DateTime Start = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Понад_стелю_відмова_а_не_обрізання()
    {
        var f = await ArrangeAsync();
        await using var db = f.Builder.CreateContext();

        // Одним set-based INSERT: 50 001 рядок через EF тут тривав би хвилини.
        await InsertResultsAsync(db, f, f.SourceRunId, CalculationResultStore.CarryOverMaxResults + 1);

        var target = await StartRunAsync(db, f, Start.AddHours(1));
        var store = new CalculationResultStore(db, new TestClock(Start.AddHours(1)));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => store.CarryOverResultsAsync(target.Id, f.Document.DocumentId, [f.MethodologyId], CancellationToken.None));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0422.carryOverTooLarge", error.Details!["messageKey"]);
        Assert.Equal(CalculationResultStore.CarryOverMaxResults + 1, error.Details["count"]);
        Assert.Equal(CalculationResultStore.CarryOverMaxResults, error.Details["max"]);

        // Нічого не перенесено — ні частини: прогін упаде цілком, а попередній лишиться актуальним.
        await db.SaveChangesAsync();
        Assert.Equal(0, await db.CalculationResults.AsNoTracking().CountAsync(r => r.CalculationRunId == target.Id));
        Assert.Equal(
            CalculationRun.CurrentStatus,
            await db.CalculationRuns.AsNoTracking().Where(r => r.Id == f.SourceRunId).Select(r => r.Status).SingleAsync());
    }

    private sealed record Fixture(
        TestDocumentBuilder Builder, TestDocument Document, int MethodologyId, int VersionId, int UnitId, long SourceRunId);

    /// <summary>Документ, методологія з версією й актуальний прогін-джерело (без результатів).</summary>
    private async Task<Fixture> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var methodology = new Methodology(EcrCode.Create($"CO_{tag}"), Name("m"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Start);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        var source = new CalculationRun(document.ProjectId, document.PeriodKey.Value, triggeredByUserId: null, Start);
        source.Complete("Succeeded", Start.AddMinutes(1), "{}", errorMessage: null);
        source.MakeCurrent();
        db.CalculationRuns.Add(source);
        await db.SaveChangesAsync();

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

        return new Fixture(builder, document, methodology.Id, version.Id, unitId, source.Id);
    }

    /// <summary>Новий прогін (ще не актуальний) для того самого проєкту й періоду.</summary>
    private static async Task<CalculationRun> StartRunAsync(EcrDbContext db, Fixture f, DateTime startedAt)
    {
        var run = new CalculationRun(f.Document.ProjectId, f.Document.PeriodKey.Value, triggeredByUserId: null, startedAt);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    private static async Task InsertResultsAsync(EcrDbContext db, Fixture f, long runId, int count)
    {
        var period = f.Document.PeriodKey.Value;
        var document = f.Document.DocumentId;

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @first sql_variant;
            EXEC sys.sp_sequence_get_range
                @sequence_name = N'calc.CalculationResultSeq',
                @range_size = {count},
                @range_first_value = @first OUTPUT;

            INSERT INTO calc.CalculationResult
                (Id, PeriodKey, CalculationRunId, MethodologyVersionId, DocumentId, SourceRowKey, OutputCode, Value, UnitId, Kind)
            SELECT CONVERT(bigint, @first) + n.Num - 1, {period}, {runId}, {f.VersionId}, {document},
                   CONCAT(N'R-', n.Num), N'EMISSION', 1, {f.UnitId}, 0
              FROM (SELECT TOP ({count}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS Num
                      FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS n;
            """);
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
