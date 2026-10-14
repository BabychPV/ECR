// tests/Ecr.Api.Tests/MethodologyRowActivityFilterApiTests.cs
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
/// <c>PUT /api/v1/methodologies/{id}/row-activity-filter</c> (D-425) на справжньому SQL: запис і зняття прапорця,
/// формат, право <c>Calculation.Publish</c>, 404, CSRF.
/// </summary>
/// <remarks>
/// ⛔ Мутації: замінити <c>Calculation.Publish</c> на <c>Calculation.EditFormula</c> в
/// <c>SetMethodologyRowActivityFilterHandler.Permission</c> — червоний тест 403; прибрати формат у
/// <c>Methodology.SetRowActivityFilter</c> — червоний 422; не викликати <c>SaveChangesAsync</c> — червоний
/// перший тест (прапорець не лишається в базі).
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyRowActivityFilterApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Meth-RowActivity-2026!";

    private static readonly string[] PublisherPermissions = ["Calculation.View", "Calculation.Publish"];
    private static readonly string[] EditorPermissions = ["Calculation.View", "Calculation.EditFormula"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task PUT_ставить_прапорець_повтор_ідемпотентний_а_null_знімає_його()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, PublisherPermissions).ConfigureAwait(true);
        var id = await MethodologyAsync().ConfigureAwait(true);

        var set = await client.PutAsJsonAsync(Url(id), new { filter = "LAND_GENERIC_EQUIPSTATUS:Running" })
            .ConfigureAwait(true);
        var body = await ReadAsync(set, HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal(id, body.GetProperty("methodologyId").GetInt32());
        Assert.Equal("LAND_GENERIC_EQUIPSTATUS:Running", body.GetProperty("rowActivityFilter").GetString());
        Assert.Equal("LAND_GENERIC_EQUIPSTATUS:Running", await StoredAsync(id).ConfigureAwait(true));

        await ReadAsync(
            await client.PutAsJsonAsync(Url(id), new { filter = "LAND_GENERIC_EQUIPSTATUS:Running" }).ConfigureAwait(true),
            HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal("LAND_GENERIC_EQUIPSTATUS:Running", await StoredAsync(id).ConfigureAwait(true));

        var cleared = await ReadAsync(
            await client.PutAsJsonAsync(Url(id), new { filter = (string?)null }).ConfigureAwait(true),
            HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("rowActivityFilter").ValueKind);
        Assert.Null(await StoredAsync(id).ConfigureAwait(true));
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("A:B:C")]
    [InlineData("REG CODE:Running")]
    [InlineData(":Running")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зіпсований_формат_дає_422_з_ключем_і_прапорець_не_змінюється(string filter)
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, PublisherPermissions).ConfigureAwait(true);
        var id = await MethodologyAsync().ConfigureAwait(true);

        var response = await client.PutAsJsonAsync(Url(id), new { filter }).ConfigureAwait(true);

        var problem = await ReadAsync(response, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);
        Assert.Equal("err.ECR-CALC-0422.rowActivityFilterFormat", problem.GetProperty("messageKey").GetString());
        Assert.Null(await StoredAsync(id).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_Publish_403_а_невідома_методологія_404()
    {
        using var app = new EcrApiFactory(sql);
        using var editor = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        using var publisher = await SignedInAsync(app, PublisherPermissions).ConfigureAwait(true);
        var id = await MethodologyAsync().ConfigureAwait(true);

        var denied = await editor.PutAsJsonAsync(Url(id), new { filter = "LAND_GENERIC_EQUIPSTATUS:Running" })
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Null(await StoredAsync(id).ConfigureAwait(true));

        var missing = await publisher.PutAsJsonAsync(Url(id + 1_000_000), new { filter = "A:B" }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("Origin", "https://evil.example")]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task PUT_з_чужого_сайту_дає_403_і_прапорець_не_змінюється(string header, string value)
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, PublisherPermissions).ConfigureAwait(true);
        var id = await MethodologyAsync().ConfigureAwait(true);

        using var put = new HttpRequestMessage(HttpMethod.Put, Url(id))
        {
            Content = JsonContent.Create(new { filter = "LAND_GENERIC_EQUIPSTATUS:Running" }),
        };
        put.Headers.Add(header, value);
        using var response = await client.SendAsync(put).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await StoredAsync(id).ConfigureAwait(true));
    }

    private static Uri Url(int methodologyId)
        => new($"/api/v1/methodologies/{methodologyId}/row-activity-filter", UriKind.Relative);

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<string?> StoredAsync(int id)
    {
        await using var db = new EcrDbContext(Options());
        return await db.Methodologies.AsNoTracking().Where(m => m.Id == id)
            .Select(m => m.RowActivityFilter).SingleAsync().ConfigureAwait(false);
    }

    private async Task<int> MethodologyAsync()
    {
        await using var db = new EcrDbContext(Options());
        var methodology = new Methodology(
            EcrCode.Create($"RA{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "D-425 row activity" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return methodology.Id;
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options;

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, string[] permissions)
    {
        var name = $"mact_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));

            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var role = new Role(
                EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Row activity test" }));

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
}
