// tests/Ecr.Api.Tests/ApprovalRouteApproveApiTests.cs
using System.Net;
using System.Net.Http.Json;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// X8-04 (R6), ФВ-5.17: багатоетапне погодження наскрізно — справжній
/// <c>AccessDecisionService</c> (читання <c>wf.ApprovalState.CurrentStepId</c>, роль кроку
/// через <c>RoleIdsAt</c>), справжня база й HTTP.
/// </summary>
/// <remarks>
/// ⛔ Доти доказ W1-05 був синтетичним: тест підміняв рішення правилом «у транзакції —
/// відмова», а <c>/approve</c> при налаштованому маршруті не викликав жоден тест на базі.
/// Регресія в читанні поточного кроку, у <c>route.StepAt</c>/<c>StepAfter</c> чи в мапінгу
/// конфлікту двох погоджувачів доводила б аркуш до <c>Approved</c> без решти підписів
/// (ФВ-10.11) — і жоден тест не почервонів би.
///
/// ⛔ Мутації: <c>CurrentApprovalStepAsync</c> завжди повертає перший крок — другий
/// погоджувач отримує 403, тест червоний; <c>ApproveSheetHandler</c> пише <c>Approve</c>
/// замість <c>ApproveStep</c> на проміжному кроці — аркуш стає <c>Approved</c> після першого
/// підпису, тест червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class ApprovalRouteApproveApiTests(SqlServerFixture sql)
{
    private const string Password = "Approval-Route-X804!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.17")]
    public async Task Маршрут_з_двох_кроків_погоджувач_першого_не_підписує_другий()
    {
        var b = await ArrangeDocumentAsync().ConfigureAwait(true);
        var submitter = await NewUserAsync(b, GrantLevel.Submit).ConfigureAwait(true);
        var first = await NewUserAsync(b, GrantLevel.Approve).ConfigureAwait(true);
        var second = await NewUserAsync(b, GrantLevel.Approve).ConfigureAwait(true);
        await RouteAsync(b, first.RoleId, second.RoleId).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var submitterClient = await SignInAsync(app, submitter.Name).ConfigureAwait(true);
        using var firstClient = await SignInAsync(app, first.Name).ConfigureAwait(true);
        using var secondClient = await SignInAsync(app, second.Name).ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.NoContent, SubmitAsync(submitterClient, b)).ConfigureAwait(true);

        // Крок 1: підписує перший — аркуш лишається Submitted, у журналі ApproveStep.
        await ExpectAsync(app, HttpStatusCode.NoContent, ApproveAsync(firstClient, b)).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(b).ConfigureAwait(true));
        Assert.Equal(1, await EventsAsync(b, ApprovalAction.ApproveStep).ConfigureAwait(true));

        // Крок 2 тому самому: черга не його — 403, стан не змінився.
        await ExpectAsync(app, HttpStatusCode.Forbidden, ApproveAsync(firstClient, b)).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(b).ConfigureAwait(true));

        // Крок 2: другий — Approved.
        await ExpectAsync(app, HttpStatusCode.NoContent, ApproveAsync(secondClient, b)).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Approved, await StatusAsync(b).ConfigureAwait(true));
        Assert.Equal(1, await EventsAsync(b, ApprovalAction.ApproveStep).ConfigureAwait(true));
        Assert.Equal(1, await EventsAsync(b, ApprovalAction.Approve).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.17")]
    public async Task Погоджувач_другого_кроку_не_підписує_першим()
    {
        var b = await ArrangeDocumentAsync().ConfigureAwait(true);
        var submitter = await NewUserAsync(b, GrantLevel.Submit).ConfigureAwait(true);
        var first = await NewUserAsync(b, GrantLevel.Approve).ConfigureAwait(true);
        var second = await NewUserAsync(b, GrantLevel.Approve).ConfigureAwait(true);
        await RouteAsync(b, first.RoleId, second.RoleId).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var submitterClient = await SignInAsync(app, submitter.Name).ConfigureAwait(true);
        using var secondClient = await SignInAsync(app, second.Name).ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.NoContent, SubmitAsync(submitterClient, b)).ConfigureAwait(true);

        // Грант Approve на проєкт є, але черга — ролі першого кроку.
        await ExpectAsync(app, HttpStatusCode.Forbidden, ApproveAsync(secondClient, b)).ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(b).ConfigureAwait(true));
        Assert.Equal(0, await EventsAsync(b, ApprovalAction.ApproveStep).ConfigureAwait(true));
    }

    /// <summary>
    /// Два погоджувачі ОДНОГО кроку натискають одночасно: рівно один підпис, другий —
    /// 409 (конфлікт <c>RowVersion</c>) або 403 (уже бачить наступний крок), і жодного 5xx.
    /// </summary>
    /// <remarks>⚠ Конкурентний тест — правило N/20 (CI).</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.17")]
    public async Task Два_погоджувачі_одного_кроку_одночасно_рівно_один_підпис()
    {
        var b = await ArrangeDocumentAsync().ConfigureAwait(true);
        var submitter = await NewUserAsync(b, GrantLevel.Submit).ConfigureAwait(true);
        var first = await NewUserAsync(b, GrantLevel.Approve).ConfigureAwait(true);
        var twin = await NewUserAsync(b, GrantLevel.Approve, roleId: first.RoleId).ConfigureAwait(true);
        var second = await NewUserAsync(b, GrantLevel.Approve).ConfigureAwait(true);
        await RouteAsync(b, first.RoleId, second.RoleId).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var submitterClient = await SignInAsync(app, submitter.Name).ConfigureAwait(true);
        using var firstClient = await SignInAsync(app, first.Name).ConfigureAwait(true);
        using var twinClient = await SignInAsync(app, twin.Name).ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.NoContent, SubmitAsync(submitterClient, b)).ConfigureAwait(true);

        var replies = await Task.WhenAll(ApproveAsync(firstClient, b), ApproveAsync(twinClient, b)).ConfigureAwait(true);
        var codes = replies.Select(r => r.StatusCode).ToList();
        foreach (var reply in replies)
        {
            reply.Dispose();
        }

        Assert.True(
            codes.Count(c => c == HttpStatusCode.NoContent) == 1
            && codes.Count(c => c is HttpStatusCode.Conflict or HttpStatusCode.Forbidden) == 1,
            $"очікувався рівно один 204 і один 409/403, а є {string.Join(", ", codes.Select(c => (int)c))}\n{app.ErrorsText}");
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(b).ConfigureAwait(true));
        Assert.Equal(1, await EventsAsync(b, ApprovalAction.ApproveStep).ConfigureAwait(true));
        Assert.Equal(0, await EventsAsync(b, ApprovalAction.Approve).ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private static async Task ExpectAsync(EcrApiFactory app, HttpStatusCode expected, Task<HttpResponseMessage> call)
    {
        using var response = await call.ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} замість {(int)expected}\n{body}\n{app.ErrorsText}");
    }

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, TestDocument b)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{b.DocumentId}/submit", UriKind.Relative),
            new { sheetDefId = b.SheetDefId, periodKey = b.PeriodKey.Value });

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, TestDocument b)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{b.DocumentId}/approve", UriKind.Relative),
            new { sheetDefId = b.SheetDefId, periodKey = b.PeriodKey.Value, approved = true, reason = (string?)null });

    private async Task<TestDocument> ArrangeDocumentAsync()
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(false);

        await using var db = Context();
        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return b;
    }

    /// <summary>Маршрут ПРОЄКТУ документа з кроками за ролями в заданому порядку.</summary>
    private async Task RouteAsync(TestDocument b, params int[] roleIds)
    {
        await using var db = Context();
        var route = new ApprovalRoute(
            EcrCode.Create($"X804_{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "X8-04 route" }),
            b.ProjectId);
        foreach (var roleId in roleIds)
        {
            route.AddStep(roleId);
        }

        db.ApprovalRoutes.Add(route);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Користувач із роллю: грант <paramref name="level"/> на проєкт документа. З
    /// <paramref name="roleId"/> — призначення на вже наявну роль (той самий крок маршруту).
    /// </summary>
    private async Task<(string Name, int RoleId)> NewUserAsync(TestDocument b, GrantLevel level, int? roleId = null)
    {
        var name = $"x804_{Guid.NewGuid():N}"[..20];

        await using var db = Context();
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        if (roleId is { } existing)
        {
            await db.SaveChangesAsync().ConfigureAwait(false);
            db.RoleAssignments.Add(new RoleAssignment(existing, user.Id, principalSid: null));
            await db.SaveChangesAsync().ConfigureAwait(false);

            return (name, existing);
        }

        var role = new Role(
            EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "X8-04 approver" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, b.ProjectId, level));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (name, role.Id);
    }

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string name)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private async Task<DocumentStatus?> StatusAsync(TestDocument document)
    {
        await using var db = Context();

        return await db.ApprovalStates
            .AsNoTracking()
            .Where(a => a.DocumentId == document.DocumentId
                        && a.SheetDefId == document.SheetDefId
                        && a.PeriodKey == document.PeriodKey.Value)
            .Select(a => (DocumentStatus?)a.Status)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
    }

    private async Task<int> EventsAsync(TestDocument document, ApprovalAction action)
    {
        await using var db = Context();

        return await db.ApprovalEvents
            .AsNoTracking()
            .CountAsync(e => e.DocumentId == document.DocumentId
                             && e.SheetDefId == document.SheetDefId
                             && e.PeriodKey == document.PeriodKey.Value
                             && e.Action == action)
            .ConfigureAwait(false);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
