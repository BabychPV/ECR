// tests/Ecr.Api.Tests/RecalculationApprovalTests.cs
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
/// «Чотири ока» на перерахунок закритого періоду (ФВ-9.7, аудит безпеки S1):
/// погодження — окрема сутність, друга людина підтверджує ВЛАСНОЮ сесією.
/// </summary>
/// <remarks>
/// ⛔ До фіксу <c>POST /projects/{id}/recalculate</c> брав <c>approvedByUserId</c>
/// з тіла: ініціатор вписував будь-кого «другою людиною» і отримував 202.
/// Тести йдуть реальним HTTP двома окремими сесіями — саме так, як експлойт.
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationApprovalTests(SqlServerFixture sql)
{
    private const string Password = "Api-Recalc-Approval-2026!";
    private const int Closed = 202601;
    private const int OtherClosed = 202602;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.7")]
    public async Task Вписаний_у_тіло_погоджувач_не_відкриває_закритий_період()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var initiator = await SignedInAsync(app, s.Initiator).ConfigureAwait(true);

        var response = await initiator.PostAsJsonAsync(
            Recalculate(s.ProjectId),
            new { periodKey = Closed, approvedByUserId = s.ApproverId, approvalReason = "Помилка коефіцієнта" })
            .ConfigureAwait(true);

        await AssertRecalculationDeniedAsync(response, app).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.7")]
    public async Task Ініціатор_не_підтверджує_власне_погодження()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var initiator = await SignedInAsync(app, s.Initiator).ConfigureAwait(true);

        var id = await RequestAsync(initiator, app, s.ProjectId, Closed).ConfigureAwait(true);
        var response = await ConfirmAsync(initiator, s.ProjectId, id).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-CALC-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-CALC-0409.ownRecalculationApproval", problem.GetProperty("messageKey").GetString());

        // І непідтверджене погодження перерахунку не відкриває.
        await AssertRecalculationDeniedAsync(
            await RunAsync(initiator, s.ProjectId, Closed, id).ConfigureAwait(true), app).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.7")]
    public async Task Дві_сесії_перераховують_один_раз_і_лишають_слід()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var initiator = await SignedInAsync(app, s.Initiator).ConfigureAwait(true);
        using var approver = await SignedInAsync(app, s.Approver).ConfigureAwait(true);

        var id = await RequestAsync(initiator, app, s.ProjectId, Closed).ConfigureAwait(true);

        // Друга людина бачить запит у переліку проєкту — з нього її кнопка.
        var list = await approver.GetFromJsonAsync<JsonElement>(Approvals(s.ProjectId)).ConfigureAwait(true);
        Assert.Contains(list.EnumerateArray(), a => a.GetProperty("id").GetInt64() == id);

        var confirm = await ConfirmAsync(approver, s.ProjectId, id).ConfigureAwait(true);
        Assert.True(confirm.IsSuccessStatusCode, $"{confirm.StatusCode}: {await confirm.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");
        var confirmed = await confirm.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(true);
        Assert.Equal(s.ApproverId, confirmed.GetProperty("confirmedByUserId").GetInt32());

        // ⛔ Погодження одноразове: чуже (підтверджене, але не своє) не працює…
        await AssertRecalculationDeniedAsync(
            await RunAsync(approver, s.ProjectId, Closed, id).ConfigureAwait(true), app).ConfigureAwait(true);

        // …своє — рівно один раз.
        var run = await RunAsync(initiator, s.ProjectId, Closed, id).ConfigureAwait(true);
        Assert.True(run.StatusCode == HttpStatusCode.Accepted, $"{run.StatusCode}: {await run.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");

        await AssertRecalculationDeniedAsync(
            await RunAsync(initiator, s.ProjectId, Closed, id).ConfigureAwait(true), app).ConfigureAwait(true);

        var after = await approver.GetFromJsonAsync<JsonElement>(Approvals(s.ProjectId)).ConfigureAwait(true);
        Assert.DoesNotContain(after.EnumerateArray(), a => a.GetProperty("id").GetInt64() == id);

        // Слід: запит — ініціатором, підтвердження — погоджувачем, використання — ініціатором.
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var events = await db.Database
            .SqlQuery<string>($"SELECT EventType + N'|' + CAST(ChangedByUserId AS nvarchar(20)) AS Value FROM aud.SecurityEvent WHERE EventType LIKE N'RecalculationApproval%' AND JSON_VALUE(DetailsJson, '$.approvalId') = {id.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
            .ToListAsync().ConfigureAwait(true);

        Assert.Equal(
            [
                $"RecalculationApprovalConfirmed|{s.ApproverId}",
                $"RecalculationApprovalRequested|{s.InitiatorId}",
                $"RecalculationApprovalUsed|{s.InitiatorId}",
            ],
            events.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.7")]
    public async Task Погодження_одного_періоду_не_відкриває_інший()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var initiator = await SignedInAsync(app, s.Initiator).ConfigureAwait(true);
        using var approver = await SignedInAsync(app, s.Approver).ConfigureAwait(true);

        var id = await RequestAsync(initiator, app, s.ProjectId, Closed).ConfigureAwait(true);
        var confirm = await ConfirmAsync(approver, s.ProjectId, id).ConfigureAwait(true);
        Assert.True(confirm.IsSuccessStatusCode, $"{confirm.StatusCode}: {app.ErrorsText}");

        await AssertRecalculationDeniedAsync(
            await RunAsync(initiator, s.ProjectId, OtherClosed, id).ConfigureAwait(true), app).ConfigureAwait(true);
    }

    private static async Task AssertRecalculationDeniedAsync(HttpResponseMessage response, EcrApiFactory app)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(
            response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"Очікувалась відмова 422, а прийшло {response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal("ECR-CALC-4221", JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());
    }

    private static async Task<long> RequestAsync(HttpClient client, EcrApiFactory app, int projectId, int periodKey)
    {
        var response = await client.PostAsJsonAsync(
            Approvals(projectId), new { periodKey, reason = "Помилка коефіцієнта, лист №17" }).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt64();
    }

    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, int projectId, long id)
        => client.PostAsync(
            new Uri($"/api/v1/projects/{projectId}/recalculation-approvals/{id}/confirm", UriKind.Relative), null);

    private static Task<HttpResponseMessage> RunAsync(HttpClient client, int projectId, int periodKey, long approvalId)
        => client.PostAsJsonAsync(Recalculate(projectId), new { periodKey, approvalId });

    private static Uri Approvals(int projectId)
        => new($"/api/v1/projects/{projectId}/recalculation-approvals", UriKind.Relative);

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

    /// <summary>
    /// Проєкт із двома закритими періодами і дві людини з ОДНАКОВИМИ правами:
    /// перерахунок + Manage на проєкт. Однакові навмисно — інакше відмову в
    /// самопідтвердженні можна було б списати на брак права, а не на правило.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: Closed).ConfigureAwait(false);

        await using var db = builder.CreateContext();

        var period = await db.Periods
            .SingleAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == Closed)
            .ConfigureAwait(false);
        period.AdvanceTo(PeriodState.Closed, DateTime.UtcNow);

        var other = new PeriodKey(OtherClosed);
        var otherPeriod = new Period(
            document.ProjectId, other, (byte)other.Sequence,
            new DateOnly(other.Year, other.Sequence, 1),
            new DateOnly(other.Year, other.Sequence, DateTime.DaysInMonth(other.Year, other.Sequence)));
        db.Periods.Add(otherPeriod);
        await db.SaveChangesAsync().ConfigureAwait(false);
        otherPeriod.AdvanceTo(PeriodState.Closed, DateTime.UtcNow);

        var role = new Role(
            EcrCode.Create($"RCAPR_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Recalc approval" }));
        db.Roles.Add(role);

        var initiator = NewUser("rcai");
        var approver = NewUser("rcaa");
        db.Users.AddRange(initiator, approver);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Calculation.Recalculate"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, initiator.Id, null));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, approver.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Manage));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(document.ProjectId, initiator.UserName, initiator.Id, approver.UserName, approver.Id);
    }

    private static User NewUser(string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        return user;
    }

    private sealed record Scenario(int ProjectId, string Initiator, int InitiatorId, string Approver, int ApproverId);
}
