using Ecr.Application.Ports;
using Ecr.Calculations;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// B85 (хвіст B13): запис результатів прогону понад <see cref="ICalculationResultStore.BulkWriteThreshold"/> іде
/// <c>SqlBulkCopy</c> і дає РІВНО ті самі рядки, що й EF-шлях: усі колонки результату, прив'язка кроків трейсу до
/// результатів, атомарність разом із трейсом.
/// </summary>
/// <remarks>
/// Той самий сценарій (322 результати — межу перетнуто) пишеться двічі через <see cref="CalculationOutputWriter"/>:
/// сховищем із типовою межею (масовий шлях) і з межею <c>int.MaxValue</c> (EF-шлях). Порівнюється все, крім Id.
/// <para>
/// ⛔ МУТАЦІЇ (червоні локально, не закомічені): у <c>CalculationResultStore.Bulk.cs</c> писати
/// <c>TableInstanceId</c> як <c>DBNull</c> — червоний <see cref="Масовий_запис_дає_ті_самі_рядки_що_й_EF"/>; не
/// зливати <c>bulkPending</c> у <c>PendingResults</c> — червоний той самий тест (кроки без результату); прибрати
/// <c>SqlBulkCopyOptions.CheckConstraints</c> — червоний <see cref="Порушення_FK_у_масовому_записі_не_лишає_нічого"/>.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationResultBulkWriteDbTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    private const int Rows = 161;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Масовий_запис_дає_ті_самі_рядки_що_й_EF()
    {
        var f = await ArrangeAsync();
        var outputs = Outputs(f, f.UnitId);
        Assert.True(outputs.Sum(o => o.Values.Count) > ICalculationResultStore.BulkWriteThreshold);

        var bulkRun = await StartRunAsync(f);
        await WriteAsync(f, bulkRun, outputs, bulkThreshold: null);

        var efRun = await StartRunAsync(f);
        await WriteAsync(f, efRun, outputs, bulkThreshold: int.MaxValue);

        await using var db = f.Builder.CreateContext();
        var bulk = await ShapeAsync(db, bulkRun);
        var ef = await ShapeAsync(db, efRun);

        Assert.Equal(Rows * 2, bulk.Results.Count);
        Assert.Equal(Rows * 3, bulk.Steps.Count);
        Assert.Equal(ef.Results, bulk.Results);
        Assert.Equal(ef.Steps, bulk.Steps);
        Assert.DoesNotContain(bulk.Steps, s => s.EndsWith("|noresult", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Масовий_шлях_справді_оминає_трекер()
    {
        var f = await ArrangeAsync();
        var outputs = Outputs(f, f.UnitId);
        var run = await StartRunAsync(f);

        await using var db = f.Builder.CreateContext();
        var store = new CalculationResultStore(db, new TestClock(Now));
        await new UnitOfWork(db, new TestClock(Now)).ExecuteInTransactionAsync(
            async ct =>
            {
                await store.WriteResultsAsync(run, outputs, ct);

                // Масовий шлях: рядки вже в базі (у транзакції), а не в трекері змін.
                Assert.Empty(db.ChangeTracker.Entries<CalculationResult>());
                Assert.Equal(Rows * 2, await db.CalculationResults.CountAsync(r => r.CalculationRunId == run, ct));
            },
            CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Порушення_FK_у_масовому_записі_не_лишає_нічого()
    {
        var f = await ArrangeAsync();
        var outputs = Outputs(f, f.UnitId);
        outputs[^1] = outputs[^1] with
        {
            Values = [new CalculationOutputValue(f.VersionId, null, "BAD", 1m, UnitId: int.MaxValue)],
        };

        var run = await StartRunAsync(f);
        await Assert.ThrowsAnyAsync<Exception>(() => WriteAsync(f, run, outputs, bulkThreshold: null));

        await using var db = f.Builder.CreateContext();
        Assert.Equal(0, await db.CalculationResults.AsNoTracking().CountAsync(r => r.CalculationRunId == run));
        Assert.Equal(0, await db.CalculationSteps.AsNoTracking().CountAsync(s => s.CalculationRunId == run));
    }

    private static async Task WriteAsync(Fixture f, long runId, List<CalculationOutput> outputs, int? bulkThreshold)
    {
        await using var db = f.Builder.CreateContext();
        var writer = new CalculationOutputWriter(
            new CalculationResultStore(db, new TestClock(Now), bulkThreshold: bulkThreshold),
            new UnitOfWork(db, new TestClock(Now)));

        await writer.WriteAsync(runId, outputs, TraceLevel.Full, CancellationToken.None);
    }

    /// <summary>Усі колонки результату, крім Id; кроки — з адресою і КОНТЕНТОМ свого результату.</summary>
    private static async Task<(List<string> Results, List<string> Steps)> ShapeAsync(EcrDbContext db, long runId)
    {
        var results = await db.CalculationResults.AsNoTracking()
            .Where(r => r.CalculationRunId == runId)
            .ToListAsync();
        var byId = results.ToDictionary(r => r.Id);
        var steps = await db.CalculationSteps.AsNoTracking()
            .Where(s => s.CalculationRunId == runId)
            .ToListAsync();

        static string Row(CalculationResult r) =>
            $"{r.PeriodKey}|{r.MethodologyVersionId}|{r.DocumentId}|{r.TableInstanceId}|{r.SourceRowKey}|{r.OutputCode}|"
            + $"{r.SubstanceEntryId}|{r.Kind}|{r.UnitId}|{r.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        return (
            [.. results.Select(Row).Order(StringComparer.Ordinal)],
            [.. steps.Select(s =>
                    $"{s.SourceRowKey}|{s.TableInstanceId}|{s.StepOrder}|{s.StepCode}|{s.SubstanceEntryId}|"
                    + (s.ResultId is { } id && byId.TryGetValue(id, out var r) ? Row(r) : "noresult"))
                .Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// 161 рядок × 2 значення = 322 результати; значення з понад 16 знаками після коми (обидва шляхи мають однаково
    /// звести їх до decimal(34,16)), речовина, екземпляр таблиці, проміжний вид.
    /// </summary>
    private static List<CalculationOutput> Outputs(Fixture f, int unitId)
        => [.. Enumerable.Range(1, Rows).Select(i => new CalculationOutput(
            f.DocumentId,
            $"R{i:000}",
            [
                new CalculationOutputValue(f.VersionId, null, "A", i / 3m, unitId),
                new CalculationOutputValue(
                    f.VersionId, 900 + (i % 3), "B", -i * 2.5m, unitId,
                    i % 2 == 0 ? CalculationResultKind.Intermediate : CalculationResultKind.Output),
            ],
            [
                new CalculationTraceStep(1, "A", "x/3", i / 3m, TraceJson: null),
                new CalculationTraceStep(2, "B", "x*2.5", -i * 2.5m, TraceJson: null, SubstanceEntryId: 900 + (i % 3)),
                new CalculationTraceStep(3, "C", "x", i, TraceJson: null),
            ])
        {
            TableInstanceId = 7_000 + (i % 2),
        })];

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
        var document = await builder.BuildAsync(periodKey: 209911);
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var methodology = new Methodology(
            EcrCode.Create($"B85_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
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
