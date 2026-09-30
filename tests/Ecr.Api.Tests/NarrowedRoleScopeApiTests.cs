// tests/Ecr.Api.Tests/NarrowedRoleScopeApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Documents;
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
/// D-214 (ФВ-6.14): область призначення ролі, звужена аркушами й періодами,
/// наскрізно — від збереженого <c>ScopeJson</c> до відповіді API.
/// </summary>
/// <remarks>
/// ⛔ Документ має один аркуш і дані за 202601; у проєкті два періоди —
/// 202601 і 202602. «Чужий» аркуш — роль звужена кодом, якого в документі
/// немає; «чужий» період — роль звужена 202602.
///
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: <c>NarrowedAccess.AppliesTo</c> завжди <c>true</c>
/// (звуження ігнорується) — червоніють усі рядки «чужий»; зберегти в профілі
/// роль зі звуженням як звичайну роль з областю — так само.
/// </remarks>
[Collection("SqlServer")]
public sealed class NarrowedRoleScopeApiTests(SqlServerFixture sql)
{
    private const string Password = "Narrowed-Scope-D210!";
    private const string OtherSheet = "NOSUCHSHEET";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Свій_аркуш_видно_і_подається()
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, b, Scope(b, sheets: [b.SheetCode])).ConfigureAwait(true);

        Assert.Contains(b.TableInstanceId, await TableIdsAsync(client, b).ConfigureAwait(true));
        await ExpectAsync(app, HttpStatusCode.OK, SliceAsync(client, b)).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, SubmitAsync(client, b)).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Чужий_аркуш_прихований_і_не_подається_а_документ_видно()
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, b, Scope(b, sheets: [OtherSheet])).ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.OK, DocumentAsync(client, b)).ConfigureAwait(true);
        Assert.DoesNotContain(b.TableInstanceId, await TableIdsAsync(client, b).ConfigureAwait(true));
        await ExpectAsync(app, HttpStatusCode.NotFound, SliceAsync(client, b)).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.Forbidden, SubmitAsync(client, b)).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Період_у_проміжку_видно()
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, b, Scope(b, from: 202601, to: 202601)).ConfigureAwait(true);

        Assert.Contains(b.TableInstanceId, await TableIdsAsync(client, b).ConfigureAwait(true));
        await ExpectAsync(app, HttpStatusCode.OK, SliceAsync(client, b)).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Період_поза_проміжком_прихований_хоч_документ_видно()
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, b, Scope(b, from: 202602)).ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.OK, DocumentAsync(client, b)).ConfigureAwait(true);
        Assert.DoesNotContain(b.TableInstanceId, await TableIdsAsync(client, b).ConfigureAwait(true));
        await ExpectAsync(app, HttpStatusCode.NotFound, SliceAsync(client, b)).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.Forbidden, SubmitAsync(client, b)).ConfigureAwait(true);
    }

    /// <summary>
    /// Жоден період проєкту (202601, 202602) не в проміжку — документа для ролі
    /// немає: 404 і немає в переліку. Роль-свідок без області з правом перегляду
    /// в ІНШОМУ проєкті — щоб відмова була саме «документа немає», а не «прав
    /// немає ніде».
    /// </summary>
    [Theory]
    [InlineData(202701, null)]
    [InlineData(null, 202512)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Документ_проєкту_поза_проміжком_не_існує(int? from, int? to)
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        var witness = await OtherProjectAsync(b).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, b, Scope(b, from: from, to: to), witnessProject: witness)
            .ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.NotFound, DocumentAsync(client, b)).ConfigureAwait(true);
        Assert.DoesNotContain(b.DocumentId, await ListedAsync(client).ConfigureAwait(true));
    }

    /// <summary>
    /// Межі включні: дані за 202601 видно рівно на «з» і рівно на «по»; на крок
    /// за межею з будь-якого боку — ні; відкрита межа не обмежує свій бік.
    /// </summary>
    [Theory]
    [InlineData(202601, 202602, HttpStatusCode.OK)]
    [InlineData(202512, 202601, HttpStatusCode.OK)]
    [InlineData(202601, 202601, HttpStatusCode.OK)]
    [InlineData(202602, 202602, HttpStatusCode.NotFound)]
    [InlineData(202601, null, HttpStatusCode.OK)]
    [InlineData(null, 202601, HttpStatusCode.OK)]
    [InlineData(202602, null, HttpStatusCode.NotFound)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Межі_періоду_включні_а_відкрита_межа_не_обмежує(int? from, int? to, HttpStatusCode expected)
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, b, Scope(b, from: from, to: to)).ConfigureAwait(true);

        await ExpectAsync(app, expected, SliceAsync(client, b)).ConfigureAwait(true);
    }

    [Theory]
    [InlineData("own", HttpStatusCode.OK)]
    [InlineData("otherProject", HttpStatusCode.NotFound)]
    [InlineData("otherSheet", HttpStatusCode.NotFound)]
    [InlineData("otherPeriod", HttpStatusCode.NotFound)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Проєкт_аркуш_і_період_перетинаються(string variant, HttpStatusCode expected)
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        var otherProject = await OtherProjectAsync(b).ConfigureAwait(true);

        var scope = variant switch
        {
            "own" => RoleAssignmentScope.Create([b.ProjectId], [b.SheetCode], new PeriodKey(202601), new PeriodKey(202602)),
            "otherProject" => RoleAssignmentScope.Create([otherProject], [b.SheetCode], new PeriodKey(202601)),
            "otherSheet" => RoleAssignmentScope.Create([b.ProjectId], [OtherSheet], new PeriodKey(202601)),
            _ => RoleAssignmentScope.Create([b.ProjectId], [b.SheetCode], new PeriodKey(202602)),
        };

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, b, scope.ToJson(), otherProject).ConfigureAwait(true);

        await ExpectAsync(app, expected, SliceAsync(client, b)).ConfigureAwait(true);
    }

    /// <summary>⛔ Зіпсоване звуження в базі — роль не діє ніде, а не «без звуження».</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Зіпсована_область_не_діє_ніде()
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        var witness = await OtherProjectAsync(b).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var broken = $"{{\"projects\":[{b.ProjectId}],\"sheets\":\"{b.SheetCode}\"}}";
        using var client = await SignedInAsync(app, b, broken, witnessProject: witness).ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.NotFound, DocumentAsync(client, b)).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NotFound, SliceAsync(client, b)).ConfigureAwait(true);
    }

    /// <summary>Регресія: роль без області — як і до D-214.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Роль_без_області_як_раніше()
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, b, scopeJson: null).ConfigureAwait(true);

        Assert.Contains(b.TableInstanceId, await TableIdsAsync(client, b).ConfigureAwait(true));
        await ExpectAsync(app, HttpStatusCode.OK, SliceAsync(client, b)).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, SubmitAsync(client, b)).ConfigureAwait(true);
    }

    /// <summary>
    /// Права на весь проєкт (перерахунок) роль зі звуженням не дає навіть на
    /// своєму аркуші: вони обійшли б звуження.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Проєктне_право_на_весь_проєкт_звужена_роль_не_дає()
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, b, Scope(b, sheets: [b.SheetCode])).ConfigureAwait(true);

        await ExpectAsync(
                app,
                HttpStatusCode.Forbidden,
                client.PostAsJsonAsync(
                    new Uri($"/api/v1/documents/{b.DocumentId}/recalculate", UriKind.Relative),
                    new { periodKey = b.PeriodKey.Value }))
            .ConfigureAwait(true);
    }

    private static string Scope(TestDocument b, string[]? sheets = null, int? from = null, int? to = null)
        => RoleAssignmentScope.Create(
                [b.ProjectId],
                sheets,
                from is { } f ? new PeriodKey(f) : null,
                to is { } t ? new PeriodKey(t) : null)
            .ToJson();

    private static async Task ExpectAsync(EcrApiFactory app, HttpStatusCode expected, Task<HttpResponseMessage> call)
    {
        using var response = await call.ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(response.StatusCode == expected, $"{response.StatusCode} замість {expected}\n{body}\n{app.ErrorsText}");
    }

    private static Task<HttpResponseMessage> DocumentAsync(HttpClient client, TestDocument b)
        => client.GetAsync(new Uri($"/api/v1/documents/{b.DocumentId}", UriKind.Relative));

    private static Task<HttpResponseMessage> SliceAsync(HttpClient client, TestDocument b)
        => client.GetAsync(new Uri($"/api/v1/documents/{b.DocumentId}/tables/{b.TableInstanceId}", UriKind.Relative));

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, TestDocument b)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{b.DocumentId}/submit", UriKind.Relative),
            new { sheetDefId = b.SheetDefId, periodKey = b.PeriodKey.Value });

    private static async Task<List<long>> TableIdsAsync(HttpClient client, TestDocument b)
    {
        using var response = await client.GetAsync(
            new Uri($"/api/v1/documents/{b.DocumentId}/tables?periodKey={b.PeriodKey.Value}", UriKind.Relative))
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");

        return [.. JsonDocument.Parse(body).RootElement.EnumerateArray()
            .Select(t => t.GetProperty("tableInstanceId").GetInt64())];
    }

    private static async Task<List<long>> ListedAsync(HttpClient client)
    {
        var body = await client.GetStringAsync(new Uri("/api/v1/documents?limit=200", UriKind.Relative))
            .ConfigureAwait(false);

        return [.. JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()
            .Select(d => d.GetProperty("id").GetInt64())];
    }

    /// <summary>Документ за 202601 зі своїм аркушем у складі; у проєкті ще й період 202602.</summary>
    private async Task<TestDocument> ArrangeAsync()
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(false);

        await using var db = Context();
        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));
        db.Periods.Add(new Period(
            b.ProjectId, new PeriodKey(202602), 2, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return b;
    }

    private async Task<int> OtherProjectAsync(TestDocument b)
    {
        await using var db = Context();
        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync().ConfigureAwait(false);
        var project = new Project(
            EcrCode.Create($"PN{Guid.NewGuid():N}"[..12]), Name("D-214 other"),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            b.TemplateVersionId, PeriodKind.Monthly, policyId, "Asia/Almaty");
        db.Projects.Add(project);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.Periods.Add(new Period(project.Id, new PeriodKey(202601), 1, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return project.Id;
    }

    /// <summary>
    /// Користувач з роллю: грант <c>Submit</c> на проєкт документа (і на
    /// <paramref name="alsoProject"/>), право <c>Document.View</c> і
    /// <c>Calculation.Recalculate</c>, призначення з <paramref name="scopeJson"/>
    /// дослівно (<c>null</c> — без області). З <paramref name="witnessProject"/> —
    /// ще роль без області: <c>Document.View</c> і <c>Read</c> лише на той проєкт.
    /// </summary>
    private async Task<HttpClient> SignedInAsync(
        EcrApiFactory app, TestDocument b, string? scopeJson, int? alsoProject = null, int? witnessProject = null)
    {
        var name = $"nar210_{Guid.NewGuid():N}"[..20];

        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("D-214 narrowed"));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
            db.RolePermissions.Add(new RolePermission(role.Id, "Calculation.Recalculate"));
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, b.ProjectId, GrantLevel.Submit));
            if (alsoProject is { } other)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, other, GrantLevel.Submit));
            }

            var assignment = new RoleAssignment(role.Id, user.Id, principalSid: null);
            db.RoleAssignments.Add(assignment);
            await db.SaveChangesAsync().ConfigureAwait(false);

            if (scopeJson is not null)
            {
                // Дослівно, повз `SetScope`: так само лежить і зіпсований запис.
                await db.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE sec.RoleAssignment SET ScopeJson = {scopeJson} WHERE Id = {assignment.Id}")
                    .ConfigureAwait(false);
            }

            if (witnessProject is { } witness)
            {
                var viewer = new Role(EcrCode.Create($"W{Guid.NewGuid():N}"[..12]), Name("D-214 witness"));
                db.Roles.Add(viewer);
                await db.SaveChangesAsync().ConfigureAwait(false);

                db.RolePermissions.Add(new RolePermission(viewer.Id, "Document.View"));
                db.ResourceGrants.Add(new ResourceGrant(viewer.Id, ResourceKind.Project, witness, GrantLevel.Read));
                db.RoleAssignments.Add(new RoleAssignment(viewer.Id, user.Id, principalSid: null));
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
