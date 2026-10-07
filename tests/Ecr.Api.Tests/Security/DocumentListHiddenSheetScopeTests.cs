// tests/Ecr.Api.Tests/Security/DocumentListHiddenSheetScopeTests.cs
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
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
/// S0 (R1/R2 плану UI-BACKEND): перелік документів, документ і зведення для ролі, чий доступ
/// звужено до аркуша A, не відкривають назву, стан і лічильники зауважень прихованого аркуша B.
/// </summary>
/// <remarks>
/// Сценарій: документ із двома аркушами. A видимий (чернетка, 1 помилка + 1 попередження),
/// B прихований — Rejected, 7 помилок + 5 попереджень у збереженому підсумку валідації. Приховування
/// двома механізмами: звуження призначення ролі аркушами (D-214) і заборона `Deny` на аркуш (ресурсний грант).
/// Лічильники B навмисно різні (7/5), щоб відрізнити їх від видимих (1/1) і від суми (8/6).
/// ⛔ Мутаційний доказ: прибрати фільтр прихованого (відкрити всі аркуші/лічильники у відповіді) —
/// тест червоніє на відповідному полі.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentListHiddenSheetScopeTests(SqlServerFixture sql)
{
    private const string Password = "Hidden-Sheet-Scope-S0!";
    private const int HiddenErrors = 7;
    private const int HiddenWarnings = 5;
    private const string SecretText = "SECRET-HIDDEN-SHEET-FINDING";

    [Theory]
    [InlineData("scope")]
    [InlineData("deny")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Перелік_не_відкриває_прихований_аркуш_і_його_лічильники(string how)
    {
        var s = await ArrangeAsync(how).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var body = await client.GetStringAsync(
            new Uri($"/api/v1/documents?limit=200&periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(true);
        var item = JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()
            .Single(d => d.GetProperty("id").GetInt64() == s.DocumentId);

        AssertNoLeak("GET /documents", body, item, s);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("deny")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Документ_не_відкриває_прихований_аркуш_і_його_лічильники(string how)
    {
        var s = await ArrangeAsync(how).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var body = await client.GetStringAsync(
            new Uri($"/api/v1/documents/{s.DocumentId}?periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(true);

        AssertNoLeak("GET /documents/{id}", body, JsonDocument.Parse(body).RootElement, s);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("deny")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Зведення_не_враховує_прихований_аркуш(string how)
    {
        var s = await ArrangeAsync(how).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var body = await client.GetStringAsync(
            new Uri($"/api/v1/documents/summary?periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(true);
        var root = JsonDocument.Parse(body).RootElement;

        // Видимий документ має єдиний видимий аркуш у чернетці; Rejected прихованого аркуша
        // не може зробити документ «відхиленим» для читача.
        var leaks = new List<string>();
        if (root.GetProperty("rejected").GetInt32() != 0)
        {
            leaks.Add($"rejected={root.GetProperty("rejected").GetInt32()} (стан прихованого аркуша B)");
        }

        if (root.GetProperty("draft").GetInt32() != 1)
        {
            leaks.Add($"draft={root.GetProperty("draft").GetInt32()} замість 1");
        }

        Assert.True(leaks.Count == 0, $"GET /documents/summary: {string.Join("; ", leaks)}\n{body}");
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("deny")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Збережений_підсумок_валідації_не_відкриває_прихований_аркуш(string how)
    {
        var s = await ArrangeAsync(how).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var body = await client.GetStringAsync(
            new Uri($"/api/v1/documents/{s.DocumentId}/validation?periodKey={s.PeriodKey}", UriKind.Relative))
            .ConfigureAwait(true);

        Assert.DoesNotContain(SecretText, body, StringComparison.Ordinal);
        Assert.DoesNotContain(s.HiddenCode, body, StringComparison.Ordinal);

        var root = JsonDocument.Parse(body).RootElement;
        foreach (var name in new[] { "errorCount", "warningCount" })
        {
            if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
            {
                var max = name == "errorCount" ? 2 : 1;
                Assert.True(v.GetInt32() <= max, $"GET /validation: {name}={v.GetInt32()} — лічильник прихованого\n{body}");
            }
        }
    }

    /// <summary>
    /// Паритет для ЗВИЧАЙНОЇ ролі (жодного обмеження нижче проєкту): фільтр нічого не відсікає,
    /// перелік показує обидва аркуші з повними лічильниками, а зведення збігається з переліком.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Звичайна_роль_бачить_усе_і_зведення_збігається_з_переліком()
    {
        var s = await ArrangeAsync("none").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var listBody = await client.GetStringAsync(
            new Uri($"/api/v1/documents?limit=200&periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(true);
        var item = JsonDocument.Parse(listBody).RootElement.GetProperty("items").EnumerateArray()
            .Single(d => d.GetProperty("id").GetInt64() == s.DocumentId);
        var summary = JsonDocument.Parse(await client.GetStringAsync(
            new Uri($"/api/v1/documents/summary?periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(true)).RootElement;

        Assert.Equal(2, item.GetProperty("sheetCount").GetInt32());
        Assert.Equal(2, item.GetProperty("sheets").GetArrayLength());
        Assert.Equal("Rejected", item.GetProperty("sheetStates").GetProperty(s.HiddenCode).GetString());
        Assert.Equal(1 + HiddenErrors, item.GetProperty("errorCount").GetInt32());
        Assert.Equal(1 + HiddenWarnings, item.GetProperty("warningCount").GetInt32());

        // Документ з відхиленим аркушем — «Відхилено» і в зведенні; із зауваженнями — теж.
        Assert.True(summary.GetProperty("rejected").GetInt32() >= 1);
        Assert.True(summary.GetProperty("withIssues").GetInt32() >= 1);
    }

    /// <summary>Спільні твердження: жодної ознаки B у відповіді, лічильники лише видимого.</summary>
    private static void AssertNoLeak(string endpoint, string body, JsonElement doc, Scenario s)
    {
        var leaks = new List<string>();

        if (body.Contains(s.HiddenCode, StringComparison.Ordinal))
        {
            leaks.Add("код прихованого аркуша B у відповіді");
        }

        if (body.Contains(s.HiddenName, StringComparison.Ordinal))
        {
            leaks.Add("назва прихованого аркуша B у відповіді");
        }

        if (body.Contains(SecretText, StringComparison.Ordinal))
        {
            leaks.Add("текст зауваження прихованого аркуша");
        }

        if (doc.TryGetProperty("sheetStates", out var states) && states.ValueKind == JsonValueKind.Object
            && states.EnumerateObject().Any(p => p.Name == s.HiddenCode))
        {
            leaks.Add($"sheetStates містить {s.HiddenCode} = {states.GetProperty(s.HiddenCode)}");
        }

        if (doc.TryGetProperty("sheets", out var sheets) && sheets.ValueKind == JsonValueKind.Array
            && sheets.EnumerateArray().Any(x => x.GetProperty("code").GetString() == s.HiddenCode))
        {
            leaks.Add("sheets містить прихований аркуш B (код, назва, стан)");
        }

        // ErrorCount/WarningCount — підсумок прогону по всьому документу, тож приховані 7/5
        // видно як число. Допустимо `null` або лише видиме (A: 1/1; +1 — знеособлене зауваження).
        if (doc.TryGetProperty("errorCount", out var e) && e.ValueKind == JsonValueKind.Number && e.GetInt32() > 2)
        {
            leaks.Add($"errorCount={e.GetInt32()} (видимих 1; прихованих {HiddenErrors})");
        }

        if (doc.TryGetProperty("warningCount", out var w) && w.ValueKind == JsonValueKind.Number && w.GetInt32() > 1)
        {
            leaks.Add($"warningCount={w.GetInt32()} (видимих 1; прихованих {HiddenWarnings})");
        }

        Assert.True(leaks.Count == 0, $"{endpoint}: ВИТІК: {string.Join("; ", leaks)}\n{body}");
    }

    private sealed record Scenario(
        long DocumentId, int PeriodKey, string UserName, string HiddenCode, string HiddenName);

    private async Task<Scenario> ArrangeAsync(string how)
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(false);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var hiddenCode = $"HIDSH{tag}";
        var hiddenName = $"HiddenSheetName{tag}";
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var userName = $"hid{Guid.NewGuid():N}"[..20];

        await using var db = Context();

        // Видимий аркуш A — у складі документа.
        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));

        // Прихований аркуш B із таблицею.
        var sheetB = new SheetDef(b.TemplateVersionId, EcrCode.Create(hiddenCode), Name(hiddenName), 2);
        db.SheetDefs.Add(sheetB);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var tableB = new TableDef(
            sheetB.Id, EcrCode.Create($"HIDTB{tag}"), Name($"HiddenTable{tag}"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(tableB);
        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, sheetB.Id));
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Користувач і його роль.
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("S0 narrowed"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, b.ProjectId, GrantLevel.Read));
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

        // Стан B — Rejected; A без рядка стану (чернетка).
        var state = new ApprovalState(b.DocumentId, sheetB.Id, b.PeriodKey.Value);
        state.Submit(user.Id, now);
        state.Reject(user.Id, "S0 hidden rejection", now);
        db.ApprovalStates.Add(state);

        // Підсумок прогону: A — 1 помилка + 1 попередження, B — 7 + 5.
        var messages = new List<ValidationMessage>
        {
            new(ValidationSeverity.Error, "RULE-A-E", "visible error", b.TableDefId, null, null, false),
            new(ValidationSeverity.Warning, "RULE-A-W", "visible warning", b.TableDefId, null, null, false),
        };
        for (var i = 0; i < HiddenErrors; i++)
        {
            messages.Add(new(ValidationSeverity.Error, $"RULE-B-E{i.ToString(CultureInfo.InvariantCulture)}", SecretText, tableB.Id, null, null, false));
        }

        for (var i = 0; i < HiddenWarnings; i++)
        {
            messages.Add(new(ValidationSeverity.Warning, $"RULE-B-W{i.ToString(CultureInfo.InvariantCulture)}", SecretText, tableB.Id, null, null, false));
        }

        db.ValidationResults.Add(new ValidationResult(
            b.DocumentId, b.PeriodKey.Value, now, 1 + HiddenErrors, 1 + HiddenWarnings, 0,
            JsonSerializer.Serialize(messages)));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(b.DocumentId, b.PeriodKey.Value, userName, hiddenCode, hiddenName);
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
