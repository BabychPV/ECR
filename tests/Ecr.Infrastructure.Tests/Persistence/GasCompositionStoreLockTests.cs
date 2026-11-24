// tests/Ecr.Infrastructure.Tests/Persistence/GasCompositionStoreLockTests.cs
using Ecr.Application.GasComposition;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Замкнені періоди складу газу (D-NEW) на справжній базі: актуальний прогін методології, що читає довідник складу, у
/// ЗАКРИТОМУ періоді або на ЗАТВЕРДЖЕНОМУ аркуші — замикає; відкритий і незатверджений — лише залежний (перерахунок).
/// </summary>
/// <remarks>
/// Мутаційні докази (локально, не в коміті): (1) прибрати <c>r.Closed ||</c> у <see cref="GasCompositionStore.ListLockedPeriodsAsync"/> →
/// <c>Закритий_період_замикає</c> червоний; (2) прибрати <c>|| r.Approved</c> → <c>Затверджений_аркуш_замикає_відкритий_період</c>
/// червоний; (3) прибрати <c>run.Status == Current</c> → <c>Застарілий_прогін_не_замикає</c> червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class GasCompositionStoreLockTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task Закритий_період_замикає()
    {
        var f = await ArrangeAsync(PeriodState.Closed);

        var locked = await LockedAsync(f.Ids);

        var period = Assert.Single(locked);
        Assert.Equal(f.PeriodKey, period.PeriodKey);
        Assert.Equal(GasLockedPeriod.Closed, period.Reason);
        Assert.Empty(await DependentsAsync(f.Ids));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task Відкритий_незатверджений_не_замикає_а_дає_рік_перерахунку()
    {
        var f = await ArrangeAsync(PeriodState.Open);

        Assert.Empty(await LockedAsync(f.Ids));
        var dependents = await DependentsAsync(f.Ids);
        Assert.Equal([f.PeriodKey / 100], dependents[f.Ids.Composition]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task Затверджений_аркуш_замикає_відкритий_період()
    {
        var f = await ArrangeAsync(PeriodState.Open, approve: true);

        var period = Assert.Single(await LockedAsync(f.Ids));
        Assert.Equal(GasLockedPeriod.Approved, period.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-476")]
    public async Task Застарілий_прогін_не_замикає()
    {
        var f = await ArrangeAsync(PeriodState.Closed, current: false);

        Assert.Empty(await LockedAsync(f.Ids));
    }

    private async Task<IReadOnlyList<GasLockedPeriod>> LockedAsync(GasRegistryIds ids)
    {
        await using var db = Context();
        return await new GasCompositionStore(db).ListLockedPeriodsAsync(ids, CancellationToken.None);
    }

    private async Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> DependentsAsync(GasRegistryIds ids)
    {
        await using var db = Context();
        return await new GasCompositionStore(db).ListDependentYearsAsync(ids, CancellationToken.None);
    }

    private sealed record Fixture(GasRegistryIds Ids, int PeriodKey);

    /// <summary>Документ, методологія, що читає «склад» (власний довідник тесту), і актуальний прогін за період документа.</summary>
    private async Task<Fixture> ArrangeAsync(PeriodState state, bool approve = false, bool current = true)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await using var db = builder.CreateContext();
        var period = await db.Periods.SingleAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
        period.TransitionTo(PeriodState.Open, Now);
        if (state == PeriodState.Closed)
        {
            period.TransitionTo(PeriodState.Closed, Now);
        }

        var registry = new RegistryDef(EcrCode.Create($"GC{_tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "gc" }), false);
        db.RegistryDefs.Add(registry);
        var methodology = new Methodology(EcrCode.Create($"GM_{_tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();
        db.RegistryUses.Add(RegistryUse.ForMethodologyFormula(version.Id, "F1", registry.Id, "X"));

        var run = new CalculationRun(document.ProjectId, document.PeriodKey.Value, null, Now, document.DocumentId);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();
        run.Complete("Succeeded", Now, null, null);
        if (current)
        {
            run.MakeCurrent();
        }

        if (approve)
        {
            var approval = new ApprovalState(document.DocumentId, document.SheetDefId, document.PeriodKey.Value);
            approval.Submit(1, Now);
            approval.Approve(1, Now);
            db.ApprovalStates.Add(approval);
        }

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        await new CalculationResultStore(db, new TestClock(Now)).WriteResultsAsync(
            run.Id,
            [new CalculationOutput(document.DocumentId, "R-1", [new CalculationOutputValue(version.Id, null, "tons", 1m, unitId)], [])],
            CancellationToken.None);
        await db.SaveChangesAsync();

        // Інші три Id — неіснуючі довідники: залежність тримає лише «склад».
        return new Fixture(new GasRegistryIds(-1, -2, registry.Id, -3), document.PeriodKey.Value);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
