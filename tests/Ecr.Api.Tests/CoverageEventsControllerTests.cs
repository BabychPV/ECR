// tests/Ecr.Api.Tests/CoverageEventsControllerTests.cs
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

/// <summary>
/// Події журналу покриття крізь справжній HTTP і справжню базу (ІНТ-3.3, <c>D-118</c>).
/// </summary>
/// <remarks>
/// ⛔ До цього ендпоінта статусні рядки <c>itg.CollectionCoverage</c>
/// (<c>CollectionRunId = null</c>, Q-186) не читав ніхто: ні деталь прогону,
/// ні жоден екран. Кожен тест заводить власні з'єднання й фільтрує за ними —
/// спільна тестова база містить чужі рядки.
/// </remarks>
[Collection("SqlServer")]
public sealed class CoverageEventsControllerTests(SqlServerFixture sql)
{
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ІНТ-3.3")]
    public async Task Видно_лише_події_зі_статусом_фільтри_звужують_курсор_доводить_до_кінця()
    {
        var w = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Integration.View").ConfigureAwait(true);

        // З'єднання: чотири події обох його сутностей, новіші першими; ні
        // звичайного інтервалу (статус NULL), ні події чужого з'єднання.
        var all = await GetAsync(client, $"?dataSource={w.Source}").ConfigureAwait(true);
        Assert.Equal([w.BCeiling, w.AConflict, w.ACeiling, w.AClosed], Ids(all));
        Assert.DoesNotContain(w.Interval, Ids(all));

        // Сутність A: сторінка по два + курсор.
        var first = await GetAsync(client, $"?entity={w.EntityA}&limit=2").ConfigureAwait(true);
        Assert.Equal([w.AConflict, w.ACeiling], Ids(first));
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        var second = await GetAsync(client, $"?entity={w.EntityA}&limit=2&cursor={Uri.EscapeDataString(cursor!)}").ConfigureAwait(true);
        Assert.Equal([w.AClosed], Ids(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);

        // Статус — без урахування регістру; період — точний збіг.
        Assert.Equal(
            [w.BCeiling, w.ACeiling],
            Ids(await GetAsync(client, $"?dataSource={w.Source}&status=skippedpointceiling").ConfigureAwait(true)));
        Assert.Equal(
            [w.AClosed],
            Ids(await GetAsync(client, $"?dataSource={w.Source}&periodKey=202608").ConfigureAwait(true)));

        var row = all.GetProperty("items")[2];
        Assert.Equal("ENT-A", row.GetProperty("sourceEntityCode").GetString());
        Assert.Equal(w.SourceCode, row.GetProperty("dataSourceCode").GetString());
        Assert.Equal(202609, row.GetProperty("periodKey").GetInt32());
        Assert.Equal(CollectionCoverage.SkippedPointCeiling, row.GetProperty("status").GetString());
        Assert.Equal("TOTAL: 120000 points, ceiling 100000", row.GetProperty("details").GetString());
        Assert.Equal(T0.AddHours(2), row.GetProperty("at").GetDateTime().ToUniversalTime());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ІНТ-3.3")]
    public async Task Без_права_403_а_невідомий_статус_422()
    {
        using var app = new EcrApiFactory(sql);

        using (var stranger = await SystemHealthControllerTests.SignedInAsync(sql, app, "System.ViewHealth").ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync(Url("")).ConfigureAwait(true)).StatusCode);
        }

        using var manager = await SystemHealthControllerTests.SignedInAsync(sql, app, "Integration.Manage").ConfigureAwait(true);
        var refused = await manager.GetAsync(Url("?status=Skipped")).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal("err.ECR-REQ-0422.coverageEventStatus", problem.GetProperty("messageKey").GetString());
    }

    private static Uri Url(string tail) => new($"/api/v1/collection-runs/coverage-events{tail}", UriKind.Relative);

    private static async Task<JsonElement> GetAsync(HttpClient client, string tail)
    {
        var response = await client.GetAsync(Url(tail)).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {text}");

        return JsonDocument.Parse(text).RootElement;
    }

    private static long[] Ids(JsonElement page)
        => [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64())];

    /// <summary>
    /// З'єднання з сутностями A (три події + звичайний інтервал прогону) і B
    /// (одна подія) та стороннє з'єднання з однією подією.
    /// </summary>
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

        var run = new CollectionRun(a.Id, T0.AddDays(-1), T0, false, null, T0);
        run.Complete("Succeeded", 7, T0.AddMinutes(1), null);
        db.CollectionRuns.Add(run);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Id ростуть у порядку вставки — по одному SaveChanges на рядок.
        var aClosed = await AddAsync(db, CollectionCoverage.Skipped(
            a.Id, 202608, CollectionCoverage.SkippedPeriodClosed, "Period 202608 is closed", T0.AddHours(1))).ConfigureAwait(false);
        var interval = await AddAsync(db, new CollectionCoverage(a.Id, T0.AddDays(-1), T0, run.Id)).ConfigureAwait(false);
        var aCeiling = await AddAsync(db, CollectionCoverage.Skipped(
            a.Id, 202609, CollectionCoverage.SkippedPointCeiling, "TOTAL: 120000 points, ceiling 100000", T0.AddHours(2))).ConfigureAwait(false);
        var aConflict = await AddAsync(db, CollectionCoverage.Skipped(
            a.Id, 202609, CollectionCoverage.ConflictKeptManual, "Manual value kept in row 3", T0.AddHours(3))).ConfigureAwait(false);
        var bCeiling = await AddAsync(db, CollectionCoverage.Skipped(
            b.Id, 202609, CollectionCoverage.SkippedPointCeiling, "FLOW: 9 points, ceiling 8", T0.AddHours(4))).ConfigureAwait(false);
        await AddAsync(db, CollectionCoverage.Skipped(
            c.Id, 202609, CollectionCoverage.SkippedPointCeiling, "foreign", T0.AddHours(5))).ConfigureAwait(false);

        return new World(source.Id, source.Code, a.Id, aClosed, aCeiling, aConflict, bCeiling, interval);
    }

    private static async Task<long> AddAsync(EcrDbContext db, CollectionCoverage row)
    {
        db.CollectionCoverages.Add(row);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return row.Id;
    }

    /// <summary>Вимкнене з'єднання: активне без відповіді зробило б <c>/health/ready</c> «Degraded».</summary>
    private static DataSource Source()
    {
        var code = $"CE{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var source = new DataSource(
            Ecr.Domain.ValueObjects.EcrCode.Create(code),
            new Ecr.Domain.ValueObjects.LocalizedText(new Dictionary<string, string> { ["en"] = "PI AF" }),
            ExternalTransport.PiWebApi, "https://pi.corp.example/piwebapi", $"DataSource.{code}");
        source.Deactivate();
        return source;
    }

    private sealed record World(
        int Source, string SourceCode, int EntityA,
        long AClosed, long ACeiling, long AConflict, long BCeiling, long Interval);
}
