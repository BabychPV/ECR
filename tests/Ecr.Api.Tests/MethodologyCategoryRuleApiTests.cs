// tests/Ecr.Api.Tests/MethodologyCategoryRuleApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>GET/PUT/DELETE …/versions/{vid}/category-rule</c> (L-2, <c>calc.CategoryRule</c>) на справжньому SQL:
/// права View/EditRule, відмови 404/409/422 з ключами каталогу, діалект Methodology.
/// </summary>
/// <remarks>
/// ⛔ Мутації: прибрати перевірку виразу в <c>SaveMethodologyCategoryRuleHandler.RequireUsable</c> —
/// червоні 422-випадки (<c>200</c>); прибрати <c>RequireDraft</c> у <c>MethodologyVersion.SetCategoryRule</c> —
/// червоний 409-випадок; зняти <c>scope.RequireAsync</c> у контролері — червоні 403/404.
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyCategoryRuleApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Category-Rule-2026!";

    private static readonly string[] EditorPermissions = ["Calculation.View", "Calculation.EditRule"];
    private static readonly string[] ViewerPermissions = ["Calculation.View"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правила_немає_GET_віддає_null_PUT_ставить_GET_читає_DELETE_прибирає()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);

        var empty = await ReadAsync(await client.GetAsync(Url(stand)).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("expression").ValueKind);

        var saved = await ReadAsync(await PutAsync(client, stand, "!ECW_Category").ConfigureAwait(true))
            .ConfigureAwait(true);
        Assert.Equal("!ECW_Category", saved.GetProperty("expression").GetString());
        Assert.True(saved.GetProperty("updatedAt").GetDateTime() > DateTime.UtcNow.AddMinutes(-5));

        // Друге збереження переписує, а не дублює (UQ_CategoryRule_Version).
        await ReadAsync(await PutAsync(client, stand, "@Fuel").ConfigureAwait(true)).ConfigureAwait(true);
        var read = await ReadAsync(await client.GetAsync(Url(stand)).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal("@Fuel", read.GetProperty("expression").GetString());
        Assert.Equal(1, await RuleCountAsync(stand.VersionId).ConfigureAwait(true));

        var deleted = await client.DeleteAsync(Url(stand)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, await RuleCountAsync(stand.VersionId).ConfigureAwait(true));

        // Повторне видалення — не помилка (обрив між запитом і відповіддю).
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Url(stand)).ConfigureAwait(true)).StatusCode);
    }

    [Theory]
    [InlineData("", "err.ECR-CALC-0422.categoryRuleEmpty")]
    [InlineData("   ", "err.ECR-CALC-0422.categoryRuleEmpty")]
    [InlineData("@Fuel +", "err.ECR-CALC-0422.categoryRuleInvalid")]
    [InlineData("1 + 1", "err.ECR-CALC-0422.categoryRuleNotText")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Порожній_нерозібраний_або_числовий_вираз_дає_422_і_нічого_не_пишеться(
        string expression, string messageKey)
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);

        var response = await PutAsync(client, stand, expression).ConfigureAwait(true);

        var problem = await ProblemAsync(response, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);
        Assert.Equal(messageKey, problem.GetProperty("messageKey").GetString());
        Assert.Equal(0, await RuleCountAsync(stand.VersionId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Опублікована_версія_не_приймає_ні_PUT_ні_DELETE_правила()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var stand = await StandAsync(rule: "@Fuel", published: true).ConfigureAwait(true);

        var put = await PutAsync(client, stand, "@Other").ConfigureAwait(true);
        var problem = await ProblemAsync(put, HttpStatusCode.Conflict).ConfigureAwait(true);
        Assert.Equal("err.ECR-CALC-0409.draftRequired", problem.GetProperty("messageKey").GetString());

        var delete = await client.DeleteAsync(Url(stand)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);

        // Вираз лишився тим, яким версію опублікували.
        var read = await ReadAsync(await client.GetAsync(Url(stand)).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal("@Fuel", read.GetProperty("expression").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Лише_View_читає_але_не_пише_а_чужа_адреса_дає_404()
    {
        using var app = new EcrApiFactory(sql);
        using var viewer = await SignedInAsync(app, ViewerPermissions).ConfigureAwait(true);
        var stand = await StandAsync(rule: "@Fuel").ConfigureAwait(true);

        var read = await ReadAsync(await viewer.GetAsync(Url(stand)).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal("@Fuel", read.GetProperty("expression").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(viewer, stand, "@Other").ConfigureAwait(true)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync(Url(stand)).ConfigureAwait(true)).StatusCode);

        // Версія чужої методології з погляду цієї адреси не існує.
        var foreign = await viewer.GetAsync(
            new Uri($"/api/v1/methodologies/{stand.MethodologyId + 1000}/versions/{stand.VersionId}/category-rule", UriKind.Relative))
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    // ── Опора ─────────────────────────────────────────────────────────────

    private static Uri Url(Stand stand)
        => new($"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.VersionId}/category-rule", UriKind.Relative);

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Stand stand, string expression)
        => client.PutAsJsonAsync(Url(stand), new { expression });

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => await ProblemAsync(response, HttpStatusCode.OK).ConfigureAwait(false);

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<int> RuleCountAsync(int versionId)
    {
        await using var db = new EcrDbContext(Options());
        return await db.MethodologyCategoryRules.CountAsync(r => r.MethodologyVersionId == versionId).ConfigureAwait(false);
    }

    /// <summary>Методологія з версією; правило й публікація — повз обробники.</summary>
    private async Task<Stand> StandAsync(string? rule = null, bool published = false)
    {
        await using var db = new EcrDbContext(Options());
        var now = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

        var methodology = new Methodology(
            EcrCode.Create($"CR{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "L-2 category rule" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (rule is not null)
        {
            db.MethodologyCategoryRules.Add(version.SetCategoryRule(null, rule, now));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        if (published)
        {
            version.Publish(publishedByUserId: 2, "L-2", new DateOnly(2026, 1, 1), testsPassed: true, now);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return new Stand(methodology.Id, version.Id);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options;

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, string[] permissions)
    {
        var name = $"catrule_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));

            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var role = new Role(
                EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Category rule test" }));

            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private sealed record Stand(int MethodologyId, int VersionId);
}
