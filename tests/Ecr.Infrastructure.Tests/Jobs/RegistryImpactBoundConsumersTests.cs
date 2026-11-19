// tests/Ecr.Infrastructure.Tests/Jobs/RegistryImpactBoundConsumersTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
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
/// B80 N3(b): за стану-воріт <see cref="DocumentSourceState.ApprovedOnly"/> зміна джерела (<c>IsSourceChange</c>) ставить
/// перерахунок і документів-споживачів БЕЗ результату (<see cref="IRegistryImpactStore.ListBoundConsumersForYearAsync"/>);
/// за <see cref="DocumentSourceState.Any"/>, з вимкненим читанням і на шляху правки довідника — набір як до N3.
/// </summary>
/// <remarks>
/// ⚠ Періоди 2099xx; власний довідник, шаблони й зв'язка на тест — робоча зв'язка HSE400 не зачіпається.
/// Мутації (локально, B80-N3): прибрати перевірку <c>ApprovedOnly</c> → <see cref="Стан_воріт_вирішує_чи_ставиться_споживач_без_результату"/>
/// (Any) червоний; кликати споживачів і на правці довідника → <see cref="Правка_довідника_за_ApprovedOnly_лише_за_методологією"/>
/// червоний; прибрати збіг зі споживачами або фільтр Open/Grace → <see cref="Не_споживач_і_закритий_період_не_ставляться"/>
/// червоний; прибрати перевірку читання → <see cref="Читання_вимкнене_бітом_чи_ключем_набір_не_розширюється"/> червоний.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Directive", "B80")]
public sealed class RegistryImpactBoundConsumersTests(SqlServerFixture sql)
{
    private const int Year = 2099;
    private const int PeriodKey = (Year * 100) + 3;
    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    /// <summary>T7: два Land-споживачі, з результатом і без — обидва поставлено.</summary>
    [Fact]
    public async Task ApprovedOnly_зміна_джерела_ставить_і_споживач_без_результату()
    {
        var registryId = await NewRegistryAsync();
        var rated = await ArrangeLandAsync(registryId, withResult: true);
        var unrated = await ArrangeLandAsync(registryId, withResult: false);
        await NewBindingAsync(registryId, DocumentSourceState.ApprovedOnly, rated.TemplateId, unrated.TemplateId);

        var queued = await RunAsync(new RegistryImpactRecalculationRequest(registryId, [], "HSE400 2099 approved", Year));

        Assert.Equal(
            new[] { Target(rated), Target(unrated) }.Order(StringComparer.Ordinal),
            queued.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(DocumentSourceState.Any, false)]
    [InlineData(DocumentSourceState.ApprovedOnly, true)]
    public async Task Стан_воріт_вирішує_чи_ставиться_споживач_без_результату(DocumentSourceState state, bool unratedQueued)
    {
        var registryId = await NewRegistryAsync();
        var rated = await ArrangeLandAsync(registryId, withResult: true);
        var unrated = await ArrangeLandAsync(registryId, withResult: false);
        await NewBindingAsync(registryId, state, rated.TemplateId, unrated.TemplateId);

        var queued = await RunAsync(new RegistryImpactRecalculationRequest(registryId, [], "HSE400 2099", Year));

        Assert.Contains(Target(rated), queued);
        Assert.Equal(unratedQueued, queued.Contains(Target(unrated)));
        Assert.Equal(unratedQueued ? 2 : 1, queued.Count);
    }

    [Fact]
    public async Task Правка_довідника_за_ApprovedOnly_лише_за_методологією()
    {
        var registryId = await NewRegistryAsync();
        var rated = await ArrangeLandAsync(registryId, withResult: true);
        var unrated = await ArrangeLandAsync(registryId, withResult: false);
        await NewBindingAsync(registryId, DocumentSourceState.ApprovedOnly, rated.TemplateId, unrated.TemplateId);
        await using (var db = sql.CreateContext())
        {
            // Правка dic.* ПІСЛЯ прогону — документ із результатом зачеплений правкою.
            await db.RegistryDefs.Where(r => r.Id == registryId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.DataChangedAt, Now.AddHours(1)));
        }

        var queued = await RunAsync(
            new RegistryImpactRecalculationRequest(registryId, [rated.DocumentId, unrated.DocumentId], "registry edit"));

        Assert.Equal([Target(rated)], queued);
    }

