// tests/Ecr.Infrastructure.Tests/Persistence/RegistryImpactStoreTests.cs
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// «Які документи зачепила правка довідника» (RT-25, §5.10) на справжній базі.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати фільтр стану періоду → <see cref="Impact_без_закритих_періодів"/> червоний;
/// прибрати <c>run.Status == Current</c> → <see cref="Застарілий_прогін_не_рахується"/> червоний;
/// прибрати фільтр <c>SourceKind</c> → <see cref="Ребро_шаблону_чи_правила_не_дає_зачепленості"/> червоний;
/// прибрати <c>run.StartedAt &lt; DataChangedAt</c> → <see cref="Перерахований_після_правки_документ_не_зачеплений"/>
/// і <see cref="Довідник_без_правок_нічого_не_зачепив"/> червоні.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryImpactStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відкритий_період_повертає_документ_і_методологію()
    {
        var f = await ArrangeAsync(PeriodState.Open);

        var rows = await ImpactAsync(f.RegistryId);

        var row = Assert.Single(rows);
        Assert.Equal(f.Document.DocumentId, row.DocumentId);
        Assert.Equal(f.Document.ProjectId, row.ProjectId);
        Assert.Equal(f.Document.PeriodKey.Value, row.PeriodKey);
        Assert.Equal(PeriodState.Open, row.PeriodState);
        Assert.Equal(f.MethodologyCode, row.MethodologyCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Грейс_теж_відкритий_період()
    {
        var f = await ArrangeAsync(PeriodState.Grace);

        Assert.Equal(PeriodState.Grace, Assert.Single(await ImpactAsync(f.RegistryId)).PeriodState);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData(PeriodState.Closed)]
    [InlineData(PeriodState.Scheduled)]
    public async Task Impact_без_закритих_періодів(PeriodState state)
    {
        var f = await ArrangeAsync(state);

        Assert.Empty(await ImpactAsync(f.RegistryId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Застарілий_прогін_не_рахується()
    {
        var f = await ArrangeAsync(PeriodState.Open, runStatus: CalculationRun.SupersededStatus);

        Assert.Empty(await ImpactAsync(f.RegistryId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ребро_шаблону_чи_правила_не_дає_зачепленості()
    {
        var f = await ArrangeAsync(PeriodState.Open, useKind: RegistryUse.TemplateFormulaSource);

        Assert.Empty(await ImpactAsync(f.RegistryId));
    }

    /// <summary>
    /// Прогін почався ПІСЛЯ правки довідника — результати вже свіжі: «Перерахувати зачеплені» не має
    /// лишати документ у переліку (той самий критерій, що й банер свіжості).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перерахований_після_правки_документ_не_зачеплений()
    {
        var f = await ArrangeAsync(PeriodState.Open, registryChangedMinutesAfterRun: -5);

        Assert.Empty(await ImpactAsync(f.RegistryId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Довідник_без_правок_нічого_не_зачепив()
    {
        var f = await ArrangeAsync(PeriodState.Open, registryChanged: false);

        Assert.Empty(await ImpactAsync(f.RegistryId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Інший_довідник_не_зачеплений()
    {
        var f = await ArrangeAsync(PeriodState.Open);
        await using var db = Context();
        var other = new RegistryDef(
            EcrCode.Create($"IO{_tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Other" }),
            isTemporal: false);
        other.MarkDataChanged(Now.AddHours(1));
        db.RegistryDefs.Add(other);
        await db.SaveChangesAsync();

        Assert.Empty(await ImpactAsync(other.Id));
        Assert.NotEmpty(await ImpactAsync(f.RegistryId));
    }

    private async Task<IReadOnlyList<Ecr.Application.Ports.RegistryImpactRow>> ImpactAsync(int registryId)
    {
        await using var db = Context();
        return await new RegistryImpactStore(db).ListImpactedAsync(registryId, 100, CancellationToken.None);
    }

    private sealed record Fixture(TestDocument Document, int RegistryId, string MethodologyCode);

    // registryChangedMinutesAfterRun — правка довідника відносно старту прогону (за замовчуванням після:
    // документ застарів); registryChanged = false — довідник не правили жодного разу (DataChangedAt = null).
    private async Task<Fixture> ArrangeAsync(
        PeriodState state,
        string runStatus = CalculationRun.CurrentStatus,
        byte useKind = RegistryUse.MethodologyVersionSource,
        int registryChangedMinutesAfterRun = 60,
        bool registryChanged = true)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await using var db = builder.CreateContext();

        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
        if (state != PeriodState.Scheduled)
        {
            period.TransitionTo(PeriodState.Open, Now);
        }

        if (state == PeriodState.Grace)
        {
            period.TransitionTo(PeriodState.Grace, Now);
        }
        else if (state == PeriodState.Closed)
        {
            period.TransitionTo(PeriodState.Closed, Now);
        }

        var registry = new RegistryDef(
            EcrCode.Create($"IR{_tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Registry" }),
            isTemporal: false);
        if (registryChanged)
        {
            registry.MarkDataChanged(Now.AddMinutes(registryChangedMinutesAfterRun));
        }

        db.RegistryDefs.Add(registry);

        var methodology = new Methodology(
            EcrCode.Create($"IM_{_tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        db.RegistryUses.Add(useKind switch
        {
            RegistryUse.TemplateFormulaSource => RegistryUse.ForTemplateFormula(version.Id, registry.Id, "X"),
            _ => RegistryUse.ForMethodologyFormula(version.Id, "F1", registry.Id, "X"),
        });

        var run = new CalculationRun(document.ProjectId, document.PeriodKey.Value, null, Now, document.DocumentId);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();

        run.Complete("Succeeded", Now, null, null);
        if (runStatus == CalculationRun.CurrentStatus)
        {
            run.MakeCurrent();
        }


        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        // Id результату — з SEQUENCE: пишемо тим самим шляхом, що й прогін.
        await new CalculationResultStore(db, new TestClock(Now)).WriteResultsAsync(
            run.Id,
            [new Ecr.Application.Ports.CalculationOutput(
                document.DocumentId, "R-1",
                [new Ecr.Application.Ports.CalculationOutputValue(version.Id, null, "tons", 1m, unitId)],
                [])],
            CancellationToken.None);
        await db.SaveChangesAsync();

        return new Fixture(document, registry.Id, methodology.Code);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
