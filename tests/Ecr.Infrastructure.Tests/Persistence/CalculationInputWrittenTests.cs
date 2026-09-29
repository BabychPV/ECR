// tests/Ecr.Infrastructure.Tests/Persistence/CalculationInputWrittenTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Трейс прив'язаний до результату й рядка (HSE301 A3b, ФВ-9.13, дефект Д-5;
/// FEATURE-HSE301-VIEW §7.1) — на реальному SQL Server через справжній
/// <see cref="CalculationResultStore"/>.
/// </summary>
/// <remarks>
/// ⛔ До кроку <c>WriteTraceAsync</c> писав <c>resultId: null</c> і не знав ні документа, ні
/// рядка: трейс знаходився лише за прогоном, тобто з комірки — ніяк.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationInputWrittenTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);

    private const string Row = "E-2026-01-001";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.13")]
    public async Task Крок_має_результат_і_адресу()
    {
        var stand = await StandAsync();
        var output = stand.Output();

        await using (var db = stand.Chain.CreateContext())
        {
            var store = new CalculationResultStore(db, new TestClock(Now));
            await store.WriteResultsAsync(stand.RunId, [output], CancellationToken.None);
            await store.WriteTraceAsync(stand.RunId, [output], TraceLevel.ErrorsOnly, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var check = stand.Chain.CreateContext();
        var results = await check.CalculationResults.AsNoTracking()
            .Where(r => r.CalculationRunId == stand.RunId)
            .ToListAsync();
        var steps = await check.CalculationSteps.AsNoTracking()
            .Where(s => s.CalculationRunId == stand.RunId)
            .ToListAsync();

        // M_t прикладу A — рівень рядка, без речовини.
        var mass = Assert.Single(steps, s => s.StepCode == "M_t");
        Assert.NotNull(mass.ResultId);
        Assert.Equal(Assert.Single(results, r => r.OutputCode == "M_t").Id, mass.ResultId);
        Assert.Equal(stand.DocumentId, mass.DocumentId);
        Assert.Equal(Row, mass.SourceRowKey);
        Assert.Null(mass.SubstanceEntryId);
        Assert.Contains("\"inputs\"", mass.TraceJson, StringComparison.Ordinal);

        // tons[902] — свій результат, а не результат першої речовини рядка.
        var tons = Assert.Single(steps, s => s.StepCode == "tons" && s.SubstanceEntryId == 902);
        Assert.Equal(
            Assert.Single(results, r => r.OutputCode == "tons" && r.SubstanceEntryId == 902).Id,
            tons.ResultId);

        // Проміжна формула без рядка результату — крок є, результату немає.
        var volume = Assert.Single(steps, s => s.StepCode == "V_Sm3");
        Assert.Null(volume.ResultId);
        Assert.Equal(Row, volume.SourceRowKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.13")]
    public async Task Off_не_пише_нічого()
    {
        var stand = await StandAsync();
        var output = stand.Output();

        await using (var db = stand.Chain.CreateContext())
        {
            var store = new CalculationResultStore(db, new TestClock(Now));
            await store.WriteResultsAsync(stand.RunId, [output], CancellationToken.None);
            await store.WriteTraceAsync(stand.RunId, [output], TraceLevel.Off, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var check = stand.Chain.CreateContext();

        // Не порожняк: результати прогону записано, мовчить лише трейс.
        Assert.NotEqual(0, await check.CalculationResults.CountAsync(r => r.CalculationRunId == stand.RunId));
        Assert.Equal(0, await check.CalculationSteps.CountAsync(s => s.CalculationRunId == stand.RunId));
    }

    private async Task<Stand> StandAsync()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();

        await using var db = chain.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8];
        var methodology = new Methodology(
            EcrCode.Create($"A3B_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.Add(version);

        var run = new CalculationRun(document.ProjectId, document.PeriodKey.Value, triggeredByUserId: null, Now);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();

        var unit = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

        return new Stand(chain, document.DocumentId, run.Id, version.Id, unit);
    }

    /// <summary>Прогін документа з однією методологією на кшталт 301.</summary>
    private sealed record Stand(TestDocumentBuilder Chain, long DocumentId, long RunId, int VersionId, int UnitId)
    {
        /// <summary>Рядок прикладу A: <c>V_Sm3</c> (невидима), <c>M_t</c> і <c>tons</c> двох речовин.</summary>
        public CalculationOutput Output() => new(
            DocumentId,
            Row,
            [
                new CalculationOutputValue(VersionId, null, "M_t", 0.2581914962m, UnitId),
                new CalculationOutputValue(VersionId, 901, "tons", 0.0891735m, UnitId),
                new CalculationOutputValue(VersionId, 902, "tons", 0.1101428m, UnitId),
            ],
            [
                new CalculationTraceStep(1, "V_Sm3", "@Volume", 269.258m, null, Detail: Json("@Volume", "Volume")),
                new CalculationTraceStep(
                    2, "M_t", "CONVERT(!V_Sm3 * @Rho20, 'kg', 't')", 0.2581914962m, null,
                    Detail: Json("CONVERT(!V_Sm3 * @Rho20, 'kg', 't')", "Rho20")),
                new CalculationTraceStep(3, "tons", "!M_t * CST.K", 0.0891735m, null, SubstanceEntryId: 901),
                new CalculationTraceStep(4, "tons", "!M_t * CST.K", 0.1101428m, null, SubstanceEntryId: 902),
            ]);

        private static string Json(string expression, string argument)
            => $$"""{"v":1,"expr":"{{expression}}","inputs":[{"kind":"arg","code":"{{argument}}"}]}""";
    }
}
