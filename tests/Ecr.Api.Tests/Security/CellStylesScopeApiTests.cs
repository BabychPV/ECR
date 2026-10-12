// tests/Ecr.Api.Tests/Security/CellStylesScopeApiTests.cs
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
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// CELL-STYLE «область»: <c>scope</c> у <c>PATCH …/cell-styles</c> на справжній СУБД через HTTP — фарбування
/// «рядки × колонки» одним запитом: розгортання, часткова відмова (передача зрізу), нерозкриття схованої
/// колонки, стан аркуша й період, стеля, одна подія аудиту, ідемпотентність, час.
/// </summary>
[Collection("SqlServer")]
public sealed class CellStylesScopeApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-CellStyle-Scope-2026!";

    /// <summary>⚠ Не 2026-05…07 (архівує <c>ArchiveJobTests</c>) і не 202609/202611/202612 (інші класи набору).</summary>
    private const int PeriodKeyValue = 202610;

    private static readonly string[] MissingColumn = ["NoSuchColumn"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Уся_таблиця_одним_запитом_повтор_ідемпотентний_reset_знімає_і_подія_одна_на_запит()
    {
        // Land-подібна таблиця: 1000 рядків × 12 колонок = 12 000 комірок одним PATCH.
        var s = await ArrangeAsync(rowCount: 1000, columnCount: 12).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.Writer).ConfigureAwait(true);

        var watch = Stopwatch.StartNew();
        var paint = await PatchAsync(client, s, new
        {
            periodKey = PeriodKeyValue,
            scope = new { allRows = true, allColumns = true, backgroundHex = "#ffe699" },
        }).ConfigureAwait(true);
        watch.Stop();
        var body = await paint.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(paint.StatusCode == HttpStatusCode.OK, $"{paint.StatusCode}: {body}\n{app.ErrorsText}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"CELLSTYLE-SCOPE 1000x12 paintMs={watch.ElapsedMilliseconds}"));

        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal(12000, json.GetProperty("appliedCells").GetInt32());
        Assert.Equal(0, json.GetProperty("deniedCells").GetInt32());
        Assert.Equal(12000, await StyleCountAsync(s).ConfigureAwait(true));
        Assert.Equal(1, await EventCountAsync(s, "CellStyleSet").ConfigureAwait(true));

        // Ідемпотентність: повтор — нічого не змінює й не пише в журнал.
        var again = await PatchAsync(client, s, new
        {
            periodKey = PeriodKeyValue,
            scope = new { allRows = true, allColumns = true, backgroundHex = "#FFE699" },
        }).ConfigureAwait(true);
        var againJson = JsonDocument.Parse(await again.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal(0, againJson.GetProperty("appliedCells").GetInt32());
        Assert.Equal(12000, againJson.GetProperty("skippedCells").GetInt32());
        Assert.Equal(1, await EventCountAsync(s, "CellStyleSet").ConfigureAwait(true));

        // Зняти все одним запитом.
        watch.Restart();
        var reset = await PatchAsync(client, s, new
        {
            periodKey = PeriodKeyValue,
            scope = new { allRows = true, allColumns = true, reset = true },
        }).ConfigureAwait(true);
        watch.Stop();
        var resetJson = JsonDocument.Parse(await reset.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"CELLSTYLE-SCOPE 1000x12 resetMs={watch.ElapsedMilliseconds}"));
        Assert.Equal(12000, resetJson.GetProperty("resetCells").GetInt32());
        Assert.Equal(0, await StyleCountAsync(s).ConfigureAwait(true));
        Assert.Equal(1, await EventCountAsync(s, "CellStyleReset").ConfigureAwait(true));

        // Подія без переліку комірок (а не 12 000 записів).
        var details = await LastEventDetailsAsync(s, "CellStyleSet").ConfigureAwait(true);
        Assert.True(details.Length < 2000, $"Подія надто велика: {details.Length} символів.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Підмножини_рядків_і_колонок_фарбують_лише_перетин_а_items_сильніші()
    {
        var s = await ArrangeAsync(rowCount: 5, columnCount: 3).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.Writer).ConfigureAwait(true);

        var response = await PatchAsync(client, s, new
        {
            periodKey = PeriodKeyValue,
            items = new[] { new { rowKey = s.RowKeys[0], columnCode = s.ColumnCodes[1], backgroundHex = "#00FF00" } },
            scope = new
            {
                rowKeys = new[] { s.RowKeys[0], s.RowKeys[2] },
                columnCodes = new[] { s.ColumnCodes[1] },
                backgroundHex = "#FF0000",
            },
        }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Contains("\"appliedCells\":2", body, StringComparison.Ordinal);

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var styles = await db.CellStyles.AsNoTracking()
            .Where(c => c.PeriodKeyValue == PeriodKeyValue && s.Document.RowIds.Contains(c.TableRowId))
            .ToListAsync().ConfigureAwait(true);
        Assert.Equal(2, styles.Count);
        Assert.All(styles, c => Assert.Equal(s.Document.ColumnDefIds[1], c.ColumnDefId));
        Assert.Equal("#00FF00", styles.Single(c => c.TableRowId == s.Document.RowIds[0]).BackgroundHex);
        Assert.Equal("#FF0000", styles.Single(c => c.TableRowId == s.Document.RowIds[2]).BackgroundHex);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Схована_колонка_не_входить_в_allColumns_і_явний_код_відмовляє_як_неіснуючий()
    {
        var s = await ArrangeAsync(rowCount: 4, columnCount: 3, hideColumn: 2).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.Writer).ConfigureAwait(true);
        var hiddenCode = s.ColumnCodes[2];

        var all = await PatchAsync(client, s, new
        {
            periodKey = PeriodKeyValue,
            scope = new { allRows = true, allColumns = true, backgroundHex = "#FFFF00" },
        }).ConfigureAwait(true);
        var allBody = await all.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(all.StatusCode == HttpStatusCode.OK, $"{all.StatusCode}: {allBody}\n{app.ErrorsText}");

        // 4 рядки × 2 видимі колонки; відповідь не називає схованої колонки.
        Assert.Contains("\"appliedCells\":8", allBody, StringComparison.Ordinal);
        Assert.DoesNotContain(hiddenCode, allBody, StringComparison.Ordinal);
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            Assert.False(await db.CellStyles.AsNoTracking().AnyAsync(
                c => c.PeriodKeyValue == PeriodKeyValue && c.ColumnDefId == s.Document.ColumnDefIds[2]).ConfigureAwait(true));
        }

        var explicitHidden = await PatchAsync(client, s, new
        {
            periodKey = PeriodKeyValue,
            scope = new { allRows = true, columnCodes = new[] { hiddenCode }, backgroundHex = "#FF0000" },
        }).ConfigureAwait(true);
        var explicitBody = await explicitHidden.Content.ReadAsStringAsync().ConfigureAwait(true);
        var missing = await PatchAsync(client, s, new
        {
            periodKey = PeriodKeyValue,
            scope = new { allRows = true, columnCodes = MissingColumn, backgroundHex = "#FF0000" },
        }).ConfigureAwait(true);
        var missingBody = await missing.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, explicitHidden.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Contains("unknownCell", explicitBody, StringComparison.Ordinal);
        Assert.DoesNotContain(hiddenCode, explicitBody, StringComparison.Ordinal);
        Assert.Contains("unknownCell", missingBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DocumentStatus.Submitted, "DocumentSubmitted")]
    [InlineData(DocumentStatus.Approved, "DocumentApproved")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Поданий_чи_затверджений_аркуш_відмовляє_область_без_запису(DocumentStatus status, string reason)
    {
        var s = await ArrangeAsync(rowCount: 3, columnCount: 2, sheetStatus: status).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.Writer).ConfigureAwait(true);

        var response = await PatchAsync(client, s, new
        {
            periodKey = PeriodKeyValue,
            scope = new { allRows = true, allColumns = true, backgroundHex = "#FFFF00" },
        }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Contains(reason, body, StringComparison.Ordinal);
        Assert.Equal(0, await StyleCountAsync(s).ConfigureAwait(true));
        Assert.Equal(0, await EventCountAsync(s, "CellStyleSet").ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Закритий_період_відмовляє_область_без_запису()
    {
        var s = await ArrangeAsync(rowCount: 3, columnCount: 2, closePeriod: true).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.Writer).ConfigureAwait(true);

        var response = await PatchAsync(client, s, new
        {
            periodKey = PeriodKeyValue,
            scope = new { allRows = true, allColumns = true, backgroundHex = "#FFFF00" },
        }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal(0, await StyleCountAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Виконавець_передачі_фарбує_лише_свої_комірки_решта_у_deniedCells_а_Filled_відмовляє_без_запису()
    {
        // Друга колонка стає місячною (травень): передача — лише на цей місяць. SQL ДО фабрики (знімок метаданих).
        var s = await ArrangeAsync(rowCount: 4, columnCount: 3).ConfigureAwait(true);
        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync().ConfigureAwait(true);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE cfg.ColumnDef SET IsMonthColumn = 1, MonthNumber = 5 WHERE Id = @id";
            command.Parameters.AddWithValue("@id", s.Document.ColumnDefIds[1]);
            await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var owner = await SignInAsync(app, s.Manager).ConfigureAwait(true);
        using var exec = await SignInAsync(app, s.Executor).ConfigureAwait(true);

        var created = await owner.PostAsJsonAsync(
            ListUri(s),
            new { slice = new { sheetCode = s.SheetCode, monthNumber = 5 }, recipientUserIds = new[] { s.ExecutorId }, isExclusive = false })
            .ConfigureAwait(true);
        var createdBody = await created.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(created.StatusCode == HttpStatusCode.OK, $"{created.StatusCode}: {createdBody}\n{app.ErrorsText}");
        var assignmentId = JsonDocument.Parse(createdBody).RootElement.GetProperty("id").GetInt64();

        var response = await PatchAsync(exec, s, new
        {
            periodKey = PeriodKeyValue,
            scope = new { allRows = true, allColumns = true, backgroundHex = "#FFFF00" },
        }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal(4, json.GetProperty("appliedCells").GetInt32());       // 4 рядки × колонка місяця
        Assert.Equal(8, json.GetProperty("deniedCells").GetInt32());        // 4 рядки × 2 чужі колонки
        Assert.True(json.GetProperty("deniedByReason").EnumerateObject().Count() <= 10);
        Assert.Equal(4, await StyleCountAsync(s).ConfigureAwait(true));

        // «Заповнено»: виконавець більше не пише — 403, нових записів немає.
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            await db.CellStyles.Where(c => c.PeriodKeyValue == PeriodKeyValue && s.Document.RowIds.Contains(c.TableRowId))
                .ExecuteDeleteAsync().ConfigureAwait(true);
        }

        var fill = await exec.PostAsync(ActionUri(s, assignmentId, "fill"), null).ConfigureAwait(true);
        Assert.True(fill.IsSuccessStatusCode, await fill.Content.ReadAsStringAsync().ConfigureAwait(true));

        var filled = await PatchAsync(exec, s, new
        {
            periodKey = PeriodKeyValue,
            scope = new { allRows = true, allColumns = true, backgroundHex = "#00FF00" },
        }).ConfigureAwait(true);
        var filledBody = await filled.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(filled.StatusCode == HttpStatusCode.Forbidden, $"{filled.StatusCode}: {filledBody}");
        Assert.Contains("ECR-ACCS-0403", filledBody, StringComparison.Ordinal);
        Assert.Equal(0, await StyleCountAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Стеля_п_ятдесят_тисяч_комірок_проходить_а_понад_неї_422_tooManyCells_з_часом()
    {
        // 2000 рядків × 26 колонок: allColumns = 52 000 (понад стелю), 25 колонок = рівно 50 000.
        var s = await ArrangeAsync(rowCount: 2000, columnCount: 26).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.Writer).ConfigureAwait(true);

        var over = await PatchAsync(client, s, new
        {
            periodKey = PeriodKeyValue,
            scope = new { allRows = true, allColumns = true, backgroundHex = "#FFFF00" },
        }).ConfigureAwait(true);
        var overBody = await over.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(over.StatusCode == HttpStatusCode.UnprocessableEntity, $"{over.StatusCode}: {overBody}");
        Assert.Contains("tooManyCells", overBody, StringComparison.Ordinal);
        Assert.Equal(0, await StyleCountAsync(s).ConfigureAwait(true));

        var watch = Stopwatch.StartNew();
        var exact = await PatchAsync(client, s, new
        {
            periodKey = PeriodKeyValue,
            scope = new { allRows = true, columnCodes = s.ColumnCodes.Take(25).ToArray(), backgroundHex = "#FFFF00" },
        }).ConfigureAwait(true);
        watch.Stop();
        var exactBody = await exact.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(exact.StatusCode == HttpStatusCode.OK, $"{exact.StatusCode}: {exactBody}\n{app.ErrorsText}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"CELLSTYLE-SCOPE 2000x25=50000 patchMs={watch.ElapsedMilliseconds}"));
        Assert.Contains("\"appliedCells\":50000", exactBody, StringComparison.Ordinal);
        Assert.Equal(50000, await StyleCountAsync(s).ConfigureAwait(true));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"PATCH 50000 комірок: {watch.Elapsed}");
    }

    private static Uri StylesUri(Scenario s)
        => new($"/api/v1/documents/{s.Document.DocumentId}/tables/{s.Document.TableInstanceId}/cell-styles", UriKind.Relative);

    private static Uri ListUri(Scenario s)
        => new($"/api/v1/documents/{s.Document.DocumentId}/edit-assignments", UriKind.Relative);

    private static Uri ActionUri(Scenario s, long id, string action)
        => new($"/api/v1/documents/{s.Document.DocumentId}/edit-assignments/{id}/{action}", UriKind.Relative);

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Scenario s, object body)
        => client.PatchAsJsonAsync(StylesUri(s), body);

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private async Task<int> StyleCountAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.CellStyles.AsNoTracking().CountAsync(
            c => c.PeriodKeyValue == PeriodKeyValue
                 && db.TableRows.Any(r => r.PeriodKeyValue == PeriodKeyValue && r.Id == c.TableRowId
                                          && r.TableInstanceId == s.Document.TableInstanceId)).ConfigureAwait(false);
    }

    private async Task<int> EventCountAsync(Scenario s, string eventType)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM aud.SecurityEvent
             WHERE EventType = @type AND DetailsJson LIKE N'%"tableInstanceId":' + @id + N',%';
            """;
        command.Parameters.AddWithValue("@type", eventType);
        command.Parameters.AddWithValue("@id", s.Document.TableInstanceId.ToString(CultureInfo.InvariantCulture));
        return (int)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private async Task<string> LastEventDetailsAsync(Scenario s, string eventType)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) DetailsJson FROM aud.SecurityEvent
             WHERE EventType = @type AND DetailsJson LIKE N'%"tableInstanceId":' + @id + N',%'
             ORDER BY Id DESC;
            """;
        command.Parameters.AddWithValue("@type", eventType);
        command.Parameters.AddWithValue("@id", s.Document.TableInstanceId.ToString(CultureInfo.InvariantCulture));
        return (string?)await command.ExecuteScalarAsync().ConfigureAwait(false) ?? string.Empty;
    }

    private async Task<Scenario> ArrangeAsync(
        int rowCount, int columnCount, DocumentStatus? sheetStatus = null, bool closePeriod = false, int? hideColumn = null)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, columnCount: columnCount, rowCount: rowCount).ConfigureAwait(false);
        await using var db = builder.CreateContext();
        var at = new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);

        async Task<(int Id, string UserName)> NewUserAsync(string prefix, GrantLevel level)
        {
            var userName = $"{prefix}_{Guid.NewGuid():N}"[..20];
            var user = new User(userName, userName, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            var role = new Role(
                EcrCode.Create($"CSS_{Guid.NewGuid():N}"[..24]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "CSS" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, level));
            await db.SaveChangesAsync().ConfigureAwait(false);
            return (user.Id, userName);
        }

        var manager = await NewUserAsync("ssm", GrantLevel.Manage).ConfigureAwait(false);
        var writer = await NewUserAsync("ssw", GrantLevel.Write).ConfigureAwait(false);
        var executor = await NewUserAsync("sse", GrantLevel.Read).ConfigureAwait(false);

        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == PeriodKeyValue).ConfigureAwait(false);
        period.AdvanceTo(PeriodState.Open, at);
        if (closePeriod)
        {
            period.AdvanceTo(PeriodState.Closed, at.AddMinutes(5));
        }

        if (sheetStatus is { } status)
        {
            var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, PeriodKeyValue);
            state.Submit(manager.Id, at);
            if (status == DocumentStatus.Approved)
            {
                state.Approve(manager.Id, at.AddMinutes(1));
            }

            db.ApprovalStates.Add(state);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        if (hideColumn is { } index)
        {
            // SQL ДО створення фабрики: знімок метаданих прогрівається при старті.
            await db.ColumnDefs.Where(c => c.Id == doc.ColumnDefIds[index])
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.IsHidden, true)).ConfigureAwait(false);
        }

        var rowKeys = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == PeriodKeyValue && r.TableInstanceId == doc.TableInstanceId)
            .OrderBy(r => r.Id).Select(r => r.RowKeyValue).ToListAsync().ConfigureAwait(false);
        var columnCodes = new List<string>();
        foreach (var id in doc.ColumnDefIds)
        {
            columnCodes.Add(await db.ColumnDefs.AsNoTracking()
                .Where(c => c.Id == id).Select(c => c.Code).SingleAsync().ConfigureAwait(false));
        }

        return new Scenario(
            doc, manager.UserName, writer.UserName, executor.UserName, executor.Id, doc.SheetCode, rowKeys, columnCodes);
    }

    private sealed record Scenario(
        TestDocument Document, string Manager, string Writer, string Executor, int ExecutorId, string SheetCode,
        IReadOnlyList<string> RowKeys, IReadOnlyList<string> ColumnCodes);
}
