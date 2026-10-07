using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.Application.Validation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// Доказ R-1 / R-2 / R-5 рев'ю безпеки фіксу S0: приховані від читача аркуші не розкриваються
/// ні ФІЛЬТРОМ за станом, ні ВІДМОВАМИ дій (подання/затвердження/повернення/відкликання/перерахунок),
/// ні 409 видалення й зміни ключа; плюс шлях БЕЗ періоду й повні асерти лічильників.
/// </summary>
/// <remarks>
/// Сценарій: документ із видимим аркушем A (чернетка, 1 помилка + 1 попередження) і схованим B
/// (стан задає тест). Приховування двома способами: звуження ролі аркушами (D-214, "scope") і Deny
/// на аркуш ("deny"); "none" — звичайна роль без обмежень (контроль паритету).
/// ⛔ Мутаційний доказ — у листі готовності.
/// </remarks>
[Collection("SqlServer")]
public sealed class HiddenSheetOracleTests(SqlServerFixture sql)
{
    private const string Password = "Hidden-Sheet-Oracle-R!";
    private const long NonexistentSheet = 2_000_000_000L;

    // ── R-1: фільтр за станом ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("scope")]
    [InlineData("deny")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Фільтр_за_станом_схованого_аркуша_не_знаходить_документ_а_за_станом_видимого_знаходить(string how)
    {
        var s = await ArrangeAsync(how, DocumentStatus.Rejected).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        // B відхилений, A — чернетка: для читача документ — чернетка, «відхилених» немає.
        Assert.DoesNotContain(s.DocumentId, await ListedAsync(client, s.PeriodKey, "Rejected").ConfigureAwait(true));
        Assert.Contains(s.DocumentId, await ListedAsync(client, s.PeriodKey, "Draft").ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Звичайна_роль_знаходить_документ_за_станом_будь_якого_аркуша()
    {
        var s = await ArrangeAsync("none", DocumentStatus.Rejected).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        Assert.Contains(s.DocumentId, await ListedAsync(client, s.PeriodKey, "Rejected").ConfigureAwait(true));
        Assert.DoesNotContain(s.DocumentId, await ListedAsync(client, s.PeriodKey, "Draft").ConfigureAwait(true));
    }

    // ── Лічильники: sheetCount, withIssues, шлях без періоду ─────────────────────────────

    [Theory]
    [InlineData("scope")]
    [InlineData("deny")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Перелік_і_картка_звуженого_читача_рахують_лише_видимі_аркуші_з_періодом_і_без(string how)
    {
        var s = await ArrangeAsync(how, DocumentStatus.Rejected).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        foreach (var query in new[] { $"&periodKey={s.PeriodKey}", string.Empty })
        {
            var listBody = await client.GetStringAsync(
                new Uri($"/api/v1/documents?limit=200{query}", UriKind.Relative)).ConfigureAwait(true);
            var item = JsonDocument.Parse(listBody).RootElement.GetProperty("items").EnumerateArray()
                .Single(d => d.GetProperty("id").GetInt64() == s.DocumentId);
            AssertVisibleOnly($"GET /documents{query}", listBody, item, s, withPeriod: query.Length > 0);

            var cardQuery = query.Length > 0 ? "?" + query[1..] : string.Empty;
            var cardBody = await client.GetStringAsync(
                new Uri($"/api/v1/documents/{s.DocumentId}{cardQuery}", UriKind.Relative)).ConfigureAwait(true);
            AssertVisibleOnly($"GET /documents/{{id}}{cardQuery}", cardBody, JsonDocument.Parse(cardBody).RootElement, s, query.Length > 0);
        }
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("deny")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Зведення_звуженого_читача_не_рахує_документ_із_зауваженнями_а_звичайного_рахує(string how)
    {
        var narrow = await ArrangeAsync(how, DocumentStatus.Rejected).ConfigureAwait(true);
        var ordinary = await ArrangeAsync("none", DocumentStatus.Rejected).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var narrowClient = await SignedInAsync(app, narrow.UserName).ConfigureAwait(true);
        using var ordinaryClient = await SignedInAsync(app, ordinary.UserName).ConfigureAwait(true);

        var narrowSummary = await SummaryAsync(narrowClient, narrow).ConfigureAwait(true);
        var ordinarySummary = await SummaryAsync(ordinaryClient, ordinary).ConfigureAwait(true);

        // Звужений: документ — чернетка (лише A), збережений підсумок по ВСЬОМУ документу не береться.
        Assert.Equal((1, 0, 0), (narrowSummary.GetProperty("draft").GetInt32(), narrowSummary.GetProperty("rejected").GetInt32(),
            narrowSummary.GetProperty("withIssues").GetInt32()));

        // Звичайний: відхилений B рахується, зауваження — теж; паритет із переліком (див. тест вище).
        Assert.True(ordinarySummary.GetProperty("rejected").GetInt32() >= 1);
        Assert.True(ordinarySummary.GetProperty("withIssues").GetInt32() >= 1);
    }

    // ── R-2: відмови дій на схований аркуш == відмови на неіснуючий ─────────────────────────

    [Theory]
    [InlineData("scope", "submit")]
    [InlineData("deny", "submit")]
    [InlineData("scope", "approve")]
    [InlineData("deny", "approve")]
    [InlineData("scope", "reopen")]
    [InlineData("deny", "reopen")]
    [InlineData("scope", "recall")]
    [InlineData("deny", "recall")]
    [InlineData("scope", "recalculate")]
    [InlineData("deny", "recalculate")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Дія_над_схованим_аркушем_відмовляє_так_само_як_над_неіснуючим(string how, string action)
    {
        var s = await ArrangeAsync(how, DocumentStatus.Submitted).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var hidden = await ActAsync(client, s, action, s.HiddenSheetId).ConfigureAwait(true);
        var missing = await ActAsync(client, s, action, NonexistentSheet).ConfigureAwait(true);

        Assert.True(hidden.Status == missing.Status, $"{action}: {hidden.Status} (схований) != {missing.Status} (неіснуючий)\n{hidden.Body}\n---\n{missing.Body}");
        var normalizedHidden = Normalize(hidden.Body, s.HiddenSheetId);
        var normalizedMissing = Normalize(missing.Body, NonexistentSheet);
        Assert.True(normalizedHidden == normalizedMissing, $"{action}: тіла різняться\n{hidden.Body}\n---\n{missing.Body}");

        foreach (var word in new[] { "Submitted", "Approved", "Rejected", "reason", "InsufficientGrantLevel", "NoGrant" })
        {
            Assert.DoesNotContain(word, hidden.Body, StringComparison.Ordinal);
        }
    }

    // ── Порівняння версій: версія схованого аркуша == неіснуюча версія ─────────────────────

    /// <remarks>
    /// ⛔ До фіксу `from`/`to` схованого аркуша давали 200 (порожній diff), а неіснуючий id — 404:
    /// оракул існування id версії. Мутаційний доказ: прибрати перевірку CanReadSheet у
    /// <c>CompareDocumentVersionsHandler.LoadAsync</c> — червоніє рядок про <c>from</c>/<c>to</c>.
    /// </remarks>
    [Theory]
    [InlineData("scope")]
    [InlineData("deny")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    [Trait("Requirement", "ФВ-5.22")]
    public async Task Порівняння_з_версією_схованого_аркуша_відмовляє_так_само_як_з_неіснуючою(string how)
    {
        var s = await ArrangeAsync(how, DocumentStatus.Submitted).ConfigureAwait(true);
        var hiddenVersion = await SnapshotAsync(s, (int)s.HiddenSheetId).ConfigureAwait(true);
        var visibleVersion = await SnapshotAsync(s, s.VisibleSheetId).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        const long missingVersion = 2_000_000_001L;

        // from = схований  vs  from = неіснуючий.
        var hiddenFrom = await CompareAsync(client, s, $"from={hiddenVersion}&to=current").ConfigureAwait(true);
        var missingFrom = await CompareAsync(client, s, $"from={missingVersion}&to=current").ConfigureAwait(true);
        AssertSameRefusal("from", hiddenFrom, hiddenVersion, missingFrom, missingVersion);

        // to = схований  vs  to = неіснуючий (from — видима версія).
        var hiddenTo = await CompareAsync(client, s, $"from={visibleVersion}&to={hiddenVersion}").ConfigureAwait(true);
        var missingTo = await CompareAsync(client, s, $"from={visibleVersion}&to={missingVersion}").ConfigureAwait(true);
        AssertSameRefusal("to", hiddenTo, hiddenVersion, missingTo, missingVersion);

        // Контроль: видима версія порівнюється.
        var visible = await CompareAsync(client, s, $"from={visibleVersion}&to=current").ConfigureAwait(true);
        Assert.True(visible.Status == HttpStatusCode.OK, $"видима версія: {visible.Status}\n{visible.Body}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.22")]
    public async Task Звичайна_роль_порівнює_з_версією_будь_якого_аркуша()
    {
        var s = await ArrangeAsync("none", DocumentStatus.Submitted).ConfigureAwait(true);
        var hiddenVersion = await SnapshotAsync(s, (int)s.HiddenSheetId).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var reply = await CompareAsync(client, s, $"from={hiddenVersion}&to=current").ConfigureAwait(true);
        Assert.True(reply.Status == HttpStatusCode.OK, $"{reply.Status}\n{reply.Body}");

        var missing = await CompareAsync(client, s, "from=2000000001&to=current").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
    }

    private static void AssertSameRefusal(string param, Reply hidden, long hiddenId, Reply missing, long missingId)
    {
        Assert.True(missing.Status == HttpStatusCode.NotFound, $"{param}: неіснуюча версія: {missing.Status}\n{missing.Body}");
        Assert.True(hidden.Status == missing.Status, $"{param}: {hidden.Status} (схований) != {missing.Status} (неіснуючий)\n{hidden.Body}\n---\n{missing.Body}");
        Assert.True(
            Normalize(hidden.Body, hiddenId) == Normalize(missing.Body, missingId),
            $"{param}: тіла різняться\n{hidden.Body}\n---\n{missing.Body}");
    }

    private static async Task<Reply> CompareAsync(HttpClient client, Scenario s, string query)
    {
        using var response = await client.GetAsync(
            new Uri($"/api/v1/documents/{s.DocumentId}/compare?{query}", UriKind.Relative)).ConfigureAwait(false);
        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private async Task<long> SnapshotAsync(Scenario s, int sheetDefId)
    {
        await using var db = Context();
        var snapshot = new SubmissionSnapshot(
            s.DocumentId, sheetDefId, s.PeriodKey, s.TemplateVersionId,
            "[]", null, null, Ecr.Application.Workflow.SubmissionPayload.Write([]), new byte[32], DateTime.UtcNow, s.UserId);
        db.SubmissionSnapshots.Add(snapshot);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return snapshot.Id;
    }

    // ── Шляхи, що вже мали межу, але без власного доказу ───────────────────────────────────

    [Theory]
    [InlineData("scope")]
    [InlineData("deny")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Таблиці_статус_таблиць_і_валідація_не_називають_схований_аркуш_і_його_таблицю(string how)
    {
        var s = await ArrangeAsync(how, DocumentStatus.Rejected).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var tag = s.HiddenCode["HIDOR".Length..];

        var tables = await client.GetStringAsync(
            new Uri($"/api/v1/documents/{s.DocumentId}/tables?periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(true);
        var status = await client.GetStringAsync(
            new Uri($"/api/v1/documents/{s.DocumentId}/tables/status?periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(true);
        using var validateResponse = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.DocumentId}/validate", UriKind.Relative), new { periodKey = s.PeriodKey }).ConfigureAwait(true);
        var validate = await validateResponse.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.DoesNotContain(tag, tables, StringComparison.Ordinal);
        Assert.DoesNotContain(tag, status, StringComparison.Ordinal);
        Assert.DoesNotContain(tag, validate, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", validate, StringComparison.Ordinal);
    }

    // ── R-5: 409 видалення / зміни ключа без деталей схованого аркуша ──────────────────────

    [Theory]
    [InlineData("delete")]
    [InlineData("rekey")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Видалення_і_зміна_ключа_не_називають_схований_аркуш_і_його_стан(string action)
    {
        var s = await ArrangeAsync("deny", DocumentStatus.Submitted).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await DestructiveAsync(client, s, action).ConfigureAwait(true);

        Assert.True(response.Status == HttpStatusCode.Conflict, $"{action}: {response.Status}\n{response.Body}");
        foreach (var word in new[] { "sheetDefId", "Submitted", "Approved", s.HiddenCode })
        {
            Assert.DoesNotContain(word, response.Body, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("rekey")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Звичайний_читач_і_надалі_бачить_аркуш_і_стан_у_409(string action)
    {
        var s = await ArrangeAsync("none", DocumentStatus.Submitted).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await DestructiveAsync(client, s, action).ConfigureAwait(true);

        Assert.True(response.Status == HttpStatusCode.Conflict, $"{action}: {response.Status}\n{response.Body}");
        Assert.Contains("sheetDefId", response.Body, StringComparison.Ordinal);
    }

    // ── допоміжне ────────────────────────────────────────────────────────────────────────

    private sealed record Reply(HttpStatusCode Status, string Body);

    private static async Task<Reply> ActAsync(HttpClient client, Scenario s, string action, long sheetId)
    {
        var id = s.DocumentId;
        var period = s.PeriodKey;
        var (url, body) = action switch
        {
            "submit" => ($"/api/v1/documents/{id}/submit", (object)new { sheetDefId = sheetId, periodKey = period }),
            "approve" => ($"/api/v1/documents/{id}/approve", new { sheetDefId = sheetId, periodKey = period, approved = true }),
            "reopen" => ($"/api/v1/documents/{id}/reopen", new { sheetDefId = sheetId, periodKey = period, reason = "x" }),
            "recall" => ($"/api/v1/documents/{id}/recall", new { sheetDefId = sheetId, periodKey = period, reason = "x" }),
            "recalculate" => ($"/api/v1/documents/{id}/recalculate", new { periodKey = period, sheetDefId = sheetId }),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        using var response = await client.PostAsJsonAsync(new Uri(url, UriKind.Relative), body).ConfigureAwait(false);
        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private static async Task<Reply> DestructiveAsync(HttpClient client, Scenario s, string action)
    {
        using var response = action == "delete"
            ? await client.DeleteAsync(new Uri($"/api/v1/documents/{s.DocumentId}", UriKind.Relative)).ConfigureAwait(false)
            : await client.PostAsJsonAsync(
                new Uri($"/api/v1/documents/{s.DocumentId}/business-key", UriKind.Relative),
                new
                {
                    businessKey = "NEW-KEY-" + s.DocumentId.ToString(CultureInfo.InvariantCulture),
                    expectedBusinessKey = s.BusinessKey,
                    reason = "r",
                })
                .ConfigureAwait(false);

        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    /// <summary>Тіло без ідентифікаторів запиту й шумових полів: порівнюються код, ключ, текст і деталі.</summary>
    private static string Normalize(string body, long sheetId)
    {
        var text = Regex.Replace(body, "\"(traceId|correlationId|requestId)\"\\s*:\\s*\"[^\"]*\",?", string.Empty);
        return Regex.Replace(text, $"(?<![0-9A-Za-z]){sheetId.ToString(CultureInfo.InvariantCulture)}(?![0-9A-Za-z])", "<SHEET>");
    }

    private static void AssertVisibleOnly(string endpoint, string body, JsonElement doc, Scenario s, bool withPeriod)
    {
        Assert.DoesNotContain(s.HiddenCode, body, StringComparison.Ordinal);
        Assert.DoesNotContain(s.HiddenName, body, StringComparison.Ordinal);

        // sheetCount — ЛИШЕ видимі (мутація «повний SheetCount» червонить саме тут).
        Assert.True(doc.GetProperty("sheetCount").GetInt32() == 1, $"{endpoint}: sheetCount != 1\n{body}");

        var states = doc.GetProperty("sheetStates");
        var sheets = doc.GetProperty("sheets");
        if (withPeriod)
        {
            Assert.Equal(s.VisibleCode, Assert.Single(states.EnumerateObject()).Name);
            Assert.Equal(s.VisibleCode, Assert.Single(sheets.EnumerateArray()).GetProperty("code").GetString());
        }
        else
        {
            // Без періоду стану аркушів немає зовсім — і прихований теж не з'являється.
            Assert.Empty(states.EnumerateObject());
            Assert.Equal(0, sheets.GetArrayLength());
        }

        // Лічильники звуженого читача — null («—»), а не число видимого чи повного.
        foreach (var name in new[] { "errorCount", "warningCount" })
        {
            if (doc.TryGetProperty(name, out var value))
            {
                Assert.True(value.ValueKind == JsonValueKind.Null, $"{endpoint}: {name} = {value} (очікувалось null)\n{body}");
            }
        }
    }

    private static async Task<List<long>> ListedAsync(HttpClient client, int periodKey, string state)
    {
        var body = await client.GetStringAsync(
            new Uri($"/api/v1/documents?limit=200&periodKey={periodKey}&state={state}", UriKind.Relative)).ConfigureAwait(false);

        return [.. JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()
            .Select(d => d.GetProperty("id").GetInt64())];
    }

    private static async Task<JsonElement> SummaryAsync(HttpClient client, Scenario s)
        => JsonDocument.Parse(await client.GetStringAsync(
            new Uri($"/api/v1/documents/summary?periodKey={s.PeriodKey}&projectId={s.ProjectId}", UriKind.Relative))
            .ConfigureAwait(false)).RootElement;

    private sealed record Scenario(
        long DocumentId, int ProjectId, int PeriodKey, string UserName, string BusinessKey,
        string VisibleCode, long HiddenSheetId, string HiddenCode, string HiddenName,
        int VisibleSheetId = 0, int TemplateVersionId = 0, int UserId = 0);

    private async Task<Scenario> ArrangeAsync(string how, DocumentStatus hiddenStatus)
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(false);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var hiddenCode = $"HIDOR{tag}";
        var hiddenName = $"HiddenOracleName{tag}";
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var userName = $"ora{Guid.NewGuid():N}"[..20];

        await using var db = Context();

        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));

        var sheetB = new SheetDef(b.TemplateVersionId, EcrCode.Create(hiddenCode), Name(hiddenName), 2);
        db.SheetDefs.Add(sheetB);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var tableB = new TableDef(
            sheetB.Id, EcrCode.Create($"ORTB{tag}"), Name($"OracleTable{tag}"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(tableB);
        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, sheetB.Id));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Oracle role"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in new[]
                 {
                     "Document.View", "Document.Submit", "Document.Reopen", "Document.Delete", "Document.ChangeKey",
                     "Calculation.Recalculate",
                 })
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
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (how == "scope")
        {
            var scope = RoleAssignmentScope.Create([b.ProjectId], [b.SheetCode], null, null).ToJson();
            await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE sec.RoleAssignment SET ScopeJson = {scope} WHERE Id = {assignment.Id}")
                .ConfigureAwait(false);
        }

        // Стан B задає тест; A без рядка стану (чернетка).
        var state = new ApprovalState(b.DocumentId, sheetB.Id, b.PeriodKey.Value);
        state.Submit(user.Id, now);
        if (hiddenStatus == DocumentStatus.Rejected)
        {
            state.Reject(user.Id, "hidden rejection", now);
        }

        db.ApprovalStates.Add(state);

        // Підсумок прогону: A — 1 помилка + 1 попередження, B — 7 + 5.
        var messages = new List<ValidationMessage>
        {
            new(ValidationSeverity.Error, "RULE-A-E", "visible error", b.TableDefId, null, null, false),
            new(ValidationSeverity.Warning, "RULE-A-W", "visible warning", b.TableDefId, null, null, false),
        };
        for (var i = 0; i < 7; i++)
        {
            messages.Add(new(ValidationSeverity.Error, $"RULE-B-E{i.ToString(CultureInfo.InvariantCulture)}", "SECRET", tableB.Id, null, null, false));
        }

        db.ValidationResults.Add(new ValidationResult(
            b.DocumentId, b.PeriodKey.Value, now, 8, 1, 0, JsonSerializer.Serialize(messages)));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var businessKey = await db.Documents.AsNoTracking().Where(d => d.Id == b.DocumentId)
            .Select(d => d.BusinessKey).SingleAsync().ConfigureAwait(false);

        return new Scenario(b.DocumentId, b.ProjectId, b.PeriodKey.Value, userName, businessKey, b.SheetCode, sheetB.Id, hiddenCode, hiddenName,
            b.SheetDefId, b.TemplateVersionId, user.Id);
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
