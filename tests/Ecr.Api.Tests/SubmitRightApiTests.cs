// tests/Ecr.Api.Tests/SubmitRightApiTests.cs
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
/// D-285 (варіант B′), наскрізно через HTTP: подання аркуша потребує рівня
/// <c>Submit</c> АБО рівня <c>Write</c> разом із проєктним правом
/// <c>Document.Submit</c>. «До/після» — у назві кожного тесту.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні докази: прибрати гілку права в <c>EditRules.CanSubmit</c> —
/// червоніє «Write + право → 204»; зробити <c>Write</c> достатнім без права —
/// червоніє «Write без права → 403»; прибрати право з
/// <c>RecallSheetHandler.HasSubmitGrant</c> — червоніє «відкликання Write + право».
/// </remarks>
[Collection("SqlServer")]
public sealed class SubmitRightApiTests(SqlServerFixture sql)
{
    private const string Password = "Submit-Right-D285!";
    private const string Right = "Document.Submit";

    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-285")]
    public async Task Write_без_права_Submit_подання_403_InsufficientGrantLevel_без_змін()
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, b, GrantLevel.Write, permissions: []).ConfigureAwait(true);

        var body = await ExpectAsync(app, HttpStatusCode.Forbidden, SubmitAsync(client, b)).ConfigureAwait(true);

        Assert.Equal("InsufficientGrantLevel", ReasonOf(body));
        Assert.Contains("deny.InsufficientGrantLevel.Submit", body, StringComparison.Ordinal);
        Assert.Null(await StatusAsync(b).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-285")]
    public async Task Write_з_правом_Submit_подає_204_ДО_було_403()
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, b, GrantLevel.Write, permissions: [Right]).ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.NoContent, SubmitAsync(client, b)).ConfigureAwait(true);

        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(b).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-285")]
    public async Task Submit_без_права_подає_204_без_змін()
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, b, GrantLevel.Submit, permissions: []).ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.NoContent, SubmitAsync(client, b)).ConfigureAwait(true);

        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(b).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-285")]
    public async Task Read_з_правом_Submit_подання_403()
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, b, GrantLevel.Read, permissions: [Right]).ConfigureAwait(true);

        var body = await ExpectAsync(app, HttpStatusCode.Forbidden, SubmitAsync(client, b)).ConfigureAwait(true);

        Assert.Equal("InsufficientGrantLevel", ReasonOf(body));
        Assert.Null(await StatusAsync(b).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-285")]
    public async Task Заборона_на_аркуш_перемагає_Write_з_правом_Submit_404_як_неіснуючий()
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(
                app, b, GrantLevel.Write, permissions: [Right], denySheet: true)
            .ConfigureAwait(true);

        // R-2: схований аркуш не розкриває свого існування — 404, як для неіснуючого.
        var body = await ExpectAsync(app, HttpStatusCode.NotFound, SubmitAsync(client, b)).ConfigureAwait(true);

        using var json = JsonDocument.Parse(body);
        Assert.Equal("ECR-DOC-0404", json.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-DOC-0404.sheetNotInDocument", json.RootElement.GetProperty("messageKey").GetString());
        Assert.Null(await StatusAsync(b).ConfigureAwait(true));
    }

    /// <summary>
    /// Write з правом подав; Write не має рівня Approve, тож погодити не може
    /// ніхто з цією парою — відмова за рівнем, а не за «власне подання».
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-285")]
    public async Task Write_з_правом_подав_і_не_погоджує_бо_рівень_нижче_Approve()
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, b, GrantLevel.Write, permissions: [Right]).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, SubmitAsync(client, b)).ConfigureAwait(true);

        var body = await ExpectAsync(app, HttpStatusCode.Forbidden, ApproveAsync(client, b)).ConfigureAwait(true);

        Assert.Equal("InsufficientGrantLevel", ReasonOf(body));
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(b).ConfigureAwait(true));
    }

    /// <summary>Той, хто подав (рівень Approve + право), власне подання не затверджує (D-278).</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-285")]
    [Trait("Decision", "D-278")]
    public async Task Подавець_з_Approve_і_правом_власне_подання_не_затверджує()
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, b, GrantLevel.Approve, permissions: [Right]).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, SubmitAsync(client, b)).ConfigureAwait(true);

        var body = await ExpectAsync(app, HttpStatusCode.Forbidden, ApproveAsync(client, b)).ConfigureAwait(true);

        Assert.Contains("approveOwnSubmission", body, StringComparison.Ordinal);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(b).ConfigureAwait(true));
    }

    /// <summary>Автор-Write з правом відкликає своє подання до першого погодження.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-285")]
    public async Task Автор_Write_з_правом_відкликає_подання_ДО_було_403()
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, b, GrantLevel.Write, permissions: [Right]).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, SubmitAsync(client, b)).ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.NoContent, RecallAsync(client, b)).ConfigureAwait(true);

        Assert.Equal(DocumentStatus.Draft, await StatusAsync(b).ConfigureAwait(true));
    }

    /// <summary>Закритий період блокує Write з правом так само, як і Submit.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-285")]
    public async Task Закритий_період_блокує_Write_з_правом_без_змін()
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        await ClosePeriodAsync(b).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, b, GrantLevel.Write, permissions: [Right]).ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.Forbidden, SubmitAsync(client, b)).ConfigureAwait(true);

        Assert.Null(await StatusAsync(b).ConfigureAwait(true));
    }

    /// <summary>
    /// Право Document.Submit, видане роллю з областю «проєкт документа», подає в цьому
    /// проєкті. ⛔ Мутація: у <c>EditRules.MeetsSubmit</c> перевіряти право глобально
    /// (<c>profile.Has(code)</c>) — роль з областю його не дає, тест червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-285")]
    public async Task Право_Submit_ролі_з_областю_проєкту_подає_в_цьому_проєкті()
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(
                app, b, GrantLevel.Write, permissions: [Right],
                scopeJson: RoleAssignmentScope.Create([b.ProjectId], null, null, null).ToJson())
            .ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.NoContent, SubmitAsync(client, b)).ConfigureAwait(true);

        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(b).ConfigureAwait(true));
    }

    /// <summary>
    /// Роль, ЗВУЖЕНА аркушами (D-214), права Document.Submit не дає: воно не Narrowable —
    /// відомий наслідок, Narrowable не розширюємо. Рівень Write на аркуші є, права немає → 403.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-285")]
    public async Task Право_Submit_ролі_звуженої_аркушами_не_діє_403()
    {
        var b = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(
                app, b, GrantLevel.Write, permissions: [Right],
                scopeJson: RoleAssignmentScope.Create([b.ProjectId], [b.SheetCode], null, null).ToJson())
            .ConfigureAwait(true);

        var body = await ExpectAsync(app, HttpStatusCode.Forbidden, SubmitAsync(client, b)).ConfigureAwait(true);

        Assert.Equal("InsufficientGrantLevel", ReasonOf(body));
        Assert.Null(await StatusAsync(b).ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private static string ReasonOf(string body)
    {
        var root = JsonDocument.Parse(body).RootElement;
        if (root.TryGetProperty("reason", out var reason))
        {
            return reason.GetString() ?? string.Empty;
        }

        return root.TryGetProperty("details", out var details) && details.TryGetProperty("reason", out var inner)
            ? inner.GetString() ?? string.Empty
            : $"немає reason у відповіді: {body}";
    }

    private static async Task<string> ExpectAsync(EcrApiFactory app, HttpStatusCode expected, Task<HttpResponseMessage> call)
    {
        using var response = await call.ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} замість {(int)expected}\n{body}\n{app.ErrorsText}");

        return body;
    }

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, TestDocument b)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{b.DocumentId}/submit", UriKind.Relative),
            new { sheetDefId = b.SheetDefId, periodKey = b.PeriodKey.Value });

    private static Task<HttpResponseMessage> ApproveAsync(HttpClient client, TestDocument b)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{b.DocumentId}/approve", UriKind.Relative),
            new { sheetDefId = b.SheetDefId, periodKey = b.PeriodKey.Value, approved = true, reason = (string?)null });

    private static Task<HttpResponseMessage> RecallAsync(HttpClient client, TestDocument b)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{b.DocumentId}/recall", UriKind.Relative),
            new { sheetDefId = b.SheetDefId, periodKey = b.PeriodKey.Value, reason = "D-285 recall" });

    private async Task<TestDocument> ArrangeAsync()
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(false);

        await using var db = Context();
        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return b;
    }

    private async Task ClosePeriodAsync(TestDocument b)
    {
        await using var db = Context();
        var period = await db.Periods
            .SingleAsync(p => p.ProjectId == b.ProjectId && p.PeriodKeyValue == b.PeriodKey.Value)
            .ConfigureAwait(false);
        period.TransitionTo(PeriodState.Open, Now);
        period.TransitionTo(PeriodState.Grace, Now);
        period.TransitionTo(PeriodState.Closed, Now);
        await db.SaveChangesAsync().ConfigureAwait(false);
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

    /// <summary>Користувач з роллю: грант на проєкт документа (+ заборона на аркуш) і названі права.</summary>
    private async Task<HttpClient> SignedInAsync(
        EcrApiFactory app, TestDocument b, GrantLevel level, string[] permissions, bool denySheet = false,
        string? scopeJson = null)
    {
        var name = $"d285_{Guid.NewGuid():N}"[..20];

        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(
                Ecr.Domain.ValueObjects.EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "D-285 submit right" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            var assignment = new RoleAssignment(role.Id, user.Id, principalSid: null);
            db.RoleAssignments.Add(assignment);
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, b.ProjectId, level));
            if (denySheet)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Sheet, b.SheetDefId, level, isDeny: true));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);

            if (scopeJson is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE sec.RoleAssignment SET ScopeJson = {scopeJson} WHERE Id = {assignment.Id}")
                    .ConfigureAwait(false);
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
}
