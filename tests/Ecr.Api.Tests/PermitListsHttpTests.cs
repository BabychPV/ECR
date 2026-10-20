// tests/Ecr.Api.Tests/PermitListsHttpTests.cs
using System.Net;
using Ecr.Application.Common;
using Ecr.Application.Permits.Lists;
using Ecr.Application.Security;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;
using static Ecr.Api.Tests.PermitListsKit;

namespace Ecr.Api.Tests;

/// <summary>
/// Читання списків вибору дозволів (Permits, PM-08e): <c>GET /permit-lists</c> і <c>GET /permit-lists/{key}</c> - склад, лічильники,
/// використання, права читання, число запитів без залежності від числа типів. Запис значень - <see cref="PermitListsWriteHttpTests"/>.
/// </summary>
/// <remarks>
/// Мутаційний доказ (виконаний): у <c>PermitListStore.ListUsagesAsync</c> додати вибірку типу по одному на кожен тип - червоний
/// «Число_запитів…» (6 -> 10 запитів на +4 типи).
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitListsHttpTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_віддає_спільний_і_лише_для_типу_з_лічильниками_і_використанням()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var shared = await EnsureSharedRegistryAsync(sql, $"PLS_{_tag}");
        await PublishTypeAsync(client, $"PLA{_tag}", shared, ["Hazardous", "Safe"]);
        await PublishTypeAsync(client, $"PLB{_tag}", shared, ["Red"]);

        var lists = await ReadListAsync(client);
        var sharedList = Assert.Single(lists, l => l.GetProperty("key").GetString() == $"shared:{shared}");

        Assert.Equal("SharedRegistry", sharedList.GetProperty("scope").GetString());
        Assert.Equal(1, sharedList.GetProperty("itemCount").GetInt32());
        Assert.True(sharedList.GetProperty("editable").GetBoolean());

        // Один спільний довідник - ОДИН рядок, обидва типи в usedBy.
        Assert.Equal(
            [$"PLA{_tag}", $"PLB{_tag}"],
            sharedList.GetProperty("usedBy").EnumerateArray().Select(u => u.GetProperty("typeCode").GetString()!).Order());

        var typeOnly = Assert.Single(lists, l => l.GetProperty("key").GetString() == $"type:PLA{_tag}:Rows:Kind");
        Assert.Equal("TypeOnly", typeOnly.GetProperty("scope").GetString());
        Assert.Equal(2, typeOnly.GetProperty("itemCount").GetInt32());
        Assert.StartsWith("PRM_", typeOnly.GetProperty("registryCode").GetString(), StringComparison.Ordinal);

        // Значення читаються окремим GET за ключем; ключ містить двокрапки.
        var detail = await GetAsync(client, $"type:PLA{_tag}:Rows:Kind");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(["Hazardous", "Safe"], Labels(await Json(detail)));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Таблиця_того_самого_дозволу_читається_без_значень_і_без_права_зміни()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var shared = await EnsureSharedRegistryAsync(sql, $"PLP_{_tag}");
        await PublishTypeAsync(client, $"PLP{_tag}", shared, ["A"], withSibling: true);

        var list = (await ReadListAsync(client)).Single(l => l.GetProperty("key").GetString() == $"permit:PLP{_tag}:Rows:Place");

        Assert.Equal("SamePermitTable", list.GetProperty("scope").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, list.GetProperty("itemCount").ValueKind);
        Assert.False(list.GetProperty("editable").GetBoolean());
        Assert.Empty((await Json(await GetAsync(client, list.GetProperty("key").GetString()!))).GetProperty("items").EnumerateArray());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читання_потребує_Registry_View_а_невідомий_ключ_дає_404_PRM_0404()
    {
        using var app = new EcrApiFactory(sql);
        using var reader = await SystemHealthControllerTests.SignedInAsync(sql, app, "Registry.View");

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(new Uri("/api/v1/permit-lists", UriKind.Relative))).StatusCode);
        await AssertErrorAsync(await GetAsync(reader, "shared:NOPE" + _tag), HttpStatusCode.NotFound, "ECR-PRM-0404");

        using var nobody = await SystemHealthControllerTests.SignedInAsync(sql, app, "Document.View");
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync(new Uri("/api/v1/permit-lists", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(nobody, "shared:NOPE" + _tag)).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Число_запитів_до_бази_не_росте_з_числом_типів()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var shared = await EnsureSharedRegistryAsync(sql, $"PLQ_{_tag}");
        await PublishTypeAsync(client, $"PLQ0{_tag}", shared, ["A"]);

        var few = await MeasureAsync();
        for (var i = 1; i <= 4; i++)
        {
            await PublishTypeAsync(client, $"PLQ{i}{_tag}", shared, ["A", "B"]);
        }

        var many = await MeasureAsync();

        Assert.True(few.Queries == many.Queries, $"Типів +4, а запитів {few.Queries} -> {many.Queries}.\n{few.Detail}\n{many.Detail}");

        // Проєкція стовпців + підрахунок значень; перелік типів і визначення типів по одному не читаються (раніше: 1 + N).
        Assert.True(many.Queries <= 2, $"Очікувалось не більше двох запитів, а {many.Queries}:\n{many.Detail}");
        Assert.True(many.Lists >= 5, $"Перелік не віддав списки типів: {many.Lists}.");
    }

    private async Task<(int Queries, int Lists, string Detail)> MeasureAsync()
    {
        var counter = new DbCommandCounter();
        var options = new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .AddInterceptors(counter);
        await using var db = new EcrDbContext(options.Options);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessBuilder { UserId = 9 }.Permission("Registry.View").Build());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        var handler = new ListPermitListsHandler(new PermitListStore(db), access, user);

        await handler.HandleAsync(CancellationToken.None); // прогрів службових команд EF
        counter.Tally.Reset();
        var lists = await handler.HandleAsync(CancellationToken.None);
        var seen = counter.Tally.Snapshot();

        return (seen.Total, lists.Count, seen.Format());
    }
}
