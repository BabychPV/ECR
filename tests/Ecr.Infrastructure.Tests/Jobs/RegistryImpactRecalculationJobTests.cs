// tests/Ecr.Infrastructure.Tests/Jobs/RegistryImpactRecalculationJobTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Батьківська задача перерахунку зачеплених (RT-25) на справжній базі: розкладає набір на
/// <see cref="ICalculationTrigger"/> лише для відкритих періодів і лише для названих документів.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати <c>wanted.Contains</c> → <see cref="Тригер_лише_для_названих_документів"/> червоний;
/// набір з payload без звірки зі сховищем → <see cref="Закритий_період_не_ставиться_навіть_з_payload"/> червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryImpactRecalculationJobTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Тригер_лише_для_названих_документів()
    {
        var registryId = await NewRegistryAsync();
        var first = await ArrangeDocumentAsync(registryId, PeriodState.Open);
        var second = await ArrangeDocumentAsync(registryId, PeriodState.Open);
        var trigger = Trigger();

        await RunAsync(trigger, registryId, first.DocumentId);

        await trigger.Received(1).RequestAsync(first.DocumentId, first.PeriodKey, Arg.Any<CancellationToken>());
        await trigger.DidNotReceive().RequestAsync(second.DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Закритий_період_не_ставиться_навіть_з_payload()
    {
        var registryId = await NewRegistryAsync();
        var closed = await ArrangeDocumentAsync(registryId, PeriodState.Closed);
        var trigger = Trigger();

        await RunAsync(trigger, registryId, closed.DocumentId);

        await trigger.DidNotReceiveWithAnyArgs().RequestAsync(default, default, default);
    }

    private static ICalculationTrigger Trigger()
    {
        var trigger = Substitute.For<ICalculationTrigger>();
        trigger.RequestAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("recalc-1"));
        return trigger;
    }

    private async Task RunAsync(ICalculationTrigger trigger, int registryId, params long[] documentIds)
    {
        await using var db = Context();
        var job = new RegistryImpactRecalculationJob(new RegistryImpactStore(db), trigger);

        await job.ExecuteAsync(
            new RegistryImpactRecalculationRequest(registryId, documentIds, "test"),
            Substitute.For<IJobProgress>(),
            CancellationToken.None);
    }

    private async Task<int> NewRegistryAsync()
    {
        await using var db = Context();
        var registry = new RegistryDef(
            EcrCode.Create($"RJ{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Registry" }),
            isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();
        return registry.Id;
    }

    /// <summary>Документ із прогоном <c>Current</c>, чия методологія читає довідник.</summary>
    private async Task<(long DocumentId, PeriodKey PeriodKey)> ArrangeDocumentAsync(int registryId, PeriodState state)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await using var db = builder.CreateContext();

        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
        period.TransitionTo(PeriodState.Open, Now);
        if (state == PeriodState.Closed)
        {
            period.TransitionTo(PeriodState.Closed, Now);
        }

        var methodology = new Methodology(
            EcrCode.Create($"JM{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        db.RegistryUses.Add(RegistryUse.ForMethodologyFormula(version.Id, "F1", registryId, "X"));

        var run = new CalculationRun(document.ProjectId, document.PeriodKey.Value, null, Now, document.DocumentId);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();

        run.Complete("Succeeded", Now, null, null);
        run.MakeCurrent();

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        await new CalculationResultStore(db, new TestClock(Now)).WriteResultsAsync(
            run.Id,
            [new CalculationOutput(
                document.DocumentId, "R-1",
                [new CalculationOutputValue(version.Id, null, "tons", 1m, unitId)],
                [])],
            CancellationToken.None);
        await db.SaveChangesAsync();

        return (document.DocumentId, document.PeriodKey);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
