// tests/Ecr.Api.Tests/CurrentPeriodSettingTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ 2026-11-07 Period clarity (п. c, квіз людини 07.11): поточний період — автоматично за датою; закріплення вручну й
/// «Повернути автоматичний» — у налаштуваннях проєкту (<c>…/settings/current-period</c>). <c>D-77</c>: значення — лише
/// за замовчуванням, НІКОЛИ не рішення про доступ.
/// </summary>
/// <remarks>
/// Мутаційні докази (локально):
/// <list type="bullet">
/// <item>прибрати негайний перерахунок у <c>SetCurrentPeriodHandler</c> (гілка зняття закріплення) — після
/// «Повернути автоматичний» поточним лишається закріплений лютий, червоний.</item>
/// <item><c>CurrentPeriodSettingHandlers.ReadAsync</c> віддає <c>PinnedReason</c> і в <c>Auto</c> — червоний.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class CurrentPeriodSettingTests(SqlServerFixture sql)
{
    private const string Password = "Api-Current-Period-2026!";
    private const string Reason = "Звітуємо за лютий наперед";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.13")]
    [Trait("Decision", "D-77")]
    public async Task Закріплення_і_повернення_автоматичного_в_налаштуваннях_проєкту()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var uri = new Uri($"/api/v1/projects/{s.ProjectId}/settings/current-period", UriKind.Relative);

        var initial = await ReadAsync(await client.GetAsync(uri).ConfigureAwait(true), app).ConfigureAwait(true);
        Assert.Equal("Auto", initial.GetProperty("mode").GetString());
        Assert.Equal(s.January, initial.GetProperty("automaticPeriodKey").GetInt32());

        // Без причини — відмова домену, налаштування не змінюється.
        var noReason = await client.PutAsJsonAsync(uri, new { mode = "Pinned", pinnedPeriodId = s.FebruaryId }).ConfigureAwait(true);
        Assert.True(noReason.StatusCode == HttpStatusCode.UnprocessableEntity, $"{noReason.StatusCode}: {app.ErrorsText}");

        var pinned = await ReadAsync(
            await client.PutAsJsonAsync(uri, new { mode = "Pinned", pinnedPeriodId = s.FebruaryId, reason = Reason }).ConfigureAwait(true),
            app).ConfigureAwait(true);
        Assert.Equal("Pinned", pinned.GetProperty("mode").GetString());
        Assert.Equal(s.February, pinned.GetProperty("currentPeriodKey").GetInt32());
        Assert.Equal(Reason, pinned.GetProperty("pinnedReason").GetString());
        Assert.Equal(s.January, pinned.GetProperty("automaticPeriodKey").GetInt32());

        // «Повернути автоматичний» — поточний період одразу за датою, без очікування задачі станів.
        var auto = await ReadAsync(await client.DeleteAsync(new Uri($"{uri}/pin", UriKind.Relative)).ConfigureAwait(true), app)
            .ConfigureAwait(true);
        Assert.Equal("Auto", auto.GetProperty("mode").GetString());
        Assert.Equal(s.January, auto.GetProperty("currentPeriodKey").GetInt32());
        Assert.Equal(JsonValueKind.Null, auto.GetProperty("pinnedReason").ValueKind);

        // Застарілий маршрут лишається робочим для старого клієнта.
        var legacy = await client.PutAsJsonAsync(
            new Uri($"/api/v1/projects/{s.ProjectId}/current-period", UriKind.Relative),
            new { pinnedPeriodId = s.FebruaryId, reason = Reason }).ConfigureAwait(true);
        Assert.True(legacy.StatusCode == HttpStatusCode.NoContent, $"{legacy.StatusCode}: {app.ErrorsText}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-77")]
    [Trait("Requirement", "ФВ-1.14")]
    public async Task Закріплений_закритий_період_не_відкривається_D77()
    {
        var s = await ArrangeAsync(closeFebruary: true).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var pinned = await client.PutAsJsonAsync(
            new Uri($"/api/v1/projects/{s.ProjectId}/settings/current-period", UriKind.Relative),
            new { mode = "Pinned", pinnedPeriodId = s.FebruaryId, reason = Reason }).ConfigureAwait(true);
        Assert.True(pinned.IsSuccessStatusCode, $"{pinned.StatusCode}: {app.ErrorsText}");

        // ⛔ D-77/ФВ-1.14: закріплення — лише значення за замовчуванням; закритий місяць лишається закритим.
        var run = await client.PostAsJsonAsync(
            new Uri($"/api/v1/projects/{s.ProjectId}/recalculate", UriKind.Relative), new { periodKey = s.February })
            .ConfigureAwait(true);
        var body = await run.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(run.StatusCode == HttpStatusCode.UnprocessableEntity, $"{run.StatusCode}: {body}");
        Assert.Equal("err.ECR-CALC-4221.periodClosed", JsonDocument.Parse(body).RootElement.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_Period_Configure_змінити_не_можна_а_прочитати_можна()
    {
        var s = await ArrangeAsync(configure: false).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var uri = new Uri($"/api/v1/projects/{s.ProjectId}/settings/current-period", UriKind.Relative);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(uri).ConfigureAwait(true)).StatusCode);
        var put = await client.PutAsJsonAsync(uri, new { mode = "Pinned", pinnedPeriodId = s.FebruaryId, reason = Reason })
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, EcrApiFactory app)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    /// <summary>Проєкт із відкритими січнем і лютим; користувач із <c>Document.View</c>, <c>Calculation.Recalculate</c>
    /// і (за замовчуванням) <c>Period.Configure</c>, грант Manage.</summary>
    private async Task<Scenario> ArrangeAsync(bool configure = true, bool closeFebruary = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: 202601).ConfigureAwait(false);
        const int february = 202602;

        await using var db = builder.CreateContext();
        var january = await db.Periods
            .SingleAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == 202601).ConfigureAwait(false);
        var key = new PeriodKey(february);
        var feb = new Period(document.ProjectId, key, 2, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        db.Periods.Add(feb);
        await db.SaveChangesAsync().ConfigureAwait(false);

        january.AdvanceTo(PeriodState.Open, DateTime.UtcNow);
        feb.AdvanceTo(closeFebruary ? PeriodState.Closed : PeriodState.Open, DateTime.UtcNow);

        var role = new Role(
            EcrCode.Create($"CPS_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Current period" }));
        db.Roles.Add(role);
        var name = $"cps_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RolePermissions.Add(new RolePermission(role.Id, "Calculation.Recalculate"));
        if (configure)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, "Period.Configure"));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Manage));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(document.ProjectId, feb.Id, 202601, february, user.UserName);
    }

    private sealed record Scenario(int ProjectId, int FebruaryId, int January, int February, string UserName);
}
