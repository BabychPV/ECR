// tests/Ecr.Infrastructure.Tests/Persistence/ApprovalRouteReplaceRaceTests.cs
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Дві одночасні заміни НАЯВНОГО маршруту погодження (<c>PUT /projects/{id}/approval-route</c>) — детермінована
/// розстановка гонки, яку <c>DuplicateRequestHttpTests</c> ловить лише інколи (CI, server-part api-7: 500 + 200).
/// </summary>
/// <remarks>
/// Порядок тут той самий, що в <c>ReplaceApprovalRouteHandler</c>: програвший запит читає маршрут із кроками
/// (<c>FindProjectRouteAsync</c>, <c>Include</c>), переможець ЦІЛКОМ комітить свою заміну, і лише тоді програвший
/// прибирає кроки (<c>RemoveStepsAsync</c>), додає нові й зберігає. Під RCSI повторний запит кроків у
/// <c>RemoveStepsAsync</c> бачить уже кроки ПЕРЕМОЖЦЯ, а прочитані раніше лишалися у трекері незміненими —
/// <c>ClearSteps</c> відвʼязував їх від маршруту, і обовʼязковий (<c>Restrict</c>) звʼязок валив збереження
/// <c>InvalidOperationException</c> («association … has been severed») — голий 500.
/// Очікувано: програвший — <c>409 ECR-CELL-0409 concurrentChange</c> (його <c>DELETE</c> уже видалених кроків
/// зачіпає 0 рядків), у базі — один маршрут із кроками переможця.
/// <para>
/// Мутація: повернути в <c>WorkflowStore.RemoveStepsAsync</c>/<c>RemoveRouteAsync</c> видалення лише
/// перечитаних кроків (без відстежуваних) — обидва тести червоні на <c>InvalidOperationException</c>.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class ApprovalRouteReplaceRaceTests(SqlServerFixture sql)
{
    private static readonly int[] Seeded = [1, 2];
    private static readonly int[] Replaced = [3, 4];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B150")]
    public async Task Заміна_кроків_після_чужої_заміни_дає_409_concurrentChange_а_не_розірваний_звʼязок()
    {
        var projectId = await SeedRouteAsync(209907);

        // Програвший: прочитав маршрут разом із кроками — і «завис» (рандеву: переможець комітиться саме тут).
        await using var loser = Context();
        var loserStore = new WorkflowStore(loser);
        var route = await loserStore.FindProjectRouteAsync(projectId, CancellationToken.None);
        Assert.NotNull(route);

        await ReplaceAsync(projectId, Replaced);

        await loserStore.RemoveStepsAsync(route, CancellationToken.None);
        foreach (var roleId in Replaced)
        {
            route.AddStep(roleId);
        }

        var thrown = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => new UnitOfWork(loser).SaveChangesAsync(CancellationToken.None));

        Assert.Equal("ECR-CELL-0409", thrown.ErrorCode);
        Assert.Equal("err.ECR-CELL-0409.concurrentChange", thrown.Details!["messageKey"]);

        var (routes, steps) = await RouteAsync(projectId);
        Assert.Equal(1, routes);
        Assert.Equal(Replaced, steps);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B150")]
    public async Task Прибирання_маршруту_після_чужої_заміни_дає_409_concurrentChange_а_не_розірваний_звʼязок()
    {
        var projectId = await SeedRouteAsync(209908);

        // Порожній набір ролей (`RemoveRouteAsync`) — та сама розстановка: прочитані кроки вже видалив переможець.
        await using var loser = Context();
        var loserStore = new WorkflowStore(loser);
        var route = await loserStore.FindProjectRouteAsync(projectId, CancellationToken.None);
        Assert.NotNull(route);

        await ReplaceAsync(projectId, Replaced);

        await loserStore.RemoveRouteAsync(route, CancellationToken.None);

        var thrown = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => new UnitOfWork(loser).SaveChangesAsync(CancellationToken.None));

        Assert.Equal("ECR-CELL-0409", thrown.ErrorCode);
        Assert.Equal("err.ECR-CELL-0409.concurrentChange", thrown.Details!["messageKey"]);

        var (routes, steps) = await RouteAsync(projectId);
        Assert.Equal(1, routes);
        Assert.Equal(Replaced, steps);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B150")]
    public async Task Чужа_заміна_між_прибиранням_кроків_і_збереженням_теж_дає_409_concurrentChange()
    {
        var projectId = await SeedRouteAsync(209910);

        // Інша розстановка тієї самої гонки: переможець комітиться ПІСЛЯ `RemoveStepsAsync` програвшого, але до його
        // збереження — `DELETE` старих кроків зачіпає 0 рядків, а нові кроки програвшого мають ті самі
        // (`ApprovalRouteId`, `Ordinal`), що й закомічені кроки переможця (`UQ_ApprovalStep`).
        await using var loser = Context();
        var loserStore = new WorkflowStore(loser);
        var route = await loserStore.FindProjectRouteAsync(projectId, CancellationToken.None);
        Assert.NotNull(route);

        await loserStore.RemoveStepsAsync(route, CancellationToken.None);
        foreach (var roleId in Replaced)
        {
            route.AddStep(roleId);
        }

        await ReplaceAsync(projectId, Replaced);

        var thrown = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => new UnitOfWork(loser).SaveChangesAsync(CancellationToken.None));

        Assert.Equal("ECR-CELL-0409", thrown.ErrorCode);
        Assert.Equal("err.ECR-CELL-0409.concurrentChange", thrown.Details!["messageKey"]);

        var (routes, steps) = await RouteAsync(projectId);
        Assert.Equal(1, routes);
        Assert.Equal(Replaced, steps);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B150")]
    public async Task Заміна_кроків_без_гонки_лишає_один_маршрут_із_новими_кроками()
    {
        var projectId = await SeedRouteAsync(209909);

        await ReplaceAsync(projectId, Replaced);
        await ReplaceAsync(projectId, Seeded);

        var (routes, steps) = await RouteAsync(projectId);
        Assert.Equal(1, routes);
        Assert.Equal(Seeded, steps);
    }

    /// <summary>Проєкт із маршрутом <see cref="Seeded"/>.</summary>
    private async Task<int> SeedRouteAsync(int periodKey)
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey);

        await using var db = Context();
        var route = new ApprovalRoute(
            EcrCode.Create($"PRJ_{document.ProjectId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Route race" }),
            document.ProjectId);
        db.ApprovalRoutes.Add(route);
        await new UnitOfWork(db).SaveChangesAsync(CancellationToken.None);

        foreach (var roleId in Seeded)
        {
            route.AddStep(roleId);
        }

        await new UnitOfWork(db).SaveChangesAsync(CancellationToken.None);
        return document.ProjectId;
    }

    /// <summary>Повна заміна кроків окремим контекстом — кроки обробника заміни, закомічені.</summary>
    private async Task ReplaceAsync(int projectId, int[] roleIds)
    {
        await using var db = Context();
        var store = new WorkflowStore(db);
        var route = await store.FindProjectRouteAsync(projectId, CancellationToken.None);
        Assert.NotNull(route);

        await store.RemoveStepsAsync(route, CancellationToken.None);
        foreach (var roleId in roleIds)
        {
            route.AddStep(roleId);
        }

        await new UnitOfWork(db).SaveChangesAsync(CancellationToken.None);
    }

    private async Task<(int Routes, int[] StepRoles)> RouteAsync(int projectId)
    {
        await using var db = Context();
        var routes = await db.ApprovalRoutes.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.TemplateVersionId == null)
            .Select(r => r.Id)
            .ToListAsync();
        var steps = await db.ApprovalSteps.AsNoTracking()
            .Where(s => routes.Contains(s.ApprovalRouteId))
            .OrderBy(s => s.Ordinal)
            .Select(s => s.RoleId)
            .ToArrayAsync();
        return (routes.Count, steps);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);
}
