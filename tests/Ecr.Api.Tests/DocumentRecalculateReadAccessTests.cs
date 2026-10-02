using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Перерахунок СВОГО документа вимагає лише читання (<c>Document.View</c> + видимість документа),
/// а не глобального <c>Calculation.Recalculate</c>; проєктний перерахунок — і далі з ним.
/// </summary>
/// <remarks>
/// Мутаційний доказ: повернути в <c>RecalculateDocumentHandler.Permission</c> значення
/// <c>Calculation.Recalculate</c> — перший тест червоний; прибрати <c>RequireVisibleAsync</c> —
/// третій (не 404) червоний; прибрати <c>RequireInAnyProjectAsync</c> — другий червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentRecalculateReadAccessTests(SqlServerFixture sql)
{
    private const string Password = "Api-Doc-RecalcRead-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Виконавець_з_читанням_перераховує_свій_документ_без_Calculation_Recalculate()
    {
        var (client, doc, app) = await SignInAsync(["Document.View"], GrantLevel.Read, grantOnProject: true).ConfigureAwait(true);
        using var _ = app;

        var response = await PostDocumentRecalcAsync(client, doc).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_Document_View_відмова_403_з_назвою_права()
    {
        var (client, doc, app) = await SignInAsync(["Calculation.Recalculate"], GrantLevel.Read, grantOnProject: true).ConfigureAwait(true);
        using var _ = app;

        var response = await PostDocumentRecalcAsync(client, doc).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("ECR-AUTH-0403", JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());
        Assert.Contains("Document.View", body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_гранта_на_документ_404()
    {
        var (client, doc, app) = await SignInAsync(["Document.View"], GrantLevel.Read, grantOnProject: false).ConfigureAwait(true);
        using var _ = app;

        var response = await PostDocumentRecalcAsync(client, doc).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Проєктний_перерахунок_без_Calculation_Recalculate_лишається_403()
    {
        var (client, doc, app) = await SignInAsync(["Document.View"], GrantLevel.Read, grantOnProject: true).ConfigureAwait(true);
        using var _ = app;

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/projects/{doc.ProjectId}/recalculate", UriKind.Relative),
            new { periodKey = doc.PeriodKey.Value, approvedByUserId = (int?)null, approvalReason = (string?)null })
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Виконавець_без_Calculation_Recalculate_ставить_без_витіснення()
    {
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var (client, doc, app) = await SignInAsync(["Document.View"], GrantLevel.Read, grantOnProject: true, jobs).ConfigureAwait(true);
        using var _ = app;

        var response = await PostDocumentRecalcAsync(client, doc).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var second = await PostDocumentRecalcAsync(client, doc).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);

        await jobs.Received(2).EnqueueCoalescedAsync<IRecalculationJob>(
            Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>()).ConfigureAwait(true);
        await jobs.DidNotReceiveWithAnyArgs().EnqueueExclusiveAsync<IRecalculationJob>(
            default!, default, default, default).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Власник_Calculation_Recalculate_витісняє_як_раніше()
    {
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var (client, doc, app) = await SignInAsync(["Document.View", "Calculation.Recalculate"], GrantLevel.Read, grantOnProject: true, jobs).ConfigureAwait(true);
        using var _ = app;

        var response = await PostDocumentRecalcAsync(client, doc).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await jobs.Received(1).EnqueueExclusiveAsync<IRecalculationJob>(
            Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>()).ConfigureAwait(true);
        await jobs.DidNotReceiveWithAnyArgs().EnqueueCoalescedAsync<IRecalculationJob>(
            default!, default, default, default).ConfigureAwait(true);
    }

    private static Task<HttpResponseMessage> PostDocumentRecalcAsync(HttpClient client, TestDocument doc)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{doc.DocumentId}/recalculate", UriKind.Relative),
            new { periodKey = doc.PeriodKey.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    private async Task<(HttpClient Client, TestDocument Doc, IDisposable App)> SignInAsync(
        string[] permissions, GrantLevel level, bool grantOnProject, IBackgroundJobScheduler? scheduler = null)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync().ConfigureAwait(false);

        await using (var db = builder.CreateContext())
        {
            var userName = $"rcrd_{Guid.NewGuid():N}"[..20];
            var user = new User(userName, userName, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(
                EcrCode.Create($"RCRD_{Guid.NewGuid():N}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Recalc read" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            if (grantOnProject)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, level));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
            _userName = userName;
        }

        var app = new EcrApiFactory(sql);
        IDisposable owner = app;
        HttpClient client;
        if (scheduler is null)
        {
            client = app.CreateClient();
        }
        else
        {
            var derived = app.WithWebHostBuilder(b => b.ConfigureTestServices(
                services => services.AddSingleton(scheduler)));
            owner = derived;
            client = derived.CreateClient();
        }

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = _userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");
        return (client, doc, owner);
    }

    private string _userName = string.Empty;
}