    [Fact]
    public async Task Не_споживач_і_закритий_період_не_ставляться()
    {
        var registryId = await NewRegistryAsync();
        var unrated = await ArrangeLandAsync(registryId, withResult: false);
        var stranger = await ArrangeLandAsync(registryId, withResult: false);
        var closed = await ArrangeLandAsync(registryId, withResult: false, PeriodState.Closed);
        await NewBindingAsync(registryId, DocumentSourceState.ApprovedOnly, unrated.TemplateId, closed.TemplateId);

        var queued = await RunAsync(new RegistryImpactRecalculationRequest(registryId, [], "HSE400 2099", Year));

        Assert.Equal([Target(unrated)], queued);
        Assert.DoesNotContain(Target(stranger), queued);
        Assert.DoesNotContain(Target(closed), queued);

        // ⚠ Закритий період відкидає й тригер; сховище — перший рубіж, тож перевіряємо і його набір.
        await using var db = sql.CreateContext();
        var rows = await new RegistryImpactStore(db).ListBoundConsumersForYearAsync(registryId, Year, CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Equal((unrated.DocumentId, PeriodKey, PeriodState.Open), (row.DocumentId, row.PeriodKey, row.PeriodState));
    }

    /// <summary>
    /// ✎ B80 N4 (дочищення N3): споживач без результату в періоді <c>Grace</c> (вікно після закінчення введення) за
    /// <c>ApprovedOnly</c> ставиться, як і в <c>Open</c>.
    /// </summary>
    /// <remarks>Мутація: фільтр <c>Open || Grace</c> → лише <c>Open</c> у <c>ListBoundConsumersForYearAsync</c> — червоний.</remarks>
    [Fact]
    public async Task Споживач_без_результату_в_періоді_Grace_ставиться()
    {
        var registryId = await NewRegistryAsync();
        var grace = await ArrangeLandAsync(registryId, withResult: false, PeriodState.Grace);
        await NewBindingAsync(registryId, DocumentSourceState.ApprovedOnly, grace.TemplateId);

        var queued = await RunAsync(new RegistryImpactRecalculationRequest(registryId, [], "HSE400 2099", Year));

        Assert.Equal([Target(grace)], queued);

        await using var db = sql.CreateContext();
        var row = Assert.Single(
            await new RegistryImpactStore(db).ListBoundConsumersForYearAsync(registryId, Year, CancellationToken.None));
        Assert.Equal((grace.DocumentId, PeriodKey, PeriodState.Grace), (row.DocumentId, row.PeriodKey, row.PeriodState));
    }

    [Theory]
    [InlineData("bit")]
    [InlineData("key")]
    public async Task Читання_вимкнене_бітом_чи_ключем_набір_не_розширюється(string how)
    {
        var registryId = await NewRegistryAsync();
        var rated = await ArrangeLandAsync(registryId, withResult: true);
        var unrated = await ArrangeLandAsync(registryId, withResult: false);
        await NewBindingAsync(registryId, DocumentSourceState.ApprovedOnly, rated.TemplateId, unrated.TemplateId);
        bool? readOverride = null;
        if (how == "bit")
        {
            await using var db = sql.CreateContext();
            await db.DocumentSourceBindings.Where(b => b.RegistryDefId == registryId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.ReadFromDocument, false));
        }
        else
        {
            readOverride = false;
        }

        var queued = await RunAsync(
            new RegistryImpactRecalculationRequest(registryId, [], "HSE400 2099", Year), readOverride);

        Assert.Equal([Target(rated)], queued);
    }

    private static string Target((long DocumentId, int TemplateId) document)
        => CalculationTrigger.DependentsTargetOf(document.DocumentId, new PeriodKey(PeriodKey));

    /// <summary>Виконує задачу на справжньому сховищі й тригері; повертає цілі поставлених перерахунків.</summary>
    private async Task<List<string>> RunAsync(RegistryImpactRecalculationRequest request, bool? readOverride = null)
    {
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var queued = new List<string>();
        jobs.EnqueueCoalescedAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(call =>
            {
                queued.Add(call.ArgAt<string>(0));
                return "recalc";
            });
        var overrides = Substitute.For<IDocumentSourceFlagOverrides>();
        overrides.ReadFromDocument.Returns(readOverride);

        await using var db = sql.CreateContext();
        var job = new RegistryImpactRecalculationJob(
            new RegistryImpactStore(db, overrides),
            new CalculationTrigger(
                jobs, new PeriodStore(db), new WorkflowStore(db), new DocumentStore(db), new DocumentSheetCopyStore(db)));
        await job.ExecuteAsync(request, Substitute.For<IJobProgress>(), CancellationToken.None);
        return queued;
    }

    private async Task<int> NewRegistryAsync()
    {
        await using var db = sql.CreateContext();
        var registry = new RegistryDef(EcrCode.Create($"HN{_tag}"), Text("Composition"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();
        return registry.Id;
    }

    /// <summary>Зв'язка довідника зі споживачами; шаблон-джерело — окремий документ (свій шаблон).</summary>
    private async Task NewBindingAsync(int registryId, DocumentSourceState state, params int[] consumers)
    {
        var source = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: (Year * 100) + 1);
        await using var db = sql.CreateContext();
        var binding = new DocumentSourceBinding($"B80N3_{_tag}", source.TemplateId, registryId);
        foreach (var templateId in consumers)
        {
            binding.AddConsumer(templateId);
        }

        db.DocumentSourceBindings.Add(binding);
        await db.SaveChangesAsync();
        await db.DocumentSourceBindings.Where(b => b.Id == binding.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.SourceState, state));
    }

    /// <summary>Документ Land (свій шаблон) у періоді 2099-03; з результатом — методологія читає довідник, прогін Current.</summary>
    private async Task<(long DocumentId, int TemplateId)> ArrangeLandAsync(
        int registryId, bool withResult, PeriodState state = PeriodState.Open)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: PeriodKey);
        await using var db = builder.CreateContext();

        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
        period.TransitionTo(PeriodState.Open, Now);
        if (state is PeriodState.Closed or PeriodState.Grace)
        {
            period.TransitionTo(state, Now);
        }

        await db.SaveChangesAsync();
        if (!withResult)
        {
            return (document.DocumentId, document.TemplateId);
        }

        var methodology = new Methodology(EcrCode.Create($"HM{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"), Text("m"));
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
            [new CalculationOutput(document.DocumentId, "R-1", [new CalculationOutputValue(version.Id, null, "tons", 1m, unitId)], [])],
            CancellationToken.None);
        await db.SaveChangesAsync();
        return (document.DocumentId, document.TemplateId);
    }

    private static LocalizedText Text(string en) => new(new Dictionary<string, string> { ["en"] = en });
}
