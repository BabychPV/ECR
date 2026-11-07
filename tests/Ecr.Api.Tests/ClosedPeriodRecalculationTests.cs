// tests/Ecr.Api.Tests/ClosedPeriodRecalculationTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ 07.11 (D-468, квіз людини): закритий місяць має ОДИН шлях — Reopen (з причиною) → звичайний перерахунок.
/// Погодження перерахунку закритого періоду (<c>…/recalculation-approvals</c>, аудит S1) прибрано разом з ендпоінтами.
/// </summary>
/// <remarks>
/// Реальний HTTP і SQL. Мутаційні докази:
/// <list type="bullet">
/// <item><see cref="Закритий_період_без_Reopen_422_periodClosed_і_approvalId_нічого_не_відкриває"/>:
/// <c>if (state == PeriodState.Closed)</c> → <c>if (false)</c> у <c>RecalculationWritePolicy.Check</c> — 202 замість 422.</item>
/// <item><see cref="Після_Reopen_звичайний_перерахунок_202"/>: у <c>RecalculationWritePolicy.Check</c> відмовляти й на
/// <c>Grace</c> (<c>state is Closed or Grace</c>) — 422 замість 202 (шлях Reopen став би глухим кутом).</item>
/// <item><see cref="Ендпоінтів_погоджень_більше_немає"/>: повернути <c>RecalculationApprovalsController</c> — 201/200 замість 404.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class ClosedPeriodRecalculationTests(SqlServerFixture sql)
{
    private const string Password = "Api-Closed-Recalc-2026!";
    private const int Closed = 202601;

    /// <summary>Коректний за форматом, але в проєкті такого періоду немає.</summary>
    private const int Missing = 203012;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.7")]
    [Trait("Decision", "D-468")]
    public async Task Закритий_період_без_Reopen_422_periodClosed_і_approvalId_нічого_не_відкриває()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        // Старий клієнт ще надсилає `approvalId` (і навіть вписаного «погоджувача»): поле ігнорується, відмова та сама.
        var response = await client.PostAsJsonAsync(
            Recalculate(s.ProjectId), new { periodKey = Closed, approvalId = 1L, approvedByUserId = s.UserId })
            .ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-CALC-4221", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-CALC-4221.periodClosed", problem.GetProperty("messageKey").GetString());

        // Повний рік: закритий січень не відмовляє всьому запиту, а вилучається зі скоупу (UAT-recalc-ux 5549eec1a) —
        // перерахунок року не заходить у закритий місяць, і без Reopen той лишається недоторканим.
        var year = await client.PostAsJsonAsync(Recalculate(s.ProjectId), new { periodKey = (int?)null }).ConfigureAwait(true);
        Assert.True(year.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK, $"{year.StatusCode}: {app.ErrorsText}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.7")]
    [Trait("Requirement", "ФВ-1.10")]
    [Trait("Decision", "D-468")]
    public async Task Після_Reopen_звичайний_перерахунок_202()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var reopen = await client.PostAsJsonAsync(
            new Uri($"/api/v1/periods/{s.PeriodId}/reopen", UriKind.Relative),
            new { reason = "Помилка коефіцієнта, лист №17", until = (DateTime?)null }).ConfigureAwait(true);
        Assert.True(reopen.IsSuccessStatusCode, $"reopen {reopen.StatusCode}: {await reopen.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");

        var run = await client.PostAsJsonAsync(Recalculate(s.ProjectId), new { periodKey = Closed }).ConfigureAwait(true);
        var body = await run.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(run.StatusCode == HttpStatusCode.Accepted, $"{run.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.False(string.IsNullOrEmpty(JsonDocument.Parse(body).RootElement.GetProperty("jobId").GetString()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-468")]
    public async Task Ендпоінтів_погоджень_більше_немає()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var approvals = new Uri($"/api/v1/projects/{s.ProjectId}/recalculation-approvals", UriKind.Relative);
        var create = await client.PostAsJsonAsync(approvals, new { periodKey = Closed, reason = "x" }).ConfigureAwait(true);
        var list = await client.GetAsync(approvals).ConfigureAwait(true);

        Assert.True(create.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{create.StatusCode}");
        Assert.True(list.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{list.StatusCode}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.7")]
    [Trait("Scenario", "Н-К1")]
    public async Task Неіснуючий_період_404_periodForProject()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await client.PostAsJsonAsync(Recalculate(s.ProjectId), new { periodKey = Missing }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-PRD-0404", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-PRD-0404.periodForProject", problem.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.7")]
    public async Task Нижче_порогу_гранта_перерахунок_403()
    {
        var below = (GrantLevel)((byte)RecalculationGrantPolicy.InitiatorGrant - 1);
        var s = await ArrangeAsync(below).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var run = await client.PostAsJsonAsync(Recalculate(s.ProjectId), new { periodKey = Closed }).ConfigureAwait(true);
        Assert.True(run.StatusCode == HttpStatusCode.Forbidden, $"{run.StatusCode}: {app.ErrorsText}");
    }

    private static Uri Recalculate(int projectId)
        => new($"/api/v1/projects/{projectId}/recalculate", UriKind.Relative);

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    /// <summary>Проєкт із закритим січнем; користувач із <c>Calculation.Recalculate</c> і <c>Period.Reopen</c>.</summary>
    private async Task<Scenario> ArrangeAsync(GrantLevel grant = GrantLevel.Manage)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: Closed).ConfigureAwait(false);

        await using var db = builder.CreateContext();

        var period = await db.Periods
            .SingleAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == Closed)
            .ConfigureAwait(false);
        period.AdvanceTo(PeriodState.Closed, DateTime.UtcNow);

        var role = new Role(
            EcrCode.Create($"CPRC_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Closed period recalc" }));
        db.Roles.Add(role);

        var name = $"cprc_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Calculation.Recalculate"));
        db.RolePermissions.Add(new RolePermission(role.Id, "Period.Reopen"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        if (grant > GrantLevel.None)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, grant));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(document.ProjectId, period.Id, user.UserName, user.Id);
    }

    private sealed record Scenario(int ProjectId, int PeriodId, string UserName, int UserId);
}
