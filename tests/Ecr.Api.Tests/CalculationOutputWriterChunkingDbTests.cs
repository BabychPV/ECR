using Ecr.Application.Ports;
using Ecr.Calculations;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>CalculationOutputWriter</c> з чанками <c>SaveChanges</c> на справжній базі: запис чанками ідентичний
/// запису одним <c>SaveChanges</c> (результати, кроки, прив'язка кроку до результату), а збій у пізнішому чанку
/// не лишає в базі нічого — методологія пишеться атомарно (одна транзакція).
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЯ: прибрати <c>ExecuteInTransactionAsync</c> навколо чанків у <c>CalculationOutputWriter</c> —
/// <see cref="Збій_у_пізнішому_чанку_не_лишає_напівзаписаної_методології"/> червоніє (рядки перших чанків
/// лишаються в базі). Тест живе в Api.Tests: лише там одночасно видно Calculations, Infrastructure і SQL Server.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationOutputWriterChunkingDbTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Запис_чанками_дає_ті_самі_рядки_що_й_один_SaveChanges()
    {
        var f = await ArrangeAsync();
        var outputs = Outputs(f, count: 12, unitId: f.UnitId);

        var chunkedRun = await StartRunAsync(f);
        await WriteAsync(f, chunkedRun, outputs, maxRows: 7);

        var wholeRun = await StartRunAsync(f);
        await WriteAsync(f, wholeRun, outputs, maxRows: CalculationOutputWriter.DefaultMaxRowsPerSave);

        await using var db = f.Builder.CreateContext();
        var chunked = await ShapeAsync(db, chunkedRun);
        var whole = await ShapeAsync(db, wholeRun);

        Assert.Equal(12 * 2, chunked.Results.Count);
        Assert.Equal(12 * 3, chunked.Steps.Count);
        Assert.Equal(whole.Results, chunked.Results);
        Assert.Equal(whole.Steps, chunked.Steps);
        Assert.DoesNotContain(chunked.Steps, s => s.EndsWith("|noresult", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збій_у_пізнішому_чанку_не_лишає_напівзаписаної_методології()
    {
        var f = await ArrangeAsync();
        var outputs = Outputs(f, count: 12, unitId: f.UnitId);

        // Вихід №10 (пізніший чанк) посилається на неіснуючу одиницю: порушення FK на SaveChanges того чанка.
        outputs[9] = outputs[9] with
        {
            Values = [new CalculationOutputValue(f.VersionId, null, "BAD", 1m, UnitId: int.MaxValue)],
        };

        var run = await StartRunAsync(f);
        await Assert.ThrowsAnyAsync<Exception>(() => WriteAsync(f, run, outputs, maxRows: 7));

        await using var db = f.Builder.CreateContext();
        Assert.Equal(0, await db.CalculationResults.AsNoTracking().CountAsync(r => r.CalculationRunId == run));
        Assert.Equal(0, await db.CalculationSteps.AsNoTracking().CountAsync(s => s.CalculationRunId == run));
    }

    private static async Task WriteAsync(Fixture f, long runId, List<CalculationOutput> outputs, int maxRows)
    {
        await using var db = f.Builder.CreateContext();
        var writer = new CalculationOutputWriter(
            new CalculationResultStore(db, new TestClock(Now)), new UnitOfWork(db, new TestClock(Now)))
        {
            MaxRowsPerSave = maxRows,
        };

        await writer.WriteAsync(runId, outputs, TraceLevel.Full, CancellationToken.None);
    }

    /// <summary>Форма записаного: результати як <c>рядок|код|значення</c>, кроки як <c>рядок|код|є/немає результату</c>.</summary>
    private static async Task<(List<string> Results, List<string> Steps)> ShapeAsync(EcrDbContext db, long runId)
    {
        var results = await db.CalculationResults.AsNoTracking()
            .Where(r => r.CalculationRunId == runId)
            .Select(r => new { r.SourceRowKey, r.OutputCode, r.Value })
            .ToListAsync();
        var steps = await db.CalculationSteps.AsNoTracking()
            .Where(s => s.CalculationRunId == runId)
            .Select(s => new { s.SourceRowKey, s.StepCode, s.StepOrder, HasResult = s.ResultId != null })
            .ToListAsync();

        return (
            [.. results.Select(r => $"{r.SourceRowKey}|{r.OutputCode}|{r.Value:0.####}").Order(StringComparer.Ordinal)],
            [.. steps.Select(s => $"{s.SourceRowKey}|{s.StepOrder}|{s.StepCode}|{(s.HasResult ? "result" : "noresult")}")
                .Order(StringComparer.Ordinal)]);
    }

    private static List<CalculationOutput> Outputs(Fixture f, int count, int unitId)
        => [.. Enumerable.Range(1, count).Select(i => new CalculationOutput(
            f.DocumentId,
            $"R{i:000}",
            [
                new CalculationOutputValue(f.VersionId, null, "A", i, unitId),
                new CalculationOutputValue(f.VersionId, null, "B", i * 2m, unitId),
            ],
            [
                new CalculationTraceStep(1, "A", "x", i, TraceJson: null),
                new CalculationTraceStep(2, "B", "x*2", i * 2m, TraceJson: null),
                new CalculationTraceStep(3, "A", "x", i, TraceJson: null),
            ]))];

    private static async Task<long> StartRunAsync(Fixture f)
    {
        await using var db = f.Builder.CreateContext();
        var run = new CalculationRun(f.ProjectId, f.PeriodKey, triggeredByUserId: null, Now);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private async Task<Fixture> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var methodology = new Methodology(
            EcrCode.Create($"CW_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

        return new Fixture(builder, document.DocumentId, document.ProjectId, document.PeriodKey.Value, version.Id, unitId);
    }

    private sealed record Fixture(
        TestDocumentBuilder Builder, long DocumentId, int ProjectId, int PeriodKey, int VersionId, int UnitId);
}
