// tests/Ecr.Api.Tests/DuplicateRequestHttpTests.UsersAndRoute.cs
using System.Net;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>Створення користувача і заміна маршруту погодження — два однакові запити.</summary>
public sealed partial class DuplicateRequestHttpTests
{
    private static object UserBody(string userName) => new
    {
        userName,
        displayName = "Duplicate request",
        provider = "Local",
        sid = (string?)null,
        initialPassword = "Dup-Request-Initial-2099!",
    };

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Створення_користувача_двічі_послідовно_друге_409_userNameTaken_і_користувач_один()
    {
        var (one, two) = await SignInTwiceAsync(["Security.ManageUsers"]).ConfigureAwait(true);
        using var first = one;
        two.Dispose();

        var name = $"dqu_{Guid.NewGuid():N}"[..20];
        var created = await SendAsync(first, HttpMethod.Post, "/api/v1/users", UserBody(name)).ConfigureAwait(true);
        var repeated = await SendAsync(first, HttpMethod.Post, "/api/v1/users", UserBody(name)).ConfigureAwait(true);

        Assert.True(created.Status == HttpStatusCode.Created, $"{created}\n{App.ErrorsText}");
        AssertRefused(repeated, HttpStatusCode.Conflict, "ECR-USR-0409", "err.ECR-USR-0409.userNameTaken");
        Assert.Equal(1, await UsersWithNameAsync(name).ConfigureAwait(true));
    }

    /// <summary>
    /// B150: два одночасні <c>POST /users</c> з тим самим іменем — один <c>201</c>, другий той самий
    /// <c>409 ECR-USR-0409 userNameTaken</c>, що й послідовний повтор (раніше — <c>500</c> на <c>UQ_User_Name</c>).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Створення_користувача_двічі_одночасно_одне_201_друге_409_і_користувач_один()
    {
        var (one, two) = await SignInTwiceAsync(["Security.ManageUsers"]).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        var name = $"dqu_{Guid.NewGuid():N}"[..20];
        var replies = await RaceAsync("users", first, second, HttpMethod.Post, "/api/v1/users", UserBody(name)).ConfigureAwait(true);

        output.WriteLine($"ROWS users {await UsersWithNameAsync(name).ConfigureAwait(true)}");
        AssertNoServerError(replies);
        Assert.Single(replies, r => r.Status == HttpStatusCode.Created);
        AssertRefused(
            replies.Single(r => r.Status != HttpStatusCode.Created),
            HttpStatusCode.Conflict, "ECR-USR-0409", "err.ECR-USR-0409.userNameTaken");
        Assert.Equal(1, await UsersWithNameAsync(name).ConfigureAwait(true));
    }

    /// <summary>
    /// B150: проєкт ще БЕЗ маршруту — обидва запити додають <c>PRJ_{id}</c>; програвший отримує той самий
    /// <c>409 ECR-CELL-0409 concurrentChange</c>, що й у гонці за наявним маршрутом (раніше — <c>500</c> на
    /// <c>UQ_ApprovalRoute</c>).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public Task Заміна_маршруту_погодження_нового_проєкту_двічі_одночасно_без_5xx_і_маршрут_один()
        => RaceRouteAsync(routeExists: false);

    /// <summary>
    /// Маршрут уже є: один запит — <c>200</c>, другий — <c>200</c> або <c>409 ECR-CELL-0409 concurrentChange</c>
    /// (кроки видалено іншим запитом); підсумок — один маршрут із тими самими кроками.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public Task Заміна_наявного_маршруту_погодження_двічі_одночасно_без_5xx_і_маршрут_один()
        => RaceRouteAsync(routeExists: true);

    private async Task RaceRouteAsync(bool routeExists)
    {
        var s = await ArrangeDocumentAsync(209911).ConfigureAwait(true);
        var (one, two) = await SignInTwiceAsync(
            ["Project.Manage"], (ResourceKind.Project, s.ProjectId, GrantLevel.Manage)).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        var roleIds = new[] { await NewRoleAsync().ConfigureAwait(true), await NewRoleAsync().ConfigureAwait(true) };
        var url = $"/api/v1/projects/{s.ProjectId}/approval-route";
        var body = new { roleIds };

        if (routeExists)
        {
            var seeded = await SendAsync(first, HttpMethod.Put, url, body).ConfigureAwait(true);
            Assert.True(seeded.Status == HttpStatusCode.OK, $"{seeded}\n{App.ErrorsText}");
        }

        var replies = await RaceAsync(
            routeExists ? "route-existing" : "route-new", first, second, HttpMethod.Put, url, body).ConfigureAwait(true);

        var (routes, steps) = await RouteAsync(s.ProjectId).ConfigureAwait(true);
        output.WriteLine($"ROWS route routes={routes} steps={string.Join(",", steps)}");

        AssertNoServerError(replies);
        Assert.Contains(replies, r => r.Status == HttpStatusCode.OK);
        Assert.All(
            replies.Where(r => r.Status != HttpStatusCode.OK),
            r => AssertRefused(r, HttpStatusCode.Conflict, "ECR-CELL-0409", "err.ECR-CELL-0409.concurrentChange"));
        Assert.Equal(1, routes);
        Assert.Equal(roleIds, steps);
    }

    private async Task<int> UsersWithNameAsync(string name)
    {
        await using var db = Db();
        return await db.Users.AsNoTracking().CountAsync(u => u.UserName == name).ConfigureAwait(false);
    }
}
