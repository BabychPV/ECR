// tests/Ecr.Api.Tests/DocumentDeleteTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>DELETE /api/v1/documents/{id}</c> — лише чернетка (рішення людини 2026-09-21).
/// </summary>
/// <remarks>
/// Наскрізно, справжнім HTTP: 409 із доменного винятку дає правило суфікса
/// <c>-0409</c> у <c>ExceptionHandlingMiddleware</c>, і модульний тест його не бачить.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentDeleteTests(SqlServerFixture sql)
{
    private const string Password = "Api-Doc-Delete-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.3")]
    public async Task Чернетка_видаляється_разом_із_даними_і_лишає_слід_у_журналі_безпеки()
    {
        var s = await ArrangeAsync(DeleteDocumentHandler.Permission, GrantLevel.Write).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await DeleteAsync(client, s.Document.DocumentId).ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"{response.StatusCode}: {app.ErrorsText}");

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var id = s.Document.DocumentId;

        Assert.False(await db.Documents.AnyAsync(d => d.Id == id).ConfigureAwait(true));
        Assert.False(await db.TableInstances.AnyAsync(i => i.DocumentId == id).ConfigureAwait(true));
        Assert.False(await db.TableRows.AnyAsync(r => r.TableInstanceId == s.Document.TableInstanceId).ConfigureAwait(true));
        Assert.False(await db.ApprovalStates.AnyAsync(a => a.DocumentId == id).ConfigureAwait(true));

        // Хто, який документ і скільки комірок: дві комірки клав ArrangeAsync.
        var events = await db.Database
            .SqlQuery<string>($"SELECT ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType = N'DocumentDeleted' AND ChangedByUserId = {s.UserId}")
            .ToListAsync().ConfigureAwait(true);

        var details = JsonDocument.Parse(Assert.Single(events)).RootElement;
        Assert.Equal(id, details.GetProperty("documentId").GetInt64());
        Assert.Equal(2, details.GetProperty("cells").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Поданий_документ_дає_409_з_причиною_і_лишається_на_місці()
    {
        var s = await ArrangeAsync(DeleteDocumentHandler.Permission, GrantLevel.Write, submitted: true)
            .ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await DeleteAsync(client, s.Document.DocumentId).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-DOC-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal("Submitted", problem.GetProperty("reason").GetString());

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.True(await db.Documents.AnyAsync(d => d.Id == s.Document.DocumentId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_Document_Delete_403()
    {
        var s = await ArrangeAsync("Document.View", GrantLevel.Write).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await DeleteAsync(client, s.Document.DocumentId).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Документ_чужого_проєкту_404_як_неіснуючий()
    {
        var s = await ArrangeAsync(DeleteDocumentHandler.Permission, grant: null).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await DeleteAsync(client, s.Document.DocumentId).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.True(await db.Documents.AnyAsync(d => d.Id == s.Document.DocumentId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Неіснуючий_документ_і_повторне_видалення_дають_404_а_не_500()
    {
        // RC15 E-1: DELETE ніколи не віддає 500 - ні для Id, якого немає, ні для вже видаленого документа
        // (відповідь та сама, що й для чужого проєкту: наявність документа не розкривається).
        var s = await ArrangeAsync(DeleteDocumentHandler.Permission, GrantLevel.Write).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var missing = await DeleteAsync(client, long.MaxValue - 7).ConfigureAwait(true);
        Assert.True(missing.StatusCode == HttpStatusCode.NotFound, $"{missing.StatusCode}: {app.ErrorsText}");

        var first = await DeleteAsync(client, s.Document.DocumentId).ConfigureAwait(true);
        Assert.True(first.StatusCode == HttpStatusCode.NoContent, $"{first.StatusCode}: {app.ErrorsText}");

        var again = await DeleteAsync(client, s.Document.DocumentId).ConfigureAwait(true);
        Assert.True(again.StatusCode == HttpStatusCode.NotFound, $"{again.StatusCode}: {app.ErrorsText}");
    }

    /// <summary>
    /// R9-F3 / F3-01: чернетка з даними в закритому періоді не видаляється — «закритий період
    /// блокує всіх» діє й на видалення; комірки лишаються на місці.
    /// </summary>
    /// <remarks>Мутація: прибрати виклик <c>EnsureNotFrozenAsync</c> у <c>DeleteDocumentHandler</c> — 204, червоніє.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.10")]
    public async Task Чернетка_з_даними_в_закритому_періоді_дає_409_і_дані_лишаються()
    {
        var s = await ArrangeAsync(DeleteDocumentHandler.Permission, GrantLevel.Write).ConfigureAwait(true);

        await using (var arrange = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var updated = await arrange.Database.ExecuteSqlAsync(
                $"UPDATE doc.Period SET State = {(int)PeriodState.Closed} WHERE ProjectId = {s.Document.ProjectId} AND PeriodKey = {s.Document.PeriodKey.Value}")
                .ConfigureAwait(true);
            Assert.Equal(1, updated);
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await DeleteAsync(client, s.Document.DocumentId).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-DOC-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal("PeriodClosed", problem.GetProperty("reason").GetString());

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.True(await db.Documents.AnyAsync(d => d.Id == s.Document.DocumentId).ConfigureAwait(true));
        Assert.Equal(2, await db.CellValues.CountAsync(c => s.Document.RowIds.Contains(c.TableRowId)).ConfigureAwait(true));
    }

    /// <summary>R9-F3 / F3-01: в архівованому проєкті документ не видаляється.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Документ_архівованого_проєкту_дає_409_і_лишається_на_місці()
    {
        var s = await ArrangeAsync(DeleteDocumentHandler.Permission, GrantLevel.Write).ConfigureAwait(true);

        await using (var arrange = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var updated = await arrange.Database.ExecuteSqlAsync(
                $"UPDATE doc.Project SET Status = {(int)ProjectStatus.Archived} WHERE Id = {s.Document.ProjectId}")
                .ConfigureAwait(true);
            Assert.Equal(1, updated);
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await DeleteAsync(client, s.Document.DocumentId).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-DOC-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal("ProjectArchived", problem.GetProperty("reason").GetString());

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.True(await db.Documents.AnyAsync(d => d.Id == s.Document.DocumentId).ConfigureAwait(true));
    }

    /// <summary>
    /// F2-03 (аудит R11): слід видалення пишеться В ТІЙ САМІЙ транзакції. Збій запису журналу
    /// відкочує видалення — документ і комірки лишаються. Доти журнал ішов ПІСЛЯ коміту: збій
    /// (або kill) лишав документ стертим без жодного сліду «хто видалив».
    /// </summary>
    /// <remarks>Мутація: повернути запис журналу після `ExecuteInTransactionAsync` — документ зникає, червоніє.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "F2-03")]
    public async Task Збій_запису_журналу_відкочує_видалення_документа()
    {
        var s = await ArrangeAsync(DeleteDocumentHandler.Permission, GrantLevel.Write).ConfigureAwait(true);

        using var baseApp = new EcrApiFactory(sql);
        using var app = FailingSecurityEventAuditWriter.Install(baseApp, DeleteDocumentHandler.DeletedEventType);
        using var client = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);

        var response = await DeleteAsync(client, s.Document.DocumentId).ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.InternalServerError, $"{response.StatusCode}: {baseApp.ErrorsText}");
        Assert.Contains(FailingSecurityEventAuditWriter.Marker, baseApp.ErrorsText, StringComparison.Ordinal);

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var id = s.Document.DocumentId;
        Assert.True(await db.Documents.AnyAsync(d => d.Id == id).ConfigureAwait(true), "Документ видалено, хоча запис журналу не вдався.");
        Assert.Equal(2, await db.CellValues.CountAsync(c => s.Document.RowIds.Contains(c.TableRowId)).ConfigureAwait(true));
    }

    /// <summary>
    /// X1-02 (аудит R11): видалення чернетки перераховує статус поточних зрізів проєкту. Зріз був
    /// <c>Draft</c> лише через цю чернетку; решта документів затверджена — після видалення зріз
    /// <c>Approved</c>. Доти статус лишався старим, і регуляторна вʼюха зрізу не бачила.
    /// </summary>
    /// <remarks>Мутація: прибрати виклик `RefreshProjectAsync` — статус `Draft`, червоніє.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "X1-02")]
    public async Task Видалення_чернетки_перераховує_статус_поточного_зрізу_проєкту()
    {
        var s = await ArrangeAsync(DeleteDocumentHandler.Permission, GrantLevel.Write).ConfigureAwait(true);

        long snapshotId;
        await using (var arrange = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var now = new DateTime(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

            // Другий документ проєкту: аркуш затверджено. Перший (s.Document) — чернетка, що й тримає зріз у Draft.
            var other = new Document(s.Document.ProjectId, $"X102-{Guid.NewGuid():N}"[..16], s.UserId, now);
            arrange.Documents.Add(other);
            await arrange.SaveChangesAsync().ConfigureAwait(true);

            arrange.DocumentSheets.Add(new DocumentSheet(other.Id, s.Document.SheetDefId));
            var state = new ApprovalState(other.Id, s.Document.SheetDefId, s.Document.PeriodKey.Value);
            state.Submit(1, now);
            state.Approve(1, now);
            arrange.ApprovalStates.Add(state);

            var tag = Guid.NewGuid().ToString("N")[..8];
            var definition = new ReportDef(
                EcrCode.Create($"RPT{tag}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "X1-02" }),
                isRegulatory: true);
            arrange.ReportDefs.Add(definition);
            await arrange.SaveChangesAsync().ConfigureAwait(true);

            var version = new ReportVersion(definition.Id, "1.0", "[]", "{}", now);
            arrange.ReportVersions.Add(version);
            await arrange.SaveChangesAsync().ConfigureAwait(true);

            var snapshot = new ReportSnapshot(
                version.Id, s.Document.ProjectId, s.Document.PeriodKey.Value, SnapshotStatus.Draft, now, builtByUserId: 1);
            snapshot.MakeCurrent();
            arrange.ReportSnapshots.Add(snapshot);
            await arrange.SaveChangesAsync().ConfigureAwait(true);
            snapshotId = snapshot.Id;
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await DeleteAsync(client, s.Document.DocumentId).ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"{response.StatusCode}: {app.ErrorsText}");

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var status = await db.ReportSnapshots.AsNoTracking()
            .Where(r => r.Id == snapshotId).Select(r => r.Status).SingleAsync().ConfigureAwait(true);
        Assert.Equal(SnapshotStatus.Approved, status);
    }

    private static async Task<HttpClient> SignedInAsync(
        WebApplicationFactory<Program> app, string userName, EcrApiFactory errors)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {errors.ErrorsText}");
        return client;
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, long documentId)
        => client.DeleteAsync(new Uri(
            $"/api/v1/documents/{documentId.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            UriKind.Relative));

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    /// <summary>Документ із двома комірками і користувач зі своєю роллю.</summary>
    /// <param name="permission">Єдине функціональне право ролі.</param>
    /// <param name="grant">Грант на проєкт документа; <c>null</c> — жодного.</param>
    /// <param name="submitted">Подати аркуш документа за період.</param>
    private async Task<Scenario> ArrangeAsync(string permission, GrantLevel? grant, bool submitted = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 1, rowCount: 2).ConfigureAwait(false);

        await using var db = builder.CreateContext();

        foreach (var rowId in document.RowIds)
        {
            db.CellValues.Add(new CellValue(
                new CellAddress(document.PeriodKey, rowId, document.ColumnDefIds[0]),
                document.TableDefId,
                new CellValueData { ValueString = "x" }));
        }

        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));

        if (submitted)
        {
            var state = new ApprovalState(document.DocumentId, document.SheetDefId, document.PeriodKey.Value);
            state.Submit(1, new DateTime(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc));
            db.ApprovalStates.Add(state);
        }

        var userName = $"docdel_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        // Роль своя на кожен прогін: база спільна на всю збірку.
        var role = new Role(
            EcrCode.Create($"DOCDEL_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Doc delete" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, permission));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        if (grant is { } level)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, level));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(document, userName, user.Id);
    }

    private sealed record Scenario(TestDocument Document, string UserName, int UserId);
}
