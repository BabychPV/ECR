// tests/Ecr.Infrastructure.Tests/Persistence/CalculationFreshnessRegistryTests.cs
using Ecr.Application.Ports;
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
/// Правка довідника робить результат методології застарілим (RT-25, ФВ-9.19) —
/// <see cref="MethodologyStore.GetCalculationFreshnessAsync"/> на справжній базі.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати запит довідників → <see cref="Правка_довідника_робить_результат_застарілим"/> червоний;
/// прибрати умову <c>DataChangedAt &gt; StartedAt</c> → <see cref="Правка_до_прогону_не_робить_застарілим"/> червоний;
/// прибрати <c>SourceKind = 1</c>/звʼязок з результатом → <see cref="Довідник_якого_методологія_не_читає_не_впливає"/> червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationFreshnessRegistryTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait("Requirement", "ФВ-9.19")]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правка_довідника_робить_результат_застарілим()
    {
        var f = await ArrangeAsync(registryChangedAt: Now.AddHours(1), readsRegistry: true);

        var freshness = await Freshness(f, tables: null);

        Assert.True(freshness.IsStale);
        Assert.Equal(Now.AddHours(1), freshness.InputsChangedAt);
        Assert.Equal([f.RegistryCode], freshness.ChangedRegistryCodes);
    }

    [Fact]
    [Trait("Requirement", "ФВ-9.19")]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правка_до_прогону_не_робить_застарілим()
    {
        var f = await ArrangeAsync(registryChangedAt: Now.AddMinutes(-5), readsRegistry: true);

        var freshness = await Freshness(f, tables: null);

        Assert.False(freshness.IsStale);
        Assert.Empty(freshness.ChangedRegistryCodes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Довідник_якого_методологія_не_читає_не_впливає()
    {
        var f = await ArrangeAsync(registryChangedAt: Now.AddHours(1), readsRegistry: false);

        Assert.False((await Freshness(f, tables: null)).IsStale);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ребро_шаблону_з_тим_самим_SourceId_не_впливає()
    {
        var f = await ArrangeAsync(registryChangedAt: Now.AddHours(1), readsRegistry: false, foreignEdgeWithSameId: true);

        Assert.False((await Freshness(f, tables: null)).IsStale);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Подання_аркуша_з_фільтром_таблиць_правкою_довідника_не_блокується()
    {
        var f = await ArrangeAsync(registryChangedAt: Now.AddHours(1), readsRegistry: true);

        var freshness = await Freshness(f, tables: [f.Document.TableDefId]);

        Assert.False(freshness.IsStale);
    }

    private async Task<CalculationFreshness> Freshness(Fixture f, IReadOnlyCollection<int>? tables)
    {
        await using var db = f.Builder.CreateContext();
        return await new MethodologyStore(db).GetCalculationFreshnessAsync(
            f.Document.DocumentId, f.Document.PeriodKey.Value, tables, CancellationToken.None);
    }

    private sealed record Fixture(TestDocumentBuilder Builder, TestDocument Document, string RegistryCode);

    private async Task<Fixture> ArrangeAsync(DateTime registryChangedAt, bool readsRegistry, bool foreignEdgeWithSameId = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var registry = new RegistryDef(
            EcrCode.Create($"FR{tag}"), Name("Registry"), isTemporal: false);
        registry.MarkDataChanged(registryChangedAt);
        db.RegistryDefs.Add(registry);

        var methodology = new Methodology(EcrCode.Create($"FM_{tag}"), Name("m"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        if (readsRegistry)
        {
            db.RegistryUses.Add(RegistryUse.ForMethodologyFormula(version.Id, "F1", registry.Id, "X"));
        }
        else if (foreignEdgeWithSameId)
        {
            // Ребро ШАБЛОНУ з тим самим SourceId — не ребро версії методології.
            db.RegistryUses.Add(RegistryUse.ForTemplateFormula(version.Id, registry.Id, "X"));
        }

        var run = new CalculationRun(document.ProjectId, document.PeriodKey.Value, triggeredByUserId: null, Now);
        run.Complete("Succeeded", Now.AddMinutes(1), "{}", errorMessage: null);
        run.MakeCurrent();
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        await new CalculationResultStore(db, new TestClock(Now)).WriteResultsAsync(
            run.Id,
            [new CalculationOutput(
                document.DocumentId, "R-1",
                [new CalculationOutputValue(version.Id, null, "tons", 1m, unitId)],
                [])],
            CancellationToken.None);
        await db.SaveChangesAsync();

        return new Fixture(builder, document, registry.Code);
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
