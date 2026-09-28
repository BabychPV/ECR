// tests/Ecr.Api.Tests/DocumentHeaderPatchTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
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
/// <c>PATCH /api/v1/documents/{id}/header</c> справжнім HTTP: стан документа,
/// аудит і конкурентність (enterprise-аудит коректності, <c>C2</c>).
/// </summary>
/// <remarks>
/// ⛔ Доти обробник перевіряв лише грант <c>Write</c>: шапку поданого чи
/// затвердженого документа правили з <c>200</c>, у журналі не лишалося
/// нічого, а дві правки з однієї версії мовчки затирали одна одну.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentHeaderPatchTests(SqlServerFixture sql)
{
    private const string Password = "Api-Doc-Header-2026!";
    private const string AreaCode = "AREA";

    [Theory]
    [InlineData(DocumentStatus.Submitted)]
    [InlineData(DocumentStatus.Approved)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Шапка_поданого_чи_затвердженого_документа_не_змінюється(DocumentStatus status)
    {
        var s = await ArrangeAsync(sheetStatus: status).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var version = await VersionAsync(client, app, s.DocumentId).ConfigureAwait(true);
        var response = await PatchAsync(client, s.DocumentId, "Tengiz", version).ConfigureAwait(true);

        // ⚠ Той самий код, що й для комірок поданого аркуша (`ECR-ACCS-0403`,
        // `reason` = причина з `EditRules`), — відрізняє лише ключ тексту.
        var problem = await ProblemAsync(response, HttpStatusCode.Forbidden, app).ConfigureAwait(true);
        Assert.Equal("ECR-ACCS-0403", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-ACCS-0403.headerLocked", problem.GetProperty("messageKey").GetString());
        Assert.Equal($"Document{status}", problem.GetProperty("reason").GetString());

        Assert.Null(await StoredAreaAsync(s).ConfigureAwait(true));
        Assert.Equal(0, await HeaderEventsAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Шапка_документа_з_усіма_закритими_періодами_не_змінюється()
    {
        var s = await ArrangeAsync(closeAllPeriods: true).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var version = await VersionAsync(client, app, s.DocumentId).ConfigureAwait(true);
        var response = await PatchAsync(client, s.DocumentId, "Tengiz", version).ConfigureAwait(true);

        var problem = await ProblemAsync(response, HttpStatusCode.Forbidden, app).ConfigureAwait(true);
        Assert.Equal("ECR-ACCS-0403", problem.GetProperty("errorCode").GetString());
        Assert.Equal(nameof(EditDenyReason.PeriodClosed), problem.GetProperty("reason").GetString());
        Assert.Null(await StoredAreaAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_шапки_лягає_в_журнал_зі_старим_і_новим_значенням()
    {
        var s = await ArrangeAsync(initialArea: "Kashagan").ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var version = await VersionAsync(client, app, s.DocumentId).ConfigureAwait(true);
        var response = await PatchAsync(client, s.DocumentId, "Tengiz", version).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        // ⚠ Відповідь несе НОВУ версію: наступна правка з неї проходить, зі
        // старої — ні (див. тести конкурентності нижче).
        var newVersion = JsonDocument.Parse(body).RootElement.GetProperty("version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(newVersion));
        Assert.NotEqual(version, newVersion);

        Assert.Equal("Tengiz", await StoredAreaAsync(s).ConfigureAwait(true));

        var details = JsonDocument.Parse(Assert.Single(await HeaderEventDetailsAsync(s).ConfigureAwait(true))).RootElement;
        Assert.Equal(s.DocumentId, details.GetProperty("documentId").GetInt64());
        var field = Assert.Single(details.GetProperty("fields").EnumerateArray());
        Assert.Equal(AreaCode, field.GetProperty("code").GetString());
        Assert.Equal("Kashagan", field.GetProperty("oldValue").GetString());
        Assert.Equal("Tengiz", field.GetProperty("newValue").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Друга_правка_з_тієї_самої_версії_дає_409_і_не_затирає_першу()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var version = await VersionAsync(client, app, s.DocumentId).ConfigureAwait(true);

        var first = await PatchAsync(client, s.DocumentId, "Kashagan", version).ConfigureAwait(true);
        Assert.True(first.StatusCode == HttpStatusCode.OK, $"{first.StatusCode}: {app.ErrorsText}");

        var second = await PatchAsync(client, s.DocumentId, "Tengiz", version).ConfigureAwait(true);

        var problem = await ProblemAsync(second, HttpStatusCode.Conflict, app).ConfigureAwait(true);
        Assert.Equal("ECR-DOC-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-DOC-0409.headerStale", problem.GetProperty("messageKey").GetString());

        Assert.Equal("Kashagan", await StoredAreaAsync(s).ConfigureAwait(true));
        Assert.Equal(1, await HeaderEventsAsync(s).ConfigureAwait(true));
    }

    /// <summary>
    /// Дві правки з однієї версії ОДНОЧАСНО: рівно одна проходить.
    /// </summary>
    /// <remarks>
    /// ⛔ Послідовний тест вище зелений і для перевірки «прочитав — порівняв —
    /// записав» без блокування: там друга правка стартує вже після коміту
    /// першої. Цей ловить саме вікно між порівнянням і записом.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дві_одночасні_правки_з_однієї_версії_проходить_рівно_одна()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var version = await VersionAsync(client, app, s.DocumentId).ConfigureAwait(true);

        var responses = await Task.WhenAll(
            PatchAsync(client, s.DocumentId, "Kashagan", version),
            PatchAsync(client, s.DocumentId, "Tengiz", version)).ConfigureAwait(true);

        var statuses = responses.Select(r => r.StatusCode).OrderBy(c => (int)c).ToArray();
        Assert.True(
            statuses.SequenceEqual([HttpStatusCode.OK, HttpStatusCode.Conflict]),
            $"{string.Join(", ", statuses)}\n{app.ErrorsText}");

        Assert.Equal(1, await HeaderEventsAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правка_без_версії_відхиляється_422()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await client.PatchAsJsonAsync(
            HeaderUri(s.DocumentId),
            new { fields = new[] { new { code = AreaCode, value = (object?)"Tengiz", isEmpty = false } } })
            .ConfigureAwait(true);

        await ProblemAsync(response, HttpStatusCode.UnprocessableEntity, app).ConfigureAwait(true);
        Assert.Null(await StoredAreaAsync(s).ConfigureAwait(true));
    }

    private static Uri HeaderUri(long documentId)
        => new($"/api/v1/documents/{documentId.ToString(CultureInfo.InvariantCulture)}/header", UriKind.Relative);

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, long documentId, string area, string? baseVersion)
        => client.PatchAsJsonAsync(
            HeaderUri(documentId),
            new
            {
                fields = new[] { new { code = AreaCode, value = (object?)area, isEmpty = false } },
                baseVersion,
            });

    /// <summary>Версія шапки з <c>GET</c> — те, з чого клієнт починає правку.</summary>
    private static async Task<string?> VersionAsync(HttpClient client, EcrApiFactory app, long documentId)
    {
        var response = await client.GetAsync(HeaderUri(documentId)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET header: {response.StatusCode}: {body}\n{app.ErrorsText}");

        return JsonDocument.Parse(body).RootElement.TryGetProperty("version", out var version)
            ? version.GetString()
            : null;
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, EcrApiFactory app)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<string?> StoredAreaAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.DocumentHeaderValues.AsNoTracking()
            .Where(v => v.DocumentId == s.DocumentId && v.HeaderFieldDefId == s.AreaFieldId)
            .Select(v => v.ValueString)
            .SingleOrDefaultAsync().ConfigureAwait(false);
    }

    private async Task<List<string>> HeaderEventDetailsAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.Database
            .SqlQuery<string>($"SELECT ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType = N'DocumentHeaderChanged' AND ChangedByUserId = {s.UserId}")
            .ToListAsync().ConfigureAwait(false);
    }

    private async Task<int> HeaderEventsAsync(Scenario s)
        => (await HeaderEventDetailsAsync(s).ConfigureAwait(false)).Count;

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    /// <summary>Документ із полем шапки <c>AREA</c>, користувач із <c>Document.View</c> і грантом Write.</summary>
    private async Task<Scenario> ArrangeAsync(
        DocumentStatus? sheetStatus = null, bool closeAllPeriods = false, string? initialArea = null)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(false);
        var at = new DateTime(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

        await using var db = builder.CreateContext();

        var area = new HeaderFieldDef(
            document.TemplateVersionId, EcrCode.Create(AreaCode),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Area" }), 0, CellDataType.String);
        db.HeaderFieldDefs.Add(area);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (initialArea is not null)
        {
            db.DocumentHeaderValues.Add(new DocumentHeaderValue(
                document.DocumentId, area.Id, new DocumentHeaderValueData { ValueString = initialArea }));
        }

        if (sheetStatus is { } status)
        {
            var state = new ApprovalState(document.DocumentId, document.SheetDefId, document.PeriodKey.Value);
            state.Submit(1, at);
            if (status == DocumentStatus.Approved)
            {
                state.Approve(1, at.AddMinutes(1));
            }

            db.ApprovalStates.Add(state);
        }

        if (closeAllPeriods)
        {
            foreach (var period in await db.Periods.Where(p => p.ProjectId == document.ProjectId).ToListAsync().ConfigureAwait(false))
            {
                period.AdvanceTo(PeriodState.Closed, at);
            }
        }

        var userName = $"hdr_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"HDR_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Doc header" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Write));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(document.DocumentId, area.Id, userName, user.Id);
    }

    private sealed record Scenario(long DocumentId, int AreaFieldId, string UserName, int UserId);
}
