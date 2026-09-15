// tests/Ecr.Api.Tests/TemplateStructureGroupRulesTests.cs
using System.Net;
using System.Net.Http.Json;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Директива "live-попередження про порушення SheetGroupRule": <c>GET
/// .../structure</c> тепер несе склад <c>SheetGroupRule</c> разом зі
/// структурою, щоб клієнт (<c>CreateDocumentModal.tsx</c>) міг порахувати
/// порушення локально при кожній зміні вибору аркушів — до цього поля в
/// <c>TemplateStructureDto</c> не було взагалі, і єдиний спосіб дізнатися
/// про порушення складу був відхилений <c>POST /documents</c>.
/// </summary>
[Collection("SqlServer")]
public sealed class TemplateStructureGroupRulesTests(SqlServerFixture sql)
{
    private const string LoginPassword = "Structure-Rules-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Структура_несе_правила_складу_груп_аркушів()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var (userName, templateVersionId) = await ArrangeAsync().ConfigureAwait(true);

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = LoginPassword }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        var response = await client.GetAsync(
            new Uri($"/api/v1/template-versions/{templateVersionId}/structure", UriKind.Relative))
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content
            .ReadFromJsonAsync<System.Text.Json.JsonElement>().ConfigureAwait(true);
        var rules = body.GetProperty("groupRules").EnumerateArray().ToList();

        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, r => r.GetProperty("sheetGroup").GetString() == "Water"
                                     && r.GetProperty("ruleKind").GetInt32() == 0);
        Assert.Contains(rules, r => r.GetProperty("sheetGroup").GetString() == "Air"
                                     && r.GetProperty("ruleKind").GetInt32() == 1);
    }

    /// <summary>Користувач із правом <c>Template.View</c> і версія із двома правилами складу.</summary>
    private async Task<(string UserName, int TemplateVersionId)> ArrangeAsync()
    {
        var userName = $"grouprules_{Guid.NewGuid():N}"[..20];

        await using var db = new Ecr.Infrastructure.Persistence.EcrDbContext(
            new DbContextOptionsBuilder<Ecr.Infrastructure.Persistence.EcrDbContext>()
                .UseSqlServer(sql.ConnectionString)
                .Options);

        var user = new Ecr.Domain.Entities.Security.User(
            userName, userName, Ecr.Domain.Enums.AuthProvider.Local);
        user.SetPassword(new Ecr.Infrastructure.Security.PasswordHasher().Hash(LoginPassword));
        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var role = new Ecr.Domain.Entities.Security.Role(
            Ecr.Domain.ValueObjects.EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
            new Ecr.Domain.ValueObjects.LocalizedText(
                new Dictionary<string, string> { ["en"] = "Group rules test" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new Ecr.Domain.Entities.Security.RolePermission(role.Id, "Template.View"));
        db.RoleAssignments.Add(new Ecr.Domain.Entities.Security.RoleAssignment(role.Id, user.Id, principalSid: null));

        var template = new Ecr.Domain.Entities.Configuration.Template(
            Ecr.Domain.ValueObjects.EcrCode.Create($"T{Guid.NewGuid():N}"[..12]),
            new Ecr.Domain.ValueObjects.LocalizedText(
                new Dictionary<string, string> { ["en"] = "Group rules template" }),
            user.Id, DateTime.UtcNow);
        db.Templates.Add(template);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new Ecr.Domain.Entities.Configuration.TemplateVersion(
            template.Id, "1.0.0.0", user.Id, DateTime.UtcNow);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.SheetGroupRules.Add(new Ecr.Domain.Entities.Configuration.SheetGroupRule(
            version.Id, "Water", ruleKind: 0, targetGroup: null));
        db.SheetGroupRules.Add(new Ecr.Domain.Entities.Configuration.SheetGroupRule(
            version.Id, "Air", ruleKind: 1, targetGroup: null));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (userName, version.Id);
    }
}
