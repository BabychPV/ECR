// tests/Ecr.Api.Tests/DocumentParticipantSummaryIssuesApiTests.cs
using System.Globalization;
using System.Net;
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

namespace Ecr.Api.Tests;

/// <summary>
/// P45 (борг D-446) наскрізно через HTTP: лічильник «З зауваженнями» (<c>withIssues</c>) смуги зведення для документа,
/// відкритого лише наданням учасника (D-443), — за тим самим правилом, що й <c>errorCount</c> його рядка.
/// </summary>
/// <remarks>
/// ⛔ Смуга дорівнює кількості рядків переліку з <c>errorCount</c> &gt; 0: документ-надання, видимий учасникові цілком,
/// рахується; частково схований (рядок — <c>null</c>, бо збережений підсумок — по всьому документу) — ні.
/// <para>Мутаційний доказ (локально, не в коміті):</para>
/// <list type="bullet">
/// <item>у <c>DocumentListSummaryStore.SummarizeAsync</c> прибрати гілку <c>OPENJSON({participantIssueJson})</c> підзапиту
/// <c>LastErrorCount</c> (стара поведінка) → червоніє <c>Цілком_видимий_документ_учасника_з_помилкою_в_смузі</c>
/// (<c>withIssues</c> = 0 при рядку з <c>errorCount</c> = 1);</item>
/// <item>у тій самій гілці прибрати <c>NOT EXISTS</c> по аркушах складу і в <c>GetDocumentListSummaryHandler</c> віддати в
/// <c>IssueDocumentIds</c> усі документи-надання → червоніє
/// <c>Частково_схований_документ_учасника_поза_смугою_з_помилкою_схованого</c> (<c>withIssues</c> = 1 при рядку з
/// <c>errorCount</c> = <c>null</c>);</item>
/// <item>прибрати умову <c>d.Id NOT IN participantDocs</c> гілки проєкту — тести лишаються зеленими (учасник без ролі
/// на проєкт), а контроль менеджера тримає гілку проєкту.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentParticipantSummaryIssuesApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Participant-Issues-2026!";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-446")]
    public async Task Цілком_видимий_документ_учасника_з_помилкою_в_смузі()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        // Надання на ВЕСЬ документ: учасник бачить кожен аркуш; помилка — у видимому аркуші 1.
        var participant = await NewParticipantAsync(s, "piw").ConfigureAwait(true);
        await GrantAsync(app, manager, s, null, null, participant).ConfigureAwait(true);
        await IssueAsync(s, s.TableDefId1).ConfigureAwait(true);
        using var client = await SignedInAsync(app, participant.UserName).ConfigureAwait(true);

        var rows = await ListAsync(app, client, s, string.Empty).ConfigureAwait(true);
        Assert.Equal(1, Field(Row(rows, s), "errorCount").GetInt32());

        // ⛔ Смуга = рядки з позначкою помилок; у межах фільтра проєкту — те саме, поза ним — нуль.
        var summary = await SummaryAsync(app, client, s, string.Empty).ConfigureAwait(true);
        Assert.Equal(1, summary.GetProperty("withIssues").GetInt32());
        Assert.Equal(WithErrors(rows), summary.GetProperty("withIssues").GetInt32());
        Assert.Equal(
            1,
            (await SummaryAsync(app, client, s, $"&projectId={Num(s.ProjectId)}").ConfigureAwait(true))
                .GetProperty("withIssues").GetInt32());
        Assert.Equal(
            0,
            (await SummaryAsync(app, client, s, $"&projectId={Num(s.ProjectId + 1_000_000)}").ConfigureAwait(true))
                .GetProperty("withIssues").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-446")]
    public async Task Частково_схований_документ_учасника_поза_смугою_з_помилкою_схованого()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, s.Manager.UserName).ConfigureAwait(true);

        // Учасникові надано лише таблицю аркуша 1; помилка — лише в схованому аркуші 2.
        var participant = await NewParticipantAsync(s, "pip").ConfigureAwait(true);
        await GrantAsync(app, manager, s, s.SheetCode1, s.TableCode1, participant).ConfigureAwait(true);
        await IssueAsync(s, s.TableDefId2).ConfigureAwait(true);

        // Контроль сценарію: менеджер (роль на проєкт) бачить помилку і в рядку, і в смузі.
        var managerRows = await ListAsync(app, manager, s, string.Empty).ConfigureAwait(true);
        Assert.Equal(1, Field(Row(managerRows, s), "errorCount").GetInt32());
        var managerSummary = await SummaryAsync(app, manager, s, string.Empty).ConfigureAwait(true);
        Assert.True(managerSummary.GetProperty("withIssues").GetInt32() >= 1);
        Assert.Equal(WithErrors(managerRows), managerSummary.GetProperty("withIssues").GetInt32());

        // ⛔ Учасник: документ у переліку є, але лічильник помилок — null, і смуга його не рахує.
        using var client = await SignedInAsync(app, participant.UserName).ConfigureAwait(true);
        var rows = await ListAsync(app, client, s, string.Empty).ConfigureAwait(true);
        Assert.Equal(JsonValueKind.Null, Field(Row(rows, s), "errorCount").ValueKind);

        var summary = await SummaryAsync(app, client, s, string.Empty).ConfigureAwait(true);
        Assert.Equal(0, summary.GetProperty("withIssues").GetInt32());
        Assert.Equal(WithErrors(rows), summary.GetProperty("withIssues").GetInt32());
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static JsonElement Row(List<JsonElement> items, Scenario s) => items.Single(d => d.GetProperty("id").GetInt64() == s.DocumentId);

    /// <summary>Рядки з видимою позначкою помилок — те, з чим смуга мусить збігатися.</summary>
    private static int WithErrors(List<JsonElement> items)
        => items.Count(d => Field(d, "errorCount") is { ValueKind: JsonValueKind.Number } e && e.GetInt32() > 0);

    /// <summary>Поле відповіді; відсутнє розглядається як <c>null</c> (серіалізатор може пропускати null).</summary>
    private static JsonElement Field(JsonElement doc, string name)
        => doc.TryGetProperty(name, out var value) ? value : JsonDocument.Parse("null").RootElement;

    private static async Task<List<JsonElement>> ListAsync(EcrApiFactory app, HttpClient client, Scenario s, string extra)
    {
        var list = await JsonAsync(
            await client.GetAsync(new Uri($"/api/v1/documents?limit=200&periodKey={s.PeriodKey}{extra}", UriKind.Relative)).ConfigureAwait(false),
            HttpStatusCode.OK, app).ConfigureAwait(false);
        return [.. list.GetProperty("items").EnumerateArray().Select(e => e.Clone())];
    }

    private static async Task<JsonElement> SummaryAsync(EcrApiFactory app, HttpClient client, Scenario s, string extra)
        => await JsonAsync(
            await client.GetAsync(new Uri($"/api/v1/documents/summary?periodKey={s.PeriodKey}{extra}", UriKind.Relative)).ConfigureAwait(false),
            HttpStatusCode.OK, app).ConfigureAwait(false);

    private static async Task GrantAsync(
        EcrApiFactory app, HttpClient manager, Scenario s, string? sheet, string? table, Person recipient)
    {
        using var response = await manager.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{Num(s.DocumentId)}/edit-assignments", UriKind.Relative),
            new
            {
                slice = new { sheetCode = sheet, tableCode = table },
                recipientUserIds = new[] { recipient.Id },
                isExclusive = false,
                level = "Read",
            }).ConfigureAwait(false);
        await JsonAsync(response, HttpStatusCode.OK, app).ConfigureAwait(false);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status, EcrApiFactory app)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
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

    /// <summary>Збережений підсумок перевірки: одна помилка в таблиці <paramref name="tableDefId"/>.</summary>
    private async Task IssueAsync(Scenario s, int tableDefId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var messages = new List<ValidationMessage>
        {
            new(ValidationSeverity.Error, "RULE-P45-E", "participant summary issue", tableDefId, null, null, false),
        };
        db.ValidationResults.Add(new ValidationResult(
            s.DocumentId, s.PeriodKey, new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc), 1, 0, 0,
            JsonSerializer.Serialize(messages)));
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Учасник без жодного гранта на проєкт: власна роль без прав.</summary>
    private async Task<Person> NewParticipantAsync(Scenario s, string prefix)
        => await NewUserAsync(s.ProjectId, prefix, level: null, withView: false).ConfigureAwait(false);

    private async Task<Person> NewUserAsync(int projectId, string prefix, GrantLevel? level, bool withView)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var userName = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"DI_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Participant issues test" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (withView)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        if (level is { } l)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, l));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return new Person(user.Id, userName);
    }

    /// <summary>Документ із ДВОМА аркушами у складі (по таблиці, колонці й екземпляру за відкритий період) і менеджер (Manage).</summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(false);
        await builder.OpenPeriodAsync(document).ConfigureAwait(false);

        await using var db = builder.CreateContext();
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var sheet2 = new SheetDef(document.TemplateVersionId, EcrCode.Create($"SHI{tag}"), Text("Sheet I"), 2);
        db.SheetDefs.Add(sheet2);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var table2 = new TableDef(sheet2.Id, EcrCode.Create($"TBI{tag}"), Text("Table I"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table2);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var column2 = new ColumnDef(table2.Id, EcrCode.Create($"CI{tag}"), Text("Col I"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(column2);
        db.RowDefs.Add(new RowDef(table2.Id, RowKey.Create($"RI{tag}"), 1, Text("Row I"), RowKind.Item));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instance2 = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(false);
        var row2 = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None).ConfigureAwait(false);
        db.TableInstances.Add(new TableInstance(document.PeriodKey, instance2, document.DocumentId, table2.Id, now));
        db.TableRows.Add(new TableRow(document.PeriodKey, row2, instance2, RowKey.Create($"RI{tag}"), 1, now));
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, sheet2.Id));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var tableCode1 = await db.TableDefs.Where(t => t.Id == document.TableDefId).Select(t => t.Code).SingleAsync().ConfigureAwait(false);

        var manager = await NewUserAsync(document.ProjectId, "pim", GrantLevel.Manage, withView: true).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.Document SET CreatedByUserId = {manager.Id} WHERE Id = {document.DocumentId}").ConfigureAwait(false);

        return new Scenario(
            document.DocumentId, document.ProjectId, document.PeriodKey.Value,
            document.SheetCode, tableCode1, document.TableDefId, table2.Id, manager);
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Person(int Id, string UserName);

    private sealed record Scenario(
        long DocumentId, int ProjectId, int PeriodKey, string SheetCode1, string TableCode1, int TableDefId1, int TableDefId2,
        Person Manager);
}
