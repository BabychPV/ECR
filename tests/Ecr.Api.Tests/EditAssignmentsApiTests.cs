// tests/Ecr.Api.Tests/EditAssignmentsApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Domain.Entities.Configuration;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Передача місяця виконавцям (EA-C) справжнім HTTP: потік «передати → заповнити → повернути →
/// валідувати», права, оракули, ідемпотентність, аудит.
/// </summary>
/// <remarks>
/// Автор документа в фікстурі — користувач <c>1</c>; керує менеджер проєкту (грант <c>Manage</c>), решта
/// ролей — або лише <c>Write</c> (не керує), або лише <c>Read</c> (виконавець).
/// </remarks>
[Collection("SqlServer")]
public sealed class EditAssignmentsApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Edit-Assign-2026!";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Потік_передати_заповнити_повернути_заповнити_валідувати()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
        using var exec = await SignedInAsync(app, s.Executor.UserName).ConfigureAwait(true);

        var created = await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true);
        var dto = await JsonAsync(created, HttpStatusCode.OK, app).ConfigureAwait(true);
        var id = dto.GetProperty("id").GetInt64();
        Assert.Equal("Assigned", dto.GetProperty("status").GetString());

        // «Мої задачі» виконавця.
        var mine = await JsonAsync(await exec.GetAsync(MeUri()).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        var task = Assert.Single(mine.GetProperty("assignedToMe").EnumerateArray());
        Assert.Equal(id, task.GetProperty("assignmentId").GetInt64());

        // Заповнено.
        var filled = await JsonAsync(await exec.PostAsync(ActionUri(s, id, "fill"), null).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal("Filled", filled.GetProperty("status").GetString());
        var version = filled.GetProperty("version").GetString();

        // Власник бачить «чекає валідації».
        var awaiting = await JsonAsync(await owner.GetAsync(MeUri()).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Single(awaiting.GetProperty("awaitingMyValidation").EnumerateArray());

        // Повернути з причиною.
        var returned = await JsonAsync(
            await owner.PostAsJsonAsync(ActionUri(s, id, "return"), new { reason = "Fix totals", baseVersion = version }).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal("Returned", returned.GetProperty("status").GetString());
        var recipient = Assert.Single(returned.GetProperty("recipients").EnumerateArray());
        Assert.Equal("Fix totals", recipient.GetProperty("returnReason").GetString());

        // Виконавець бачить причину повернення і заповнює знову.
        mine = await JsonAsync(await exec.GetAsync(MeUri()).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal("Fix totals", Assert.Single(mine.GetProperty("assignedToMe").EnumerateArray()).GetProperty("returnReason").GetString());

        filled = await JsonAsync(await exec.PostAsync(ActionUri(s, id, "fill"), null).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        version = filled.GetProperty("version").GetString();

        // Валідація.
        var validated = await JsonAsync(
            await owner.PostAsJsonAsync(ActionUri(s, id, "validate"), new { baseVersion = version }).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal("Validated", validated.GetProperty("status").GetString());

        // Повтор валідації — 200 без змін.
        var again = await owner.PostAsJsonAsync(ActionUri(s, id, "validate"), new { baseVersion = "stale" }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        // Аудит: передано, заповнено, повернуто, валідовано.
        var events = await EventTypesAsync(s).ConfigureAwait(true);
        Assert.Contains("EditAssignmentGranted", events);
        Assert.Contains("EditAssignmentFilled", events);
        Assert.Contains("EditAssignmentReturned", events);
        Assert.Contains("EditAssignmentValidated", events);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повторне_заповнено_і_повторний_upsert_ідемпотентні()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
        using var exec = await SignedInAsync(app, s.Executor.UserName).ConfigureAwait(true);

        var first = await JsonAsync(await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        var id = first.GetProperty("id").GetInt64();

        // Той самий склад і ознаки без baseVersion — не помилка й не нова передача.
        var second = await JsonAsync(await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal(id, second.GetProperty("id").GetInt64());
        Assert.Equal(first.GetProperty("version").GetString(), second.GetProperty("version").GetString());

        var f1 = await exec.PostAsync(ActionUri(s, id, "fill"), null).ConfigureAwait(true);
        var f2 = await exec.PostAsync(ActionUri(s, id, "fill"), null).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, f1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, f2.StatusCode);

        var filledEvents = (await EventTypesAsync(s).ConfigureAwait(true)).Count(e => e == "EditAssignmentFilled");
        Assert.Equal(1, filledEvents);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_наявної_без_baseVersion_422_а_зі_старою_409()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var first = await JsonAsync(await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);

        var noVersion = await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id, isExclusive: false)).ConfigureAwait(true);
        var p1 = await JsonAsync(noVersion, HttpStatusCode.UnprocessableEntity, app).ConfigureAwait(true);
        Assert.Equal("err.ECR-REQ-0422.editAssignmentBaseVersion", p1.GetProperty("messageKey").GetString());

        var stale = await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id, isExclusive: false, baseVersion: "00")).ConfigureAwait(true);
        var p2 = await JsonAsync(stale, HttpStatusCode.Conflict, app).ConfigureAwait(true);
        Assert.Equal("err.ECR-DOC-0409.editAssignmentStale", p2.GetProperty("messageKey").GetString());

        var ok = await owner.PostAsJsonAsync(
            ListUri(s),
            CreateBody(s, s.Executor.Id, isExclusive: false, baseVersion: first.GetProperty("version").GetString())).ConfigureAwait(true);
        var updated = await JsonAsync(ok, HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.False(updated.GetProperty("isExclusive").GetBoolean());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Керувати_може_лише_автор_чи_менеджер_а_невидимому_404()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var writer = await SignedInAsync(app, s.Writer.UserName).ConfigureAwait(true);
        using var stranger = await SignedInAsync(app, s.Stranger.UserName).ConfigureAwait(true);

        var forbidden = await writer.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true);
        var p = await JsonAsync(forbidden, HttpStatusCode.Forbidden, app).ConfigureAwait(true);
        Assert.Equal("err.ECR-ACCS-0403.notEditAssignmentManager", p.GetProperty("messageKey").GetString());

        var listForbidden = await writer.GetAsync(ListUri(s)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, listForbidden.StatusCode);

        var hidden = await stranger.GetAsync(ListUri(s)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Одержувач_без_Read_чи_неіснуючий_дає_однакове_422()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var noRead = await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Stranger.Id)).ConfigureAwait(true);
        var missing = await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, 987_654_321)).ConfigureAwait(true);

        var a = await noRead.Content.ReadAsStringAsync().ConfigureAwait(true);
        var b = await missing.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noRead.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Equal(
            "err.ECR-REQ-0422.editAssignmentRecipientInvalid",
            JsonDocument.Parse(a).RootElement.GetProperty("messageKey").GetString());
        Assert.Equal(
            JsonDocument.Parse(a).RootElement.GetProperty("messageKey").GetString(),
            JsonDocument.Parse(b).RootElement.GetProperty("messageKey").GetString());

        // Нічого не створено.
        var list = await JsonAsync(await owner.GetAsync(ListUri(s)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Empty(list.GetProperty("assignments").EnumerateArray());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Fill_чужою_людиною_і_неіснуючої_передачі_дає_404()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
        using var other = await SignedInAsync(app, s.OtherExecutor.UserName).ConfigureAwait(true);

        var created = await JsonAsync(await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        var id = created.GetProperty("id").GetInt64();

        var foreign = await other.PostAsync(ActionUri(s, id, "fill"), null).ConfigureAwait(true);
        var missing = await other.PostAsync(ActionUri(s, id + 1000, "fill"), null).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var foreignKey = JsonDocument.Parse(await foreign.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement.GetProperty("messageKey").GetString();
        var missingKey = JsonDocument.Parse(await missing.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement.GetProperty("messageKey").GetString();
        Assert.Equal(foreignKey, missingKey);

        // Чужа людина не бачить передачі і в «своїх» задачах.
        var mine = await JsonAsync(await other.GetAsync(MeUri()).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Empty(mine.GetProperty("assignedToMe").EnumerateArray());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Передача_понад_власні_права_дає_422()
    {
        var s = await ArrangeAsync(denyColumnForManager: true).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var response = await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true);
        var p = await JsonAsync(response, HttpStatusCode.UnprocessableEntity, app).ConfigureAwait(true);
        Assert.Equal(
            "err.ECR-REQ-0422.editAssignmentDelegationExceedsOwnRights",
            p.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Валідація_до_заповнення_422_повернення_без_причини_422()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var created = await JsonAsync(await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        var id = created.GetProperty("id").GetInt64();
        var version = created.GetProperty("version").GetString();

        var early = await owner.PostAsJsonAsync(ActionUri(s, id, "validate"), new { baseVersion = version }).ConfigureAwait(true);
        var p = await JsonAsync(early, HttpStatusCode.UnprocessableEntity, app).ConfigureAwait(true);
        Assert.Equal("err.ECR-REQ-0422.editAssignmentNotAllFilled", p.GetProperty("messageKey").GetString());

        var noReason = await owner.PostAsJsonAsync(ActionUri(s, id, "return"), new { reason = "  ", baseVersion = version }).ConfigureAwait(true);
        var p2 = await JsonAsync(noReason, HttpStatusCode.UnprocessableEntity, app).ConfigureAwait(true);
        Assert.Equal("err.ECR-REQ-0422.editAssignmentReasonRequired", p2.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зняття_передачі_і_пошук_кандидатів()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        var created = await JsonAsync(await owner.PostAsJsonAsync(ListUri(s), CreateBody(s, s.Executor.Id)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        var id = created.GetProperty("id").GetInt64();

        var del = await owner.DeleteAsync(new Uri($"{ListUri(s)}/{id.ToString(CultureInfo.InvariantCulture)}", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        var list = await JsonAsync(await owner.GetAsync(ListUri(s)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Empty(list.GetProperty("assignments").EnumerateArray());
        Assert.Contains("EditAssignmentRevoked", await EventTypesAsync(s).ConfigureAwait(true));

        var tooShort = await owner.GetAsync(new Uri($"{ListUri(s)}/candidates?q=a", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooShort.StatusCode);

        var found = await owner.GetAsync(new Uri($"{ListUri(s)}/candidates?q={Uri.EscapeDataString(s.Executor.UserName[..6])}", UriKind.Relative)).ConfigureAwait(true);
        var body = await found.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.DoesNotContain("email", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(s.Executor.Id.ToString(CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Набір_застосовується_до_зрізів_копіюється_і_зміна_зі_старою_версією_409()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var owner = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);
        using var other = await SignedInAsync(app, s.OtherExecutor.UserName).ConfigureAwait(true);
        var setsUri = new Uri("/api/v1/me/editor-sets", UriKind.Relative);

        var set = await JsonAsync(
            await owner.PostAsJsonAsync(setsUri, new { name = "Dept 5", memberUserIds = new[] { s.Executor.Id } }).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        var setId = set.GetProperty("id").GetInt32();
        var setVersion = set.GetProperty("version").GetString();

        // Чужий користувач не бачить набір і не змінює його.
        var foreignList = await JsonAsync(await other.GetAsync(setsUri).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Empty(foreignList.EnumerateArray());
        var foreignPut = await other.PutAsJsonAsync(
            new Uri($"/api/v1/me/editor-sets/{setId.ToString(CultureInfo.InvariantCulture)}", UriKind.Relative),
            new { name = "X", memberUserIds = Array.Empty<int>(), baseVersion = setVersion }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, foreignPut.StatusCode);

        // Застосування до двох зрізів.
        var applied = await JsonAsync(
            await owner.PostAsJsonAsync(
                new Uri($"{ListUri(s)}/apply-set", UriKind.Relative),
                new
                {
                    setId,
                    slices = new[]
                    {
                        new { sheetCode = s.SheetCode, periodKey = (int?)202601 },
                        new { sheetCode = s.SheetCode, periodKey = (int?)202602 },
                    },
                }).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        var items = applied.GetProperty("assignments").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal("Dept 5", i.GetProperty("appliedSetName").GetString()));

        // Копіювання на третій зріз.
        var copied = await JsonAsync(
            await owner.PostAsJsonAsync(
                new Uri($"{ListUri(s)}/copy", UriKind.Relative),
                new
                {
                    fromSlice = new { sheetCode = s.SheetCode, periodKey = (int?)202601 },
                    toSlices = new[] { new { sheetCode = s.SheetCode, periodKey = (int?)202603 } },
                }).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        var copy = Assert.Single(copied.GetProperty("assignments").EnumerateArray());
        Assert.Equal(s.Executor.Id, Assert.Single(copy.GetProperty("recipients").EnumerateArray()).GetProperty("userId").GetInt32());

        // Зміна набору: зі свіжою версією — 200, зі старою — 409.
        var putUri = new Uri($"/api/v1/me/editor-sets/{setId.ToString(CultureInfo.InvariantCulture)}", UriKind.Relative);
        var updated = await JsonAsync(
            await owner.PutAsJsonAsync(putUri, new { name = "Dept 5b", memberUserIds = new[] { s.Executor.Id }, baseVersion = setVersion }).ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal("Dept 5b", updated.GetProperty("name").GetString());

        var stale = await owner.PutAsJsonAsync(
            putUri, new { name = "Dept 5c", memberUserIds = new[] { s.Executor.Id }, baseVersion = setVersion }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // Видалення набору лишає передачі.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(putUri).ConfigureAwait(true)).StatusCode);
        var list = await JsonAsync(await owner.GetAsync(ListUri(s)).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        Assert.Equal(3, list.GetProperty("assignments").GetArrayLength());
    }
    private static Uri ListUri(Scenario s)
        => new($"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/edit-assignments", UriKind.Relative);

    private static Uri ActionUri(Scenario s, long id, string action)
        => new($"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/edit-assignments/{id.ToString(CultureInfo.InvariantCulture)}/{action}", UriKind.Relative);

    private static Uri MeUri() => new("/api/v1/me/edit-assignments", UriKind.Relative);

    private static object CreateBody(Scenario s, int recipient, bool isExclusive = true, string? baseVersion = null)
        => new
        {
            slice = new { sheetCode = s.SheetCode },
            recipientUserIds = new[] { recipient },
            isExclusive,
            baseVersion,
        };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status, EcrApiFactory app)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<List<string>> EventTypesAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var docMarker = $"\"documentId\":{s.DocumentId.ToString(CultureInfo.InvariantCulture)}";
        return await db.Database
            .SqlQuery<string>($"SELECT EventType AS Value FROM aud.SecurityEvent WHERE EventType LIKE N'EditAssignment%' AND DetailsJson LIKE {"%" + docMarker + "%"}")
            .ToListAsync().ConfigureAwait(false);
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private async Task<Scenario> ArrangeAsync(bool denyColumnForManager = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(false);
        await using var db = builder.CreateContext();

        async Task<Person> NewUserAsync(string prefix, GrantLevel? level, bool withView, bool denyColumn = false)
        {
            var userName = $"{prefix}_{Guid.NewGuid():N}"[..20];
            var user = new User(userName, userName, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var role = new Role(
                EcrCode.Create($"EA_{Guid.NewGuid():N}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Edit assignment test" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            if (withView)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            if (level is { } l)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, l));
            }

            if (denyColumn)
            {
                db.ResourceGrants.Add(new ResourceGrant(
                    role.Id, ResourceKind.Column, document.ColumnDefIds[1], GrantLevel.None, isDeny: true));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
            return new Person(user.Id, userName);
        }

        var manager = await NewUserAsync("eam", GrantLevel.Manage, withView: true, denyColumn: denyColumnForManager).ConfigureAwait(false);
        var writer = await NewUserAsync("eaw", GrantLevel.Write, withView: true).ConfigureAwait(false);
        var executor = await NewUserAsync("eae", GrantLevel.Read, withView: true).ConfigureAwait(false);
        var other = await NewUserAsync("eao", GrantLevel.Read, withView: true).ConfigureAwait(false);
        var stranger = await NewUserAsync("eas", null, withView: false).ConfigureAwait(false);

        return new Scenario(document.DocumentId, document.SheetCode, manager, writer, executor, other, stranger);
    }

    private sealed record Person(int Id, string UserName);

    private sealed record Scenario(
        long DocumentId, string SheetCode, Person Manager, Person Writer, Person Executor, Person OtherExecutor, Person Stranger);
}
