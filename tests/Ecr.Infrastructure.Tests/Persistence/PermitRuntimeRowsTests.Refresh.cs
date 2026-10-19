// tests/Ecr.Infrastructure.Tests/Persistence/PermitRuntimeRowsTests.Refresh.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Permits.Bindings;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// «Оновити з дозволу» (Permits, PM-10): обробник над справжніми правами й базою - додає рядки, не видаляє зниклі,
/// відхиляє закритий період і поданий аркуш тією самою відмовою, що й додавання рядка.
/// </summary>
public sealed partial class PermitRuntimeRowsTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Обробник_оновлення_додає_рядки_повідомляє_про_зниклі_і_ставить_перерахунок()
    {
        var scene = await ArrangeAsync();
        await using var _ = scene.H;
        var profile = await OpenPeriodProfileAsync(scene);
        await scene.H.SeedPermitAsync(Type, $"N3{_tag}", "Site A", new DateOnly(2026, 1, 15), null, true, ("NEW1", 1m, null), ("NEW2", 2m, "x"));
        var jobs = Jobs();

        var result = await HandleAsync(scene, profile, jobs);

        Assert.Equal(2, result.Added);
        Assert.Equal(3, result.RemovedFromPermit.Count);
        Assert.Equal(5, (await scene.RowsAsync()).Count);
        await jobs.Received(1).EnqueueCoalescedAsync<IFormulaRecalculationJob>(
            Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());

        // Невідомий код прив'язки - 404.
        await using var db = scene.NewContext();
        await Assert.ThrowsAsync<NotFoundException>(
            () => NewHandler(db, Jobs()).HandleAsync(scene.DocumentId, "NOPE", January.Value, profile, default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Обробник_оновлення_відхиляє_закритий_період_і_поданий_аркуш_нічого_не_змінюючи()
    {
        var scene = await ArrangeAsync();
        await using var _ = scene.H;
        var profile = await OpenPeriodProfileAsync(scene);
        await scene.H.SeedPermitAsync(Type, $"N3{_tag}", "Site A", new DateOnly(2026, 1, 15), null, true, ("NEW1", 1m, null));

        // Подано.
        await using (var db = scene.NewContext())
        {
            var state = new ApprovalState(scene.DocumentId, scene.Sheet.Doc.SheetDefId, January.Value);
            state.Submit(profile.UserId, Now);
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync();
        }

        var submitted = await Assert.ThrowsAsync<AccessDeniedException>(() => HandleAsync(scene, profile, Jobs()));
        Assert.Equal("ECR-ACCS-0403", submitted.ErrorCode);
        Assert.Equal("err.ECR-ACCS-0403.addRowDenied", submitted.Details!["messageKey"]);
        Assert.Equal(nameof(EditDenyReason.DocumentSubmitted), submitted.Details["reason"]?.ToString());

        // Закрито.
        await using (var db = scene.NewContext())
        {
            await db.ApprovalStates.Where(s => s.DocumentId == scene.DocumentId).ExecuteDeleteAsync();
            var period = await db.Periods.FirstAsync(p => p.ProjectId == scene.ProjectId && p.PeriodKeyValue == January.Value);
            period.AdvanceTo(PeriodState.Grace, Now);
            period.TransitionTo(PeriodState.Closed, Now);
            await db.SaveChangesAsync();
        }

        var closed = await Assert.ThrowsAsync<AccessDeniedException>(() => HandleAsync(scene, profile, Jobs()));
        Assert.Equal(nameof(EditDenyReason.PeriodClosed), closed.Details!["reason"]?.ToString());

        // Рядки з дозволу створені при заведенні екземпляра (3), нічого не додано.
        Assert.Equal(3, (await scene.RowsAsync()).Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Обробник_оновлення_без_Registry_View_відмовляє_до_будь_якого_запису_і_не_видає_даних_дозволу()
    {
        var scene = await ArrangeAsync();
        await using var _ = scene.H;
        var withRight = await OpenPeriodProfileAsync(scene);
        await scene.H.SeedPermitAsync(Type, $"N3{_tag}", "Site A", new DateOnly(2026, 1, 15), null, true, ("NEW1", 1m, null));

        // Той самий запис у проєкт, але без права читати реєстри дозволів.
        var noRegistry = new AccessBuilder { UserId = withRight.UserId }
            .Grant(ResourceKind.Project, scene.ProjectId, GrantLevel.Write)
            .Build();
        var jobs = Jobs();

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => HandleAsync(scene, noRegistry, jobs));

        Assert.Equal(("ECR-AUTH-0403", "Registry.View"), (denied.ErrorCode, denied.Details!["permission"]?.ToString()));
        Assert.DoesNotContain($"N3{_tag}", denied.Message, StringComparison.Ordinal);
        Assert.Equal(3, (await scene.RowsAsync()).Count);
        await jobs.DidNotReceive().EnqueueCoalescedAsync<IFormulaRecalculationJob>(
            Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());

        // З правом - рядок додається (контроль, що відмова саме про реєстр, а не про проєкт).
        Assert.Equal(1, (await HandleAsync(scene, withRight, Jobs())).Added);
    }

    private async Task<PermitRefreshResultDto> HandleAsync(Scene scene, AccessProfile profile, IBackgroundJobScheduler jobs)
    {
        await using var db = scene.NewContext();
        return await NewHandler(db, jobs).HandleAsync(scene.DocumentId, $"B{_tag}", January.Value, profile, default);
    }

    private static IBackgroundJobScheduler Jobs()
    {
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnlistsInCallerTransaction.Returns(false);
        jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("job-1");
        return jobs;
    }

    private RefreshPermitBindingHandler NewHandler(EcrDbContext db, IBackgroundJobScheduler jobs)
    {
        var clock = new TestClock(Now);
        var memory = new MemoryCache(new MemoryCacheOptions());
        var serviceDb = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var access = new AccessDecisionService(
            serviceDb, new MetadataCache(memory, serviceDb), new AccessProfileCache(memory), clock,
            Substitute.For<ICurrentUser>(), new WorkflowStore(serviceDb));

        return new RefreshPermitBindingHandler(
            NewStore(db, permits: true), new DocumentStore(db), new PermitBindingStore(db), new PeriodStore(db), NewService(db),
            new PermitTypeStore(db), new MetadataCache(memory, db), access, new UnitOfWork(db), clock, new SheetEditGate(db), jobs, new EditAssignmentStore(db));
    }

    /// <summary>Користувач із записом у проєкт; період відкрито, а рядки з дозволу вже заведені відкриттям документа.</summary>
    private async Task<AccessProfile> OpenPeriodProfileAsync(Scene scene)
    {
        await using (var db = scene.NewContext())
        {
            await NewStore(db, permits: true).EnsureTableInstancesAsync(scene.DocumentId, January, default);
        }

        await using var setup = scene.NewContext();
        var period = await setup.Periods.FirstAsync(p => p.ProjectId == scene.ProjectId && p.PeriodKeyValue == January.Value);
        period.AdvanceTo(PeriodState.Open, Now);
        var user = new User($"pm10_{Guid.NewGuid():N}"[..20], "PM-10 refresh test user", AuthProvider.Local);
        user.SetPassword("not-a-real-hash");
        setup.Users.Add(user);
        await setup.SaveChangesAsync();

        return new AccessBuilder { UserId = user.Id }
            .Grant(ResourceKind.Project, scene.ProjectId, GrantLevel.Write)
            .Permission("Registry.View")
            .Build();
    }
}
