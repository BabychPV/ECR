using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Резолюція маршруту погодження: від конкретного до загального (ФВ-5.17).
/// </summary>
/// <remarks>
/// ⛔ Головне тут — **останній рівень**: маршруту немає, і затвердження
/// лишається одноетапним. Порожня таблиця маршрутів означає поведінку без
/// змін, і саме тому багатоетапність вмикається тим, що хтось завів маршрут,
/// а не тим, що вийшла нова версія системи.
/// </remarks>
[Collection("SqlServer")]
public sealed class ApprovalRouteResolutionTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.17")]
    public async Task Виграє_найконкретніший_маршрут_із_придатних()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202611, ct: CancellationToken.None);

        var tag = doc.ProjectId;

        await using (var db = builder.CreateContext())
        {
            db.ApprovalRoutes.AddRange(
                Route($"ALL_{tag}", null, null, roleId: 1),
                Route($"VER_{tag}", null, doc.TemplateVersionId, roleId: 2),
                Route($"PRJ_{tag}", doc.ProjectId, null, roleId: 3),
                Route($"BOTH_{tag}", doc.ProjectId, doc.TemplateVersionId, roleId: 4));

            await db.SaveChangesAsync(CancellationToken.None);
        }

        // 1. Проєкт І версія.
        Assert.Equal(4, await RoleOfFirstStepAsync(builder, doc));

        // 2. Проєкт, версія будь-яка.
        await DropAsync(builder, $"BOTH_{tag}");
        Assert.Equal(3, await RoleOfFirstStepAsync(builder, doc));

        // 3. Версія, проєкт будь-який.
        //
        // ⚠ Цього рівня директива не називає, але поле `TemplateVersionId`
        // існувало в схемі до `ProjectId`. Без нього маршрут, налаштований
        // лише на версію, був би тихо мертвим — той самий дефект, від якого
        // весь `A7`.
        await DropAsync(builder, $"PRJ_{tag}");
        Assert.Equal(2, await RoleOfFirstStepAsync(builder, doc));

        // 4. Типовий: обидві координати порожні.
        await DropAsync(builder, $"VER_{tag}");
        Assert.Equal(1, await RoleOfFirstStepAsync(builder, doc));

        // 5. Маршруту немає → затвердження ОДНОЕТАПНЕ, як було.
        await DropAsync(builder, $"ALL_{tag}");

        await using var last = builder.CreateContext();
        Assert.Null(await new WorkflowStore(last)
            .FindRouteAsync(doc.ProjectId, doc.TemplateVersionId, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.17")]
    public async Task Маршрут_чужого_проєкту_не_застосовується()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var mine = await builder.BuildAsync(periodKey: 202611, ct: CancellationToken.None);
        var alien = await builder.BuildAsync(periodKey: 202611, ct: CancellationToken.None);

        await using (var db = builder.CreateContext())
        {
            db.ApprovalRoutes.Add(Route($"ALIEN_{alien.ProjectId}", alien.ProjectId, null, roleId: 9));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // ⛔ Маршрут сусіда не має вмикати багатоетапність тут. Інакше один
        // налаштований проєкт змінив би поведінку всіх решти.
        await using var db2 = builder.CreateContext();
        var route = await new WorkflowStore(db2)
            .FindRouteAsync(mine.ProjectId, mine.TemplateVersionId, CancellationToken.None);

        Assert.Null(route);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.17")]
    public async Task Заміна_кроків_прибирає_старі_а_не_додає_поверх()
    {
        // ⛔ `ClearSteps` чистить лише список у пам'яті. Якщо EF не побачить
        // видалення, старі рядки лишаться в базі, і маршрут із двох кроків
        // після заміни став би маршрутом із чотирьох — половина з яких від
        // конфігурації, якої вже немає.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202611, ct: CancellationToken.None);

        await using (var db = builder.CreateContext())
        {
            var route = Route($"REPL_{doc.ProjectId}", doc.ProjectId, null, roleId: 11);
            route.AddStep(22);

            db.ApprovalRoutes.Add(route);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = builder.CreateContext())
        {
            var route = await new WorkflowStore(db)
                .FindProjectRouteAsync(doc.ProjectId, CancellationToken.None);

            Assert.NotNull(route);
            Assert.Equal(2, route.Steps.Count);

            await new WorkflowStore(db).RemoveStepsAsync(route, CancellationToken.None);
            route.AddStep(33);

            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var check = builder.CreateContext();
        var replaced = await new WorkflowStore(check)
            .FindProjectRouteAsync(doc.ProjectId, CancellationToken.None);

        Assert.NotNull(replaced);
        Assert.Equal([33], replaced.Steps.Select(s => s.RoleId));
    }

    /// <summary>Роль першого кроку маршруту, який виграв резолюцію.</summary>
    private async Task<int> RoleOfFirstStepAsync(TestDocumentBuilder builder, TestDocument doc)
    {
        await using var db = builder.CreateContext();

        var route = await new WorkflowStore(db)
            .FindRouteAsync(doc.ProjectId, doc.TemplateVersionId, CancellationToken.None);

        Assert.NotNull(route);

        return route.Steps.OrderBy(s => s.Ordinal).First().RoleId;
    }

    private static async Task DropAsync(TestDocumentBuilder builder, string code)
    {
        await using var db = builder.CreateContext();

        var route = db.ApprovalRoutes.Single(r => r.Code == code);

        await new WorkflowStore(db).RemoveRouteAsync(route, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static ApprovalRoute Route(string code, int? projectId, int? templateVersionId, int roleId)
    {
        var route = new ApprovalRoute(
            EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }),
            projectId,
            templateVersionId);

        route.AddStep(roleId);

        return route;
    }
}
