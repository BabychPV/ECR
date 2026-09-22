// tests/Ecr.Api.Tests/DocumentRecalculateDenialTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>POST /api/v1/documents/{id}/recalculate</c> — відмова <c>ECR-CALC-4221</c>
/// наскрізно: ключ за причиною, нейтральний заголовок, англійська подробиця.
/// </summary>
/// <remarks>
/// Сирі <c>periodKey</c> (число) і <c>denial</c> перевіряються тут же: їх читає
/// клієнт, і локалізація не має права їх зачепити.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentRecalculateDenialTests(SqlServerFixture sql)
{
    private const string Password = "Api-Doc-Recalc-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Закритий_період_дає_422_з_ключем_periodClosed()
    {
        var problem = await DenialAsync(closePeriod: true, submitSheet: false).ConfigureAwait(true);

        Assert.Equal("err.ECR-CALC-4221.periodClosed", problem.GetProperty("messageKey").GetString());
        Assert.Equal("PeriodClosed", problem.GetProperty("denial").GetString());
        Assert.StartsWith($"Period {problem.GetProperty("periodKey").GetInt32()} is closed", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Поданий_аркуш_дає_422_з_ключем_sheetsSubmitted()
    {
        var problem = await DenialAsync(closePeriod: false, submitSheet: true).ConfigureAwait(true);

        Assert.Equal("err.ECR-CALC-4221.sheetsSubmitted", problem.GetProperty("messageKey").GetString());
        Assert.Equal("SheetsSubmitted", problem.GetProperty("denial").GetString());
        Assert.StartsWith($"Period {problem.GetProperty("periodKey").GetInt32()} has submitted sheets", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    /// <summary>Готує документ, шле перерахунок і повертає тіло відмови.</summary>
    private async Task<JsonElement> DenialAsync(bool closePeriod, bool submitSheet)
    {
        var s = await ArrangeAsync(closePeriod, submitSheet).ConfigureAwait(false);

        using var app = new EcrApiFactory(sql);
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = s.UserName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/recalculate", UriKind.Relative),
            new { periodKey = s.Document.PeriodKey.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(
            response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        var problem = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal("ECR-CALC-4221", problem.GetProperty("errorCode").GetString());
        Assert.Equal("Recalculation is not allowed", problem.GetProperty("title").GetString());
        Assert.Equal(s.Document.PeriodKey.Value, problem.GetProperty("periodKey").GetInt32());
        return problem;
    }

    private async Task<Scenario> ArrangeAsync(bool closePeriod, bool submitSheet)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync().ConfigureAwait(false);

        await using var db = builder.CreateContext();

        var period = await db.Periods
            .SingleAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value)
            .ConfigureAwait(false);
        period.AdvanceTo(closePeriod ? PeriodState.Closed : PeriodState.Open, DateTime.UtcNow);

        if (submitSheet)
        {
            var state = new ApprovalState(document.DocumentId, document.SheetDefId, document.PeriodKey.Value);
            state.Submit(1, DateTime.UtcNow);
            db.ApprovalStates.Add(state);
        }

        var userName = $"docrec_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"DOCREC_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Doc recalc" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Calculation.Recalculate"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Write));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(document, userName);
    }

    private sealed record Scenario(TestDocument Document, string UserName);
}
