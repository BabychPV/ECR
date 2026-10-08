// tests/Ecr.Api.Tests/Security/RegistryImpactNarrowedReaderTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// <c>GET /registries/{code}/impact</c> для читача, якому частина документа схована (аркуш/період): перелік
/// «які документи зачепила правка довідника і через яку методологію» будується з результатів методологій
/// ВСЬОГО документа, тож розкривав би існування й код методології, чиї виходи прив'язані лише до схованого
/// аркуша (той самий клас дефекту, що й свіжість у <c>calculation-results</c>, Н-2).
/// </summary>
/// <remarks>
/// ⛔ Сценарій: методологія з виходом, прив'язаним ЛИШЕ до колонки схованого аркуша B; поточний прогін дав
/// результат; довідник, який читає методологія, змінено після прогону. Повна роль ("none") бачить документ у
/// переліку (контроль: сценарій справді щось показує); читач, якому B схований ("scope", "deny"), - порожній
/// перелік і так само порожні <c>calculation-results</c> (узгоджено з <c>resultsStale = null</c>).
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryImpactNarrowedReaderTests(SqlServerFixture sql)
{
    private const string Password = "Registry-Impact-Narrowed-R7!";

    [Theory]
    [InlineData("scope")]
    [InlineData("deny")]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Вплив_довідника_для_звуженого_читача_не_називає_документ_і_методологію_схованого_аркуша(string how)
    {
        var s = await ArrangeAsync(how);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName);

        var impact = await GetAsync(client, $"/api/v1/registries/{s.RegistryCode}/impact");

        // Розкриття - це 200 із документом/кодом методології; відмова (403/404) теж не розкриває нічого.
        Assert.DoesNotContain(s.MethodologyCode, impact.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(s.BusinessKey, impact.Body, StringComparison.Ordinal);
        if (impact.Status == HttpStatusCode.OK)
        {
            Assert.Equal(0, JsonNode.Parse(impact.Body)!["total"]!.GetValue<int>());
        }

        // Той самий читач у calculation-results числа методології схованого аркуша не бачить.
        var results = await GetAsync(client, $"/api/v1/documents/{s.DocumentId}/calculation-results?periodKey={s.PeriodKey}");
        Assert.DoesNotContain(s.MethodologyCode, results.Body, StringComparison.Ordinal);
        Assert.True(
            results.Status != HttpStatusCode.OK || results.Body.Replace(" ", string.Empty, StringComparison.Ordinal) == "[]",
            $"{how}: calculation-results\n{results.Body}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Перерахунок_зачеплених_для_читача_з_Deny_на_аркуш_відмовляє_422_як_для_незачепленого()
    {
        var s = await ArrangeAsync("deny");
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName);

        using var reply = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{s.RegistryCode}/recalculate-impacted", UriKind.Relative),
            new { documentIds = new[] { s.DocumentId }, reason = "перевірка межі читання" });
        var body = await reply.Content.ReadAsStringAsync();

        Assert.True(reply.StatusCode == HttpStatusCode.UnprocessableEntity, $"{reply.StatusCode}\n{body}");
        Assert.Contains("ECR-REG-0422", body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Вплив_довідника_для_ролі_без_обмежень_називає_документ_і_методологію_контроль_сценарію()
    {
        var s = await ArrangeAsync("none");
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName);

        var impact = await GetAsync(client, $"/api/v1/registries/{s.RegistryCode}/impact");

        Assert.True(impact.Status == HttpStatusCode.OK, $"none: {impact.Status}\n{impact.Body}");
        Assert.Contains(s.BusinessKey, impact.Body, StringComparison.Ordinal);
        Assert.Contains(s.MethodologyCode, impact.Body, StringComparison.Ordinal);
    }

    private sealed record Scenario(
        long DocumentId, int PeriodKey, string UserName, string RegistryCode, string MethodologyCode, string BusinessKey);

    private async Task<Scenario> ArrangeAsync(string how)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var b = await builder.BuildAsync();
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var now = DateTime.UtcNow;
        var runStartedAt = now.AddHours(-2);
        var userName = $"ri{Guid.NewGuid():N}"[..20];

        await using var db = Context();

        var period = await db.Periods.SingleAsync(p => p.ProjectId == b.ProjectId && p.PeriodKeyValue == b.PeriodKey.Value);
        period.TransitionTo(PeriodState.Open, now.AddDays(-1));

        db.DocumentSheets.Add(new Ecr.Domain.Entities.Documents.DocumentSheet(b.DocumentId, b.SheetDefId));

        // Шаблон: схований аркуш B з таблицею й колонкою.
        var sheetB = new SheetDef(b.TemplateVersionId, EcrCode.Create($"HIDRI{tag}"), Name($"HiddenImpactSheet{tag}"), 2);
        db.SheetDefs.Add(sheetB);
        await db.SaveChangesAsync();
        var tableB = new TableDef(
            sheetB.Id, EcrCode.Create($"RITB{tag}"), Name($"HiddenImpactTable{tag}"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(tableB);
        await db.SaveChangesAsync();
        var columnB = new ColumnDef(tableB.Id, EcrCode.Create($"RICB{tag}"), Name($"HiddenImpactColumn{tag}"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(columnB);

        // Методологія, чий єдиний вихід прив'язаний до колонки B; читає довідник, змінений ПІСЛЯ прогону.
        var methodologyCode = $"RIM{tag}";
        var methodology = new Methodology(EcrCode.Create(methodologyCode), Name("impact methodology"));
        var registryCode = $"RIR{tag}";
        var registry = new RegistryDef(EcrCode.Create(registryCode), Name("Registry"), isTemporal: false);
        registry.MarkDataChanged(now.AddHours(-1));
        db.Methodologies.Add(methodology);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        db.CalculationBindings.Add(new CalculationBinding(tableB.Id, columnB.Id, methodology.Id, "tons", "{}"));
        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, runStartedAt.AddDays(-1));
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();
        db.RegistryUses.Add(RegistryUse.ForMethodologyFormula(version.Id, "F1", registry.Id, "X"));

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Impact role"));
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        foreach (var permission in new[] { "Document.View", "Calculation.View", "Calculation.Recalculate", "Registry.View" })
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, b.ProjectId, GrantLevel.Manage));
        if (how == "deny")
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Sheet, sheetB.Id, GrantLevel.Read, isDeny: true));
        }

        var assignment = new RoleAssignment(role.Id, user.Id, principalSid: null);
        db.RoleAssignments.Add(assignment);
        await db.SaveChangesAsync();

        if (how == "scope")
        {
            var scope = RoleAssignmentScope.Create([b.ProjectId], [b.SheetCode], null, null).ToJson();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE sec.RoleAssignment SET ScopeJson = {scope} WHERE Id = {assignment.Id}");
        }

        var run = new CalculationRun(b.ProjectId, b.PeriodKey.Value, null, runStartedAt, b.DocumentId);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();
        run.Complete("Succeeded", runStartedAt.AddMinutes(1), null, null);
        run.MakeCurrent();

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        await new CalculationResultStore(db, new TestClock(runStartedAt.AddMinutes(1))).WriteResultsAsync(
            run.Id,
            [new CalculationOutput(b.DocumentId, "RIROW", [new CalculationOutputValue(version.Id, null, "tons", 5m, unitId)], [])],
            CancellationToken.None);
        await db.SaveChangesAsync();

        var businessKey = await db.Documents.AsNoTracking()
            .Where(d => d.Id == b.DocumentId).Select(d => d.BusinessKey).SingleAsync();

        return new Scenario(b.DocumentId, b.PeriodKey.Value, userName, registryCode, methodologyCode, businessKey);
    }

    private sealed record Reply(HttpStatusCode Status, string Body);

    private static async Task<Reply> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
