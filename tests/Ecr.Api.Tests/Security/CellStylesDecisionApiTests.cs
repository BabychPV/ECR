// tests/Ecr.Api.Tests/Security/CellStylesDecisionApiTests.cs
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// CELL-STYLE L3b, рішення запису оформлення комірок на справжній СУБД через HTTP: стан аркуша
/// (Submitted/Approved/відкритий після Reopen), архівований проєкт, передача зрізу (виконавець, «чужа»
/// комірка, Filled/Validated, ексклюзивність для решти), стеля 2000/2001 з часом.
/// </summary>
/// <remarks>
/// ⚠ Стан аркуша тут відхиляється ще ДО транзакції (рішення про права). Повторну перевірку під блокуванням
/// ізолює юніт-тест <c>PatchCellStylesAssignmentGateTests</c> (мутація на <c>EnsureSheetEditable</c>).
/// </remarks>
[Collection("SqlServer")]
public sealed class CellStylesDecisionApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-CellStyle-Decision-2026!";

    /// <summary>⚠ Не 2026-05…07 (архівує <c>ArchiveJobTests</c>) і не 202611 (інший клас цього набору).</summary>
    private const int PeriodKeyValue = 202612;

    [Theory]
    [InlineData(DocumentStatus.Submitted, "DocumentSubmitted")]
    [InlineData(DocumentStatus.Approved, "DocumentApproved")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Поданий_чи_затверджений_аркуш_відмовляє_писаря_без_запису(DocumentStatus status, string reason)
    {
        var s = await ArrangeAsync(sheetStatus: status).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.Writer).ConfigureAwait(true);

        var response = await PatchAsync(client, s, "#FFFF00").ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Contains("ECR-ACCS-0403", body, StringComparison.Ordinal);
        Assert.Contains(reason, body, StringComparison.Ordinal);
        Assert.False(await HasStyleAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відкритий_період_і_аркуш_після_Reopen_дозволяє_оформлення()
    {
        var s = await ArrangeAsync(sheetStatus: DocumentStatus.Approved, reopen: true).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.Writer).ConfigureAwait(true);

        var response = await PatchAsync(client, s, "#FFFF00").ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.True(await HasStyleAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Архівований_проєкт_слайс_читається_без_помилки_а_запис_відмовляється()
    {
        var s = await ArrangeAsync(archive: true).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.Manager).ConfigureAwait(true);

        var slice = await client.GetAsync(SliceUri(s)).ConfigureAwait(true);
        var sliceBody = await slice.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(slice.StatusCode == HttpStatusCode.OK, $"{slice.StatusCode}: {sliceBody}\n{app.ErrorsText}");

        var response = await PatchAsync(client, s, "#FFFF00").ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Contains("ProjectArchived", body, StringComparison.Ordinal);
        Assert.False(await HasStyleAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Виконавець_передачі_фарбує_свою_частину_а_Filled_і_Validated_відмовляють_без_запису()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var owner = await SignInAsync(app, s.Manager).ConfigureAwait(true);
        using var exec = await SignInAsync(app, s.Executor).ConfigureAwait(true);

        // Без передачі виконавець (грант Read) фарбувати не може.
        var before = await PatchAsync(exec, s, "#FFFF00").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);

        var created = await owner.PostAsJsonAsync(
            ListUri(s), new { slice = new { sheetCode = s.SheetCode }, recipientUserIds = new[] { s.ExecutorId }, isExclusive = true })
            .ConfigureAwait(true);
        var created1 = await ReadAsync(created, HttpStatusCode.OK, app).ConfigureAwait(true);
        var id = created1.GetProperty("id").GetInt64();

        // Підйом передачею: Read -> Write у своєму зрізі.
        var allowed = await PatchAsync(exec, s, "#FFFF00").ConfigureAwait(true);
        var allowedBody = await allowed.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(allowed.StatusCode == HttpStatusCode.OK, $"{allowed.StatusCode}: {allowedBody}\n{app.ErrorsText}");
        Assert.True(await HasStyleAsync(s).ConfigureAwait(true));
        await ClearStylesAsync(s).ConfigureAwait(true);

        // Заповнено: виконавець більше не пише.
        var filled = await ReadAsync(
            await exec.PostAsync(ActionUri(s, id, "fill"), null).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        var filledResponse = await PatchAsync(exec, s, "#00FF00").ConfigureAwait(true);
        var filledBody = await filledResponse.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(filledResponse.StatusCode == HttpStatusCode.Forbidden, $"{filledResponse.StatusCode}: {filledBody}");
        Assert.Contains("AssignmentFilled", filledBody, StringComparison.Ordinal);
        Assert.False(await HasStyleAsync(s).ConfigureAwait(true));

        // Валідовано власником.
        await ReadAsync(
            await owner.PostAsJsonAsync(ActionUri(s, id, "validate"), new { baseVersion = filled.GetProperty("version").GetString() })
                .ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        var validated = await PatchAsync(exec, s, "#00FF00").ConfigureAwait(true);
        var validatedBody = await validated.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(validated.StatusCode == HttpStatusCode.Forbidden, $"{validated.StatusCode}: {validatedBody}");
        Assert.Contains("AssignmentValidated", validatedBody, StringComparison.Ordinal);
        Assert.False(await HasStyleAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ексклюзивна_передача_замикає_решту_а_чужа_комірка_виконавця_не_піднімається()
    {
        // Друга колонка стає місячною (травень): передача — лише на цей місяць; фарбована комірка (перша колонка)
        // для виконавця «чужа». SQL ДО створення фабрики: знімок метаданих прогрівається при старті.
        var s = await ArrangeAsync().ConfigureAwait(true);
        await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync().ConfigureAwait(true);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE cfg.ColumnDef SET IsMonthColumn = 1, MonthNumber = 5 WHERE Id = @id";
            command.Parameters.AddWithValue("@id", s.Document.ColumnDefIds[1]);
            await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var owner = await SignInAsync(app, s.Manager).ConfigureAwait(true);
        using var writer = await SignInAsync(app, s.Writer).ConfigureAwait(true);
        using var exec = await SignInAsync(app, s.Executor).ConfigureAwait(true);

        // Передача лише на місяць, якого в колонки комірки немає: для виконавця комірка «чужа».
        var created = await owner.PostAsJsonAsync(
            ListUri(s),
            new { slice = new { sheetCode = s.SheetCode, monthNumber = 5 }, recipientUserIds = new[] { s.ExecutorId }, isExclusive = true })
            .ConfigureAwait(true);
        await ReadAsync(created, HttpStatusCode.OK, app).ConfigureAwait(true);

        var foreign = await PatchAsync(exec, s, "#FFFF00").ConfigureAwait(true);
        var foreignBody = await foreign.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(foreign.StatusCode == HttpStatusCode.Forbidden, $"{foreign.StatusCode}: {foreignBody}\n{app.ErrorsText}");
        Assert.False(await HasStyleAsync(s).ConfigureAwait(true));

        // Ексклюзивна передача на ВЕСЬ аркуш: решта за ролями (писар) писати не може.
        var whole = await owner.PostAsJsonAsync(
            ListUri(s),
            new { slice = new { sheetCode = s.SheetCode }, recipientUserIds = new[] { s.ExecutorId }, isExclusive = true })
            .ConfigureAwait(true);
        await ReadAsync(whole, HttpStatusCode.OK, app).ConfigureAwait(true);

        var locked = await PatchAsync(writer, s, "#FFFF00").ConfigureAwait(true);
        var lockedBody = await locked.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(locked.StatusCode == HttpStatusCode.Forbidden, $"{locked.StatusCode}: {lockedBody}\n{app.ErrorsText}");
        Assert.Contains("LockedByOwner", lockedBody, StringComparison.Ordinal);
        Assert.False(await HasStyleAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Рівно_2000_комірок_одним_запитом_проходить_а_2001_відхиляється_стелею()
    {
        const int count = 2000;
        var s = await ArrangeAsync(rowCount: count).ConfigureAwait(true);

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var rowKeys = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == PeriodKeyValue && r.TableInstanceId == s.Document.TableInstanceId)
            .OrderBy(r => r.Id).Select(r => r.RowKeyValue).ToListAsync().ConfigureAwait(true);
        Assert.True(rowKeys.Count >= count, $"Рядків {rowKeys.Count}, потрібно {count}.");

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.Writer).ConfigureAwait(true);

        var items = rowKeys.Take(count)
            .Select(k => new { rowKey = k, columnCode = s.ColumnCode, backgroundHex = "#FFFF00" })
            .ToArray();

        var watch = Stopwatch.StartNew();
        var response = await client.PatchAsJsonAsync(
            StylesUri(s), new { periodKey = PeriodKeyValue, items, reset = false }).ConfigureAwait(true);
        watch.Stop();
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Contains($"\"appliedCells\":{count}", body, StringComparison.Ordinal);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"CELLSTYLE-2000 patchMs={watch.ElapsedMilliseconds}"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"PATCH 2000 комірок: {watch.Elapsed}");

        var stored = await db.CellStyles.AsNoTracking().CountAsync(
            c => c.PeriodKeyValue == PeriodKeyValue && c.ColumnDefId == s.Document.ColumnDefIds[0]
                 && db.TableRows.Any(r => r.PeriodKeyValue == PeriodKeyValue && r.Id == c.TableRowId
                                          && r.TableInstanceId == s.Document.TableInstanceId)).ConfigureAwait(true);
        Assert.Equal(count, stored);

        // 2001 -> 422 стеля, нічого не змінюється понад наявне.
        var tooMany = rowKeys.Take(count).Append("EXTRA")
            .Select(k => new { rowKey = k, columnCode = s.ColumnCode, backgroundHex = "#FF0000" }).ToArray();
        var refused = await client.PatchAsJsonAsync(
            StylesUri(s), new { periodKey = PeriodKeyValue, items = tooMany, reset = false }).ConfigureAwait(true);
        var refusedBody = await refused.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(refused.StatusCode == HttpStatusCode.UnprocessableEntity, $"{refused.StatusCode}: {refusedBody}");
        Assert.Contains("tooManyItems", refusedBody, StringComparison.Ordinal);
    }

    private static Uri StylesUri(Scenario s)
        => new($"/api/v1/documents/{s.Document.DocumentId}/tables/{s.Document.TableInstanceId}/cell-styles", UriKind.Relative);

    private static Uri SliceUri(Scenario s)
        => new($"/api/v1/documents/{s.Document.DocumentId}/tables/{s.Document.TableInstanceId}", UriKind.Relative);

    private static Uri ListUri(Scenario s)
        => new($"/api/v1/documents/{s.Document.DocumentId}/edit-assignments", UriKind.Relative);

    private static Uri ActionUri(Scenario s, long id, string action)
        => new($"/api/v1/documents/{s.Document.DocumentId}/edit-assignments/{id}/{action}", UriKind.Relative);

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Scenario s, string background)
        => client.PatchAsJsonAsync(
            StylesUri(s),
            new
            {
                periodKey = PeriodKeyValue,
                items = new[] { new { rowKey = s.RowKey, columnCode = s.ColumnCode, backgroundHex = background } },
                reset = false,
            });

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode status, EcrApiFactory app)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private async Task<bool> HasStyleAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.CellStyles.AsNoTracking()
            .AnyAsync(c => c.PeriodKeyValue == PeriodKeyValue && c.TableRowId == s.RowId).ConfigureAwait(false);
    }

    private async Task ClearStylesAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        await db.CellStyles.Where(c => c.PeriodKeyValue == PeriodKeyValue && c.TableRowId == s.RowId)
            .ExecuteDeleteAsync().ConfigureAwait(false);
    }

    private async Task<Scenario> ArrangeAsync(
        DocumentStatus? sheetStatus = null, bool reopen = false, bool archive = false, int rowCount = 2)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, columnCount: 2, rowCount: rowCount).ConfigureAwait(false);
        await using var db = builder.CreateContext();
        var at = new DateTime(2026, 12, 5, 9, 0, 0, DateTimeKind.Utc);

        async Task<(int Id, string UserName)> NewUserAsync(string prefix, GrantLevel level)
        {
            var userName = $"{prefix}_{Guid.NewGuid():N}"[..20];
            var user = new User(userName, userName, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            var role = new Role(
                EcrCode.Create($"CSD_{Guid.NewGuid():N}"[..24]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "CSD" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, level));
            await db.SaveChangesAsync().ConfigureAwait(false);
            return (user.Id, userName);
        }

        var manager = await NewUserAsync("csm", GrantLevel.Manage).ConfigureAwait(false);
        var writer = await NewUserAsync("csw", GrantLevel.Write).ConfigureAwait(false);
        var executor = await NewUserAsync("cse", GrantLevel.Read).ConfigureAwait(false);

        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == PeriodKeyValue).ConfigureAwait(false);
        period.AdvanceTo(PeriodState.Open, at);

        if (sheetStatus is { } status)
        {
            var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, PeriodKeyValue);
            state.Submit(manager.Id, at);
            if (status == DocumentStatus.Approved)
            {
                state.Approve(manager.Id, at.AddMinutes(1));
            }

            if (reopen)
            {
                state.Reopen(manager.Id, "Fix", at.AddMinutes(2));
            }

            db.ApprovalStates.Add(state);
        }

        if (archive)
        {
            var project = await db.Projects.Include(p => p.Periods)
                .FirstAsync(p => p.Id == doc.ProjectId).ConfigureAwait(false);
            project.Activate(at);
            foreach (var p in project.Periods)
            {
                p.AdvanceTo(PeriodState.Closed, at);
            }

            project.Archive(at);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        var rowKey = await db.TableRows.AsNoTracking()
            .Where(r => r.Id == doc.RowIds[0]).Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(false);
        var columnCode = await db.ColumnDefs.AsNoTracking()
            .Where(c => c.Id == doc.ColumnDefIds[0]).Select(c => c.Code).SingleAsync().ConfigureAwait(false);

        return new Scenario(
            doc, manager.UserName, writer.UserName, executor.UserName, executor.Id, doc.SheetCode, rowKey,
            doc.RowIds[0], columnCode);
    }

    private sealed record Scenario(
        TestDocument Document, string Manager, string Writer, string Executor, int ExecutorId, string SheetCode,
        string RowKey, long RowId, string ColumnCode);
}
