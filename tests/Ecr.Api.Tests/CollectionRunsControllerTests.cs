// tests/Ecr.Api.Tests/CollectionRunsControllerTests.cs
using System.Net;
using System.Text.Json;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>Журнал прогонів збору крізь справжній HTTP і справжню базу (ФВ-5.23).</summary>
/// <remarks>
/// Кожен тест заводить власне вимкнене з'єднання і фільтрує за ним: спільна
/// тестова база містить чужі прогони, і твердження «рівно ці рядки» інакше
/// залежало б від порядку наборів.
/// </remarks>
[Collection("SqlServer")]
public sealed class CollectionRunsControllerTests(SqlServerFixture sql)
{
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.23")]
    public async Task Фільтри_звужують_запит_новіші_першими_курсор_доводить_до_кінця()
    {
        var w = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Integration.View").ConfigureAwait(true);

        // Сутність A: три прогони, новіші першими; сторінка по два + курсор.
        var first = await GetAsync(client, $"?entity={w.EntityA}&limit=2").ConfigureAwait(true);
        Assert.Equal([w.A3, w.A2], Ids(first));
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        var second = await GetAsync(client, $"?entity={w.EntityA}&limit=2&cursor={Uri.EscapeDataString(cursor!)}").ConfigureAwait(true);
        Assert.Equal([w.A1], Ids(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);

        // З'єднання: обидві його сутності, і нічого з іншого з'єднання.
        Assert.Equal([w.B1, w.A3, w.A2, w.A1], Ids(await GetAsync(client, $"?dataSource={w.Source}").ConfigureAwait(true)));

        // Стан — без урахування регістру.
        Assert.Equal([w.A2], Ids(await GetAsync(client, $"?entity={w.EntityA}&state=failed").ConfigureAwait(true)));

        // Проміжок за початком прогону: [from, to).
        var window = await GetAsync(
            client, $"?entity={w.EntityA}&from={T0.AddHours(1):O}&to={T0.AddHours(2):O}").ConfigureAwait(true);
        Assert.Equal([w.A2], Ids(window));

        var row = window.GetProperty("items")[0];
        Assert.Equal(w.SourceCode, row.GetProperty("dataSourceCode").GetString());
        Assert.Equal(90_000, row.GetProperty("durationMs").GetInt64());
        Assert.Equal(7, row.GetProperty("pointsRetrieved").GetInt32());
        Assert.True(row.GetProperty("hasError").GetBoolean());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.23")]
    public async Task Деталь_несе_помилку_й_покриття_а_невідомий_прогін_404()
    {
        var w = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Integration.Manage").ConfigureAwait(true);

        var detail = await GetAsync(client, $"/{w.A2}").ConfigureAwait(true);
        Assert.Equal("ECR-INT-0503: source down", detail.GetProperty("errorMessage").GetString());
        Assert.Equal(w.A2, detail.GetProperty("run").GetProperty("id").GetInt64());
        var coverage = Assert.Single(detail.GetProperty("coverage").EnumerateArray());
        Assert.Equal(T0.AddDays(-1), coverage.GetProperty("coveredFrom").GetDateTime().ToUniversalTime());

        var missing = await client.GetAsync(Url("/9223372036854775000")).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var problem = JsonDocument.Parse(await missing.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal("ECR-INT-0404", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-INT-0404.collectionRun", problem.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.23")]
    public async Task Без_права_403_а_невідомий_стан_422()
    {
        using var app = new EcrApiFactory(sql);

        using (var stranger = await SystemHealthControllerTests.SignedInAsync(sql, app, "System.ViewHealth").ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync(Url("")).ConfigureAwait(true)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync(Url("/1")).ConfigureAwait(true)).StatusCode);
        }

        using var viewer = await SystemHealthControllerTests.SignedInAsync(sql, app, "Integration.View").ConfigureAwait(true);
        var refused = await viewer.GetAsync(Url("?state=Done")).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal("err.ECR-REQ-0422.collectionRunState", problem.GetProperty("messageKey").GetString());
    }

    private static Uri Url(string tail) => new($"/api/v1/collection-runs{tail}", UriKind.Relative);

    private static async Task<JsonElement> GetAsync(HttpClient client, string tail)
    {
        var response = await client.GetAsync(Url(tail)).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {text}");

        return JsonDocument.Parse(text).RootElement;
    }

    private static long[] Ids(JsonElement page)
        => [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64())];

    /// <summary>З'єднання з двома сутностями (A — три прогони, B — один) і стороннє з'єднання з одним.</summary>
    private async Task<World> ArrangeAsync()
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

        var source = Source();
        var other = Source();
        db.DataSources.AddRange(source, other);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var a = new SourceEntity(source.Id, "ENT-A", RegistrySourceKind.External);
        var b = new SourceEntity(source.Id, "ENT-B", RegistrySourceKind.External);
        var c = new SourceEntity(other.Id, "ENT-C", RegistrySourceKind.External);

        // ⚠ Вимкнені: активна сутність із прогалиною робить `/health/ready` жовтим для сусідніх тестів.
        foreach (var entity in new[] { a, b, c })
        {
            entity.Deactivate();
        }

        db.SourceEntities.AddRange(a, b, c);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Id ростуть у порядку вставки; B1 — найновіший, C1 — чужий.
        var a1 = Run(a.Id, T0, "Succeeded", null);
        var a2 = Run(a.Id, T0.AddHours(1), "Failed", "ECR-INT-0503: source down");
        var a3 = new CollectionRun(a.Id, T0.AddDays(-1), T0, false, null, T0.AddHours(2));
        var b1 = Run(b.Id, T0.AddHours(3), "Succeeded", null);
        var c1 = Run(c.Id, T0.AddHours(4), "Succeeded", null);

        foreach (var run in new[] { a1, a2, a3, b1, c1 })
        {
            db.CollectionRuns.Add(run);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        db.CollectionCoverages.Add(new CollectionCoverage(a.Id, T0.AddDays(-1), T0, a2.Id));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new World(source.Id, source.Code, a.Id, a1.Id, a2.Id, a3.Id, b1.Id);
    }

    private static CollectionRun Run(int entityId, DateTime startedAt, string status, string? error)
    {
        var run = new CollectionRun(entityId, T0.AddDays(-1), T0, false, null, startedAt);
        run.Complete(status, 7, startedAt.AddSeconds(90), error);
        return run;
    }

    /// <summary>Вимкнене з'єднання: активне без відповіді зробило б <c>/health/ready</c> «Degraded».</summary>
    private static DataSource Source()
    {
        var code = $"CR{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var source = new DataSource(
            Ecr.Domain.ValueObjects.EcrCode.Create(code),
            new Ecr.Domain.ValueObjects.LocalizedText(new Dictionary<string, string> { ["en"] = "PI AF" }),
            ExternalTransport.PiWebApi, "https://pi.corp.example/piwebapi", $"DataSource.{code}");
        source.Deactivate();
        return source;
    }

    private sealed record World(int Source, string SourceCode, int EntityA, long A1, long A2, long A3, long B1);
}
