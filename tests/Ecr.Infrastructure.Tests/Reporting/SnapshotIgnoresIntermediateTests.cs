// tests/Ecr.Infrastructure.Tests/Reporting/SnapshotIgnoresIntermediateTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Зріз <c>rpt.*</c> бере лише виходи: проміжні значення видимих формул лежать у тій
/// самій <c>calc.CalculationResult</c>, але <c>ContentHash</c> зрізу від них не змінюється
/// (HSE301 A3a, <c>D-175</c>, V-6; <c>D-53</c>).
/// </summary>
/// <remarks>
/// ⛔ На реальній базі й через справжній <see cref="CalculationResultStore"/>: тест
/// доводить і те, що <c>Kind</c> доходить до рядка, і те, що будівник його фільтрує.
///
/// Мутаційні докази: прибрати умову <c>Kind == Output</c> у
/// <c>ReportSnapshotBuilder.AggregateAsync</c> — суми різні, тест червоний; не передавати
/// <c>Kind</c> у <c>WriteResultsAsync</c> — проміжних у базі нуль (усі лягли виходами),
/// тест червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class SnapshotIgnoresIntermediateTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Той_самий_документ_зі_видимими_формулами_і_без_дає_той_самий_хеш_зрізу()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();

        await using var db = chain.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8];
        var methodology = new Methodology(
            EcrCode.Create($"A3A_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var methodologyVersion = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.Add(methodologyVersion);

        var run = new CalculationRun(document.ProjectId, document.PeriodKey.Value, triggeredByUserId: null, Now);
        run.Complete("Succeeded", Now, "{}", errorMessage: null);
        run.MakeCurrent();
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();

        var unit = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        var store = new CalculationResultStore(db, new TestClock(Now));

        CalculationOutputValue Value(int? substance, string code, decimal value, CalculationResultKind kind)
            => new(methodologyVersion.Id, substance, code, value, unit, kind);

        // Виходи рядка: M_t раз на рядок, tons — на кожну речовину.
        await store.WriteResultsAsync(
            run.Id,
            [
                new CalculationOutput(document.DocumentId, "E-1",
                [
                    Value(null, "M_t", 0.2581914962m, CalculationResultKind.Output),
                    Value(901, "tons", 0.0891735m, CalculationResultKind.Output),
                    Value(902, "tons", 0.1101428m, CalculationResultKind.Output),
                ], []),
            ],
            CancellationToken.None);
        await db.SaveChangesAsync();

        var report = await PublishedAsync(db);
        var builder = new ReportSnapshotBuilder(db, new TestClock(Now), new MemoryCache(new MemoryCacheOptions()));

        var before = await BuildAsync(builder, db, report.Id, document.ProjectId, document.PeriodKey);

        // Той самий прогін, той самий документ — тепер ще й видимі формули.
        await store.WriteResultsAsync(
            run.Id,
            [
                new CalculationOutput(document.DocumentId, "E-1",
                [
                    Value(null, "V_Sm3", 269.258m, CalculationResultKind.Intermediate),
                    Value(901, "W_COMP", 17.5m, CalculationResultKind.Intermediate),
                    Value(902, "W_COMP", 0m, CalculationResultKind.Intermediate),
                ], []),
            ],
            CancellationToken.None);
        await db.SaveChangesAsync();

        // Не порожняк: проміжні справді в базі, з видом Intermediate, і читання сітки їх бачить.
        Assert.Equal(
            3,
            await db.CalculationResults.AsNoTracking().CountAsync(
                r => r.CalculationRunId == run.Id && r.Kind == CalculationResultKind.Intermediate));
        Assert.Equal(6, (await store.ReadCurrentAsync(document.DocumentId, document.PeriodKey.Value, default)).Count);

        var after = await BuildAsync(builder, db, report.Id, document.ProjectId, document.PeriodKey);

        // `RowCount` зрізу рахує КОМІРКИ: три виходи × чотири колонки опису.
        Assert.Equal(3 * 4, before.RowCount);
        Assert.Equal(before.RowCount, after.RowCount);
        Assert.Equal(before.Hash, after.Hash);
    }

    private static async Task<(int RowCount, string Hash)> BuildAsync(
        ReportSnapshotBuilder builder, EcrDbContext db, int reportVersionId, int projectId, PeriodKey periodKey)
    {
        var id = await builder.BuildAsync(reportVersionId, projectId, periodKey, parametersJson: null, CancellationToken.None);

        var snapshot = await db.ReportSnapshots.AsNoTracking().SingleAsync(s => s.Id == id);
        return (snapshot.RowCount, Convert.ToHexString(snapshot.ContentHash!));
    }

    private static async Task<ReportVersion> PublishedAsync(EcrDbContext db)
    {
        var def = new ReportDef(
            EcrCode.Create($"RPI{Guid.NewGuid().ToString("N")[..8]}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Intermediate filter test" }),
            isRegulatory: true);
        db.ReportDefs.Add(def);
        await db.SaveChangesAsync();

        var version = new ReportVersion(
            def.Id,
            "1.0",
            """
            [{"code":"RowKey","kind":"text"},{"code":"OutputCode","kind":"text"},
             {"code":"Value","kind":"number"},{"code":"SubstanceEntryId","kind":"number"}]
            """,
            """{"rowSource":"CalculationResults"}""",
            Now);
        version.Publish();
        db.ReportVersions.Add(version);
        await db.SaveChangesAsync();

        return version;
    }
}
