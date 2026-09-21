// tests/Ecr.Api.Tests/UserPreferencesApiTests.cs
using System.Net;
using System.Text;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary><c>/api/v1/me/preferences</c> (<c>BE-20</c>): лише власні, з лімітами.</summary>
[Collection("SqlServer")]
public sealed class UserPreferencesApiTests(SqlServerFixture sql)
{
    private static readonly Uri List = new("/api/v1/me/preferences", UriKind.Relative);

    private static Uri At(string key) => new($"/api/v1/me/preferences/{key}", UriKind.Relative);

    private static StringContent Json(string raw) => new(raw, Encoding.UTF8, "application/json");

    private Task<HttpClient> SignedInAsync(EcrApiFactory app)
        => SystemHealthControllerTests.SignedInAsync(sql, app);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task AssertInvalidAsync(HttpResponseMessage response, string messageKey)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{response.StatusCode}: {body}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-REQ-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal(messageKey, problem.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Запис_читання_заміна_і_видалення_налаштування()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);

        var put = await ReadAsync(await client.PutAsync(At("theme"), Json("\"dark\"")).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal("dark", put.GetProperty("value").GetString());

        await ReadAsync(await client.PutAsync(At("grid.columnWidths.42"), Json("""{ "c1": 120 }""")).ConfigureAwait(true)).ConfigureAwait(true);
        await ReadAsync(await client.PutAsync(At("theme"), Json("\"light\"")).ConfigureAwait(true)).ConfigureAwait(true);

        var all = await ReadAsync(await client.GetAsync(List).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(["grid.columnWidths.42", "theme"], all.EnumerateArray().Select(p => p.GetProperty("key").GetString()));
        Assert.Equal("light", all[1].GetProperty("value").GetString());
        Assert.Equal(120, all[0].GetProperty("value").GetProperty("c1").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(At("theme")).ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(At("theme")).ConfigureAwait(true)).StatusCode);

        all = await ReadAsync(await client.GetAsync(List).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(["grid.columnWidths.42"], all.EnumerateArray().Select(p => p.GetProperty("key").GetString()));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Користувач_не_бачить_і_не_змінює_чужих_налаштувань()
    {
        using var app = new EcrApiFactory(sql);
        using var alice = await SignedInAsync(app).ConfigureAwait(true);
        using var bob = await SignedInAsync(app).ConfigureAwait(true);

        await ReadAsync(await alice.PutAsync(At("density"), Json("\"compact\"")).ConfigureAwait(true)).ConfigureAwait(true);

        var bobs = await ReadAsync(await bob.GetAsync(List).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(0, bobs.GetArrayLength());

        await ReadAsync(await bob.PutAsync(At("density"), Json("\"comfortable\"")).ConfigureAwait(true)).ConfigureAwait(true);
        await bob.DeleteAsync(At("density")).ConfigureAwait(true);

        var alices = await ReadAsync(await alice.GetAsync(List).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal("compact", Assert.Single(alices.EnumerateArray()).GetProperty("value").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ключ_поза_білим_списком_і_кривий_ключ_відхиляються()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);

        await AssertInvalidAsync(
            await client.PutAsync(At("password"), Json("1")).ConfigureAwait(true),
            "err.ECR-REQ-0422.preferenceKeyInvalid").ConfigureAwait(true);
        await AssertInvalidAsync(
            await client.PutAsync(At("grid.a b"), Json("1")).ConfigureAwait(true),
            "err.ECR-REQ-0422.preferenceKeyInvalid").ConfigureAwait(true);
        await AssertInvalidAsync(
            await client.PutAsync(At("grid." + new string('x', 96)), Json("1")).ConfigureAwait(true),
            "err.ECR-REQ-0422.preferenceKeyInvalid").ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Значення_понад_8192_байти_відхиляється_а_рівно_8192_приймається()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);

        // JSON-рядок із n літер займає n + 2 байти (лапки).
        await ReadAsync(await client.PutAsync(At("grid.big"), Json($"\"{new string('a', 8190)}\"")).ConfigureAwait(true)).ConfigureAwait(true);
        await AssertInvalidAsync(
            await client.PutAsync(At("grid.big"), Json($"\"{new string('a', 8191)}\"")).ConfigureAwait(true),
            "err.ECR-REQ-0422.preferenceValueTooLarge").ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Двохсот_перший_ключ_відхиляється_а_заміна_наявного_ні()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);

        var me = await ReadAsync(await client.GetAsync(new Uri("/api/v1/me", UriKind.Relative)).ConfigureAwait(true)).ConfigureAwait(true);
        var userId = me.GetProperty("userId").GetInt32();

        var options = new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;
        await using (var db = new EcrDbContext(options))
        {
            for (var i = 0; i < 200; i++)
            {
                db.UserPreferences.Add(new UserPreference(userId, $"grid.k{i}", "1", DateTime.UtcNow));
            }

            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        await AssertInvalidAsync(
            await client.PutAsync(At("theme"), Json("\"dark\"")).ConfigureAwait(true),
            "err.ECR-REQ-0422.preferenceLimitReached").ConfigureAwait(true);
        await ReadAsync(await client.PutAsync(At("grid.k7"), Json("2")).ConfigureAwait(true)).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_входу_401()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(List).ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsync(At("theme"), Json("1")).ConfigureAwait(true)).StatusCode);
    }
}
