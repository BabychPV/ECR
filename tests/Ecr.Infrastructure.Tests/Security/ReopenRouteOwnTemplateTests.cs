// tests/Ecr.Infrastructure.Tests/Security/ReopenRouteOwnTemplateTests.cs
using Ecr.Application.Common;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// D2 (B155, 16.11): роль повернення ЗАТВЕРДЖЕНОГО аркуша — останній крок маршруту версії шаблону ДОКУМЕНТА за
/// період (власний шаблон B74), а не чинної версії проєкту. Справжні база й <c>AccessDecisionService</c>.
/// </summary>
/// <remarks>
/// Документ має власний шаблон (інший за основний проєкту). Два маршрути: «проєкт × версія власного шаблону» з
/// останнім кроком роль 1 і «проєкт × версія основного шаблону» з роллю 2. До виправлення сервіс брав версію
/// проєкту й вимагав роль 2. Мутація (для AU): у <c>LastApprovalStepRoleAsync</c> взяти <c>p.TemplateVersionId</c>
/// проєкту замість <c>RouteScopeAsync</c> → червоніють обидва тести.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReopenRouteOwnTemplateTests(SqlServerFixture sql) : IDisposable
{
    private const int PeriodKeyValue = 209903;

    private static readonly DateTime Now = new(2099, 3, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.17")]
    [Trait("Decision", "B155")]
    public async Task Затверджений_аркуш_власного_шаблону_повертає_роль_маршруту_власного_шаблону()
    {
        var (doc, codes) = await ArrangeApprovedAsync();
        try
        {
            await using var db = Context();
            var decision = await Service(db).CanReopenAsync(
                Profile(doc, roleId: 1), doc.DocumentId, doc.SheetDefId, doc.PeriodKey, CancellationToken.None);

            Assert.True(decision.IsAllowed, $"Роль останнього кроку маршруту власного шаблону має повертати: {decision.Reason} {decision.Detail}");
        }
        finally
        {
            await CleanupAsync(doc, codes);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.17")]
    [Trait("Decision", "B155")]
    public async Task Роль_маршруту_версії_проєкту_не_повертає_аркуш_власного_шаблону()
    {
        var (doc, codes) = await ArrangeApprovedAsync();
        try
        {
            await using var db = Context();
            var decision = await Service(db).CanReopenAsync(
                Profile(doc, roleId: 2), doc.DocumentId, doc.SheetDefId, doc.PeriodKey, CancellationToken.None);

            Assert.False(decision.IsAllowed, "Роль чужого маршруту (версії проєкту) не має повертати аркуш власного шаблону.");
        }
        finally
        {
            await CleanupAsync(doc, codes);
        }
    }

    /// <summary>Документ із власним шаблоном, два маршрути, аркуш затверджено.</summary>
    private async Task<(TestDocument Doc, string[] Codes)> ArrangeApprovedAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, rowCount: 1);
        await builder.OpenPeriodAsync(doc);

        // Власний шаблон документа — ІНШИЙ шаблон зі своєю версією (як WorkflowNoneCase.OwnNone).
        var other = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 209912);

        string[] codes = [$"D2OWN_{doc.ProjectId}", $"D2PRJ_{doc.ProjectId}"];
        await using (var db = Context())
        {
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId) VALUES ({doc.ProjectId}, {other.TemplateId}, {other.TemplateVersionId})");
            await db.Database.ExecuteSqlAsync(
                $"UPDATE doc.Document SET TemplateId = {other.TemplateId} WHERE Id = {doc.DocumentId}");

            db.ApprovalRoutes.AddRange(
                Route(codes[0], doc.ProjectId, other.TemplateVersionId, roleId: 1),
                Route(codes[1], doc.ProjectId, doc.TemplateVersionId, roleId: 2));

            var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, PeriodKeyValue);
            state.Submit(userId: 1, Now);
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync();
        }

        await using (var db = Context())
        {
            var state = await db.ApprovalStates.SingleAsync(
                a => a.DocumentId == doc.DocumentId && a.SheetDefId == doc.SheetDefId && a.PeriodKey == PeriodKeyValue);
            state.Approve(userId: 2, Now);
            await db.SaveChangesAsync();
        }

        return (doc, codes);
    }

    private async Task CleanupAsync(TestDocument doc, string[] codes)
    {
        await using var db = Context();
        await db.ApprovalStates.Where(a => a.DocumentId == doc.DocumentId).ExecuteDeleteAsync();
        var routes = await db.ApprovalRoutes.Include(r => r.Steps).Where(r => codes.Contains(r.Code)).ToListAsync();
        foreach (var route in routes)
        {
            await new WorkflowStore(db).RemoveRouteAsync(route, CancellationToken.None);
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Approve на проєкт і одна роль у проєкті.</summary>
    private static AccessProfile Profile(TestDocument doc, int roleId)
        => new AccessBuilder().Grant(ResourceKind.Project, doc.ProjectId, GrantLevel.Approve).Role(roleId).Build();

    private static ApprovalRoute Route(string code, int projectId, int templateVersionId, int roleId)
    {
        var route = new ApprovalRoute(
            EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }),
            projectId,
            templateVersionId);
        route.AddStep(roleId);
        return route;
    }

    private EcrDbContext Context() => new TestDocumentBuilder(sql.ConnectionString).CreateContext();

    private AccessDecisionService Service(EcrDbContext db)
        => new(
            db,
            new MetadataCache(_memory, db),
            new AccessProfileCache(_memory),
            new TestClock(Now),
            Substitute.For<ICurrentUser>(),
            new WorkflowStore(db));
}
