// tests/Ecr.Api.Tests/Security/DenyReadTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Workflow;
using Ecr.Domain.Entities.Documents;
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
/// S6 (enterprise-аудит безпеки, 2026-09-28): заборона на аркуш, таблицю й
/// колонку діє на ЧИТАННЯ (ФВ-6.6 — «<c>IsDeny</c> виграє завжди, на будь-якому
/// рівні»). Наскрізно: справжній SQL, справжній вхід, HTTP.
/// </summary>
/// <remarks>
/// ⛔ До S6 читання дивилося лише на проєкт: користувач із <c>Read</c> на проєкт
/// і <c>Deny</c> на чутливу таблицю читав її числа через
/// <c>GET /documents/{d}/tables/{ti}</c> і порівняння версій — заборона лише
/// сірила редагування.
///
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: <c>EditRules.CanRead</c> повертає <c>true</c> — червоніють
/// усі тести з <c>reader</c>, регресійні (<c>plain</c>) лишаються зеленими.
/// </remarks>
[Collection("SqlServer")]
public sealed class DenyReadTests(SqlServerFixture sql)
{
    private const string Password = "Api-Deny-Read-2026!";

    // Значення, які не можуть випадково збігтися ні з чим у відповіді.
    private const decimal Visible = 111111.5m;
    private const decimal DeniedColumn = 333333.5m;
    private const decimal DeniedTable = 777777.5m;
    private const decimal DeniedSheet = 888888.5m;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Заборонена_колонка_відсутня_у_зрізі_разом_зі_значеннями_решта_на_місці()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.Reader).ConfigureAwait(true);

        var (status, body) = await GetAsync(client, Slice(s, s.Doc.TableInstanceId)).ConfigureAwait(true);
        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");

        var slice = JsonDocument.Parse(body).RootElement;
        var codes = slice.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("code").GetString()).ToList();

        Assert.Equal([s.ColumnCodes[0], s.ColumnCodes[1]], codes);
        Assert.DoesNotContain(s.ColumnCodes[2], body, StringComparison.Ordinal);
        Assert.DoesNotContain(Digits(DeniedColumn), body, StringComparison.Ordinal);
        Assert.Contains(Digits(Visible), body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Заборонена_таблиця_і_таблиця_забороненого_аркуша_виглядають_як_неіснуючі()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.Reader).ConfigureAwait(true);

        const long missing = 999_999_999L;
        var (_, absent) = await GetAsync(client, Slice(s, missing)).ConfigureAwait(true);

        foreach (var (instance, secret) in new[]
                 {
                     (s.DeniedTable.TableInstanceId, DeniedTable),
                     (s.DeniedSheetTable.TableInstanceId, DeniedSheet),
                 })
        {
            var (status, body) = await GetAsync(client, Slice(s, instance)).ConfigureAwait(true);

            Assert.True(status == HttpStatusCode.NotFound, $"{status}: {body}\n{app.ErrorsText}");
            Assert.DoesNotContain(Digits(secret), body, StringComparison.Ordinal);

            var problem = JsonDocument.Parse(body).RootElement;
            Assert.Equal("ECR-DOC-0404", problem.GetProperty("errorCode").GetString());
            Assert.Equal("err.ECR-DOC-0404.tableInstance", problem.GetProperty("messageKey").GetString());

            // ⚠ Речення відрізняється від неіснуючого ЛИШЕ ідентифікатором (B-08).
            Assert.Equal(
                Detail(absent).Replace(missing.ToString(CultureInfo.InvariantCulture), "{id}", StringComparison.Ordinal),
                Detail(body).Replace(instance.ToString(CultureInfo.InvariantCulture), "{id}", StringComparison.Ordinal));
        }

        // Інша таблиця того самого документа — на місці.
        var (ok, _) = await GetAsync(client, Slice(s, s.Doc.TableInstanceId)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, ok);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_заборон_видно_все_регресія()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.Plain).ConfigureAwait(true);

        var (status, body) = await GetAsync(client, Slice(s, s.Doc.TableInstanceId)).ConfigureAwait(true);
        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");
        Assert.Contains(s.ColumnCodes[2], body, StringComparison.Ordinal);
        Assert.Contains(Digits(DeniedColumn), body, StringComparison.Ordinal);

        foreach (var (instance, value) in new[]
                 {
                     (s.DeniedTable.TableInstanceId, DeniedTable),
                     (s.DeniedSheetTable.TableInstanceId, DeniedSheet),
                 })
        {
            var (code, text) = await GetAsync(client, Slice(s, instance)).ConfigureAwait(true);
            Assert.True(code == HttpStatusCode.OK, $"{code}: {text}\n{app.ErrorsText}");
            Assert.Contains(Digits(value), text, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Порівняння_версій_не_показує_заборонених_таблиць_колонок_і_рядків()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var v1 = await SnapshotCurrentAsync(s).ConfigureAwait(true);

        // Змінюється КОЖНЕ значення, а рядок забороненої таблиці видаляється.
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            await SetAsync(db, s.Doc.PeriodKey, s.Doc.RowIds[0], s.Doc.ColumnDefIds[1], s.Doc.TableDefId, Visible + 1).ConfigureAwait(true);
            await SetAsync(db, s.Doc.PeriodKey, s.Doc.RowIds[0], s.Doc.ColumnDefIds[2], s.Doc.TableDefId, DeniedColumn + 1).ConfigureAwait(true);
            await SetAsync(db, s.Doc.PeriodKey, s.DeniedTable.RowIds[0], s.DeniedTable.ColumnDefIds[0], s.DeniedTable.TableDefId, DeniedTable + 1).ConfigureAwait(true);
            await SetAsync(db, s.Doc.PeriodKey, s.DeniedSheetTable.RowIds[0], s.DeniedSheetTable.ColumnDefIds[0], s.DeniedSheetTable.TableDefId, DeniedSheet + 1).ConfigureAwait(true);

            var removed = await db.TableRows.SingleAsync(r => r.Id == s.DeniedTable.RowIds[1]).ConfigureAwait(true);
            removed.SoftDelete(DateTime.UtcNow);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);

        using (var reader = await SignedInAsync(app, s.Reader).ConfigureAwait(true))
        {
            var (status, body) = await GetAsync(reader, Compare(s, v1)).ConfigureAwait(true);
            Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");

            var diff = JsonDocument.Parse(body).RootElement;
            var change = Assert.Single(diff.GetProperty("changes").EnumerateArray());
            Assert.Equal(s.ColumnCodes[1], change.GetProperty("columnCode").GetString());
            Assert.Empty(diff.GetProperty("removedRows").EnumerateArray());
            Assert.Empty(diff.GetProperty("addedRows").EnumerateArray());

            foreach (var secret in new[]
                     {
                         Digits(DeniedColumn), Digits(DeniedTable), Digits(DeniedSheet),
                         s.ColumnCodes[2], s.DeniedTable.TableCode, s.DeniedTable.RowKeys[1],
                     })
            {
                Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
            }
        }

        // Регресія: без заборон видно всі чотири зміни й видалений рядок.
        using (var plain = await SignedInAsync(app, s.Plain).ConfigureAwait(true))
        {
            var (status, body) = await GetAsync(plain, Compare(s, v1)).ConfigureAwait(true);
            Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");

            var diff = JsonDocument.Parse(body).RootElement;
            Assert.Equal(4, diff.GetProperty("changes").GetArrayLength());
            var gone = Assert.Single(diff.GetProperty("removedRows").EnumerateArray());
            Assert.Equal(s.DeniedTable.RowIds[1], gone.GetProperty("rowId").GetInt64());
        }
    }

    /// <summary>
    /// Вивантаження xlsx, csv і json — справжня задача в черзі, справжній файл.
    /// </summary>
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: у <c>ExportDocumentHandler</c> не класти межі в
    /// завдання — червоніють рядки <c>reader</c> усіх трьох форматів.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    [InlineData("xlsx")]
    [InlineData("csv")]
    [InlineData("json")]
    public async Task Експорт_не_містить_заборонених_таблиць_і_колонок(string format)
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        string[] secrets =
        [
            Digits(DeniedColumn), Digits(DeniedTable), Digits(DeniedSheet),
            s.ColumnCodes[2], s.DeniedTable.TableCode, s.DeniedSheetTable.TableCode,
        ];

        using var app = new EcrApiFactory(sql);

        using (var reader = await SignedInAsync(app, s.Reader).ConfigureAwait(true))
        {
            var text = await ExportTextAsync(app, reader, s, format).ConfigureAwait(true);

            Assert.Contains(Digits(Visible), text, StringComparison.Ordinal);
            Assert.Contains(s.ColumnCodes[1], text, StringComparison.Ordinal);
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            }
        }

        // Регресія: без заборон у файлі все.
        using (var plain = await SignedInAsync(app, s.Plain).ConfigureAwait(true))
        {
            var text = await ExportTextAsync(app, plain, s, format).ConfigureAwait(true);
            foreach (var secret in secrets)
            {
                Assert.Contains(secret, text, StringComparison.Ordinal);
            }
        }
    }

    // ────────────────────────────── збірка ────────────────────────────

    /// <summary>
    /// Вивантажує документ і повертає ВЕСЬ текст файлу: для zip (xlsx, csv) —
    /// склеєний вміст кожного запису, тобто й прихованого аркуша карти книги.
    /// </summary>
    private static async Task<string> ExportTextAsync(EcrApiFactory app, HttpClient client, Scenario s, string format)
    {
        var start = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/export", UriKind.Relative),
            new { includeFormulas = false, includeStyles = false, language = "en", periodKey = s.Doc.PeriodKey.Value, format })
            .ConfigureAwait(false);
        Assert.True(start.StatusCode == HttpStatusCode.Accepted, $"експорт: {start.StatusCode}: {app.ErrorsText}");
        var jobId = (await start.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false))
            .GetProperty("jobId").GetString()!;

        JsonElement job = default;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            job = await client.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/v1/jobs/{Uri.EscapeDataString(jobId)}", UriKind.Relative)).ConfigureAwait(false);
            if (job.GetProperty("state").GetString() is not ("Queued" or "Running"))
            {
                break;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        Assert.True(job.GetProperty("state").GetString() == "Succeeded", $"задача: {job.GetRawText()}\n{app.ErrorsText}");

        var download = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/export/{job.GetProperty("message").GetString()}", UriKind.Relative))
            .ConfigureAwait(false);
        Assert.True(download.StatusCode == HttpStatusCode.OK, $"файл: {download.StatusCode}: {app.ErrorsText}");
        var bytes = await download.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

        if (format == "json")
        {
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        var all = new System.Text.StringBuilder();
        foreach (var entry in zip.Entries)
        {
            using var stream = new StreamReader(entry.Open());
            all.Append(entry.FullName).Append('\n').Append(await stream.ReadToEndAsync().ConfigureAwait(false));
        }

        return all.ToString();
    }

    private static string Slice(Scenario s, long instance)
        => $"/api/v1/documents/{s.Doc.DocumentId}/tables/{instance}";

    private static string Compare(Scenario s, long from)
        => $"/api/v1/documents/{s.Doc.DocumentId}/compare?from={from}&to=current";

    /// <summary>Цифри значення без дробової частини — так воно видно в будь-якому поданні JSON.</summary>
    private static string Digits(decimal value)
        => decimal.Truncate(value).ToString(CultureInfo.InvariantCulture);

    private static string Detail(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("detail").GetString()!;

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
        return (response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
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

    private static async Task SetAsync(
        Ecr.Infrastructure.Persistence.EcrDbContext db, PeriodKey period, long rowId, int columnId, int tableDefId,
        decimal value)
    {
        var data = new CellValueData { ValueNumeric = value };
        var cell = await db.CellValues
            .SingleOrDefaultAsync(c => c.PeriodKeyValue == period.Value && c.TableRowId == rowId && c.ColumnDefId == columnId)
            .ConfigureAwait(false);

        if (cell is null)
        {
            db.CellValues.Add(new CellValue(new CellAddress(period, rowId, columnId), tableDefId, data));
        }
        else
        {
            cell.Apply(data);
        }
    }

    /// <summary>Зріз поточних комірок УСІХ таблиць документа — формат подання.</summary>
    private async Task<long> SnapshotCurrentAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var instances = await db.TableInstances
            .Where(t => t.DocumentId == s.Doc.DocumentId)
            .Select(t => t.Id).ToListAsync().ConfigureAwait(false);
        var rows = await db.TableRows
            .Where(r => instances.Contains(r.TableInstanceId) && !r.IsDeleted)
            .Select(r => r.Id).ToListAsync().ConfigureAwait(false);
        var cells = await db.CellValues
            .Where(c => rows.Contains(c.TableRowId)).ToListAsync().ConfigureAwait(false);

        var payload = SubmissionPayload.Write(cells.Select(c => new CellRecord(
            new CellAddress(s.Doc.PeriodKey, c.TableRowId, c.ColumnDefId),
            c.TableDefId,
            new CellValueData { ValueNumeric = c.ValueNumeric, ValueString = c.ValueString })));

        var snapshot = new SubmissionSnapshot(
            s.Doc.DocumentId, s.Doc.SheetDefId, s.Doc.PeriodKey.Value, s.Doc.TemplateVersionId,
            "[]", null, null, payload, new byte[32], DateTime.UtcNow, s.ReaderId);
        db.SubmissionSnapshots.Add(snapshot);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return snapshot.Id;
    }

    /// <summary>
    /// Документ на три таблиці (основна, ще одна на новому аркуші, третя на
    /// третьому аркуші) і два користувачі з <c>Read</c> на проєкт: <c>reader</c>
    /// із забороною на колонку C3 основної таблиці, на другу таблицю і на
    /// аркуш третьої, <c>plain</c> — без заборон.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, rowCount: 2).ConfigureAwait(false);
        var extra = await MultiTableDocument.AddTablesAsync(builder, doc, [1, 1]).ConfigureAwait(false);
        var deniedTable = extra[0];
        var deniedSheetTable = extra[1];

        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));

        await SetAsync(db, doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1], doc.TableDefId, Visible).ConfigureAwait(false);
        await SetAsync(db, doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[2], doc.TableDefId, DeniedColumn).ConfigureAwait(false);
        await SetAsync(db, doc.PeriodKey, deniedTable.RowIds[0], deniedTable.ColumnDefIds[0], deniedTable.TableDefId, DeniedTable).ConfigureAwait(false);
        await SetAsync(db, doc.PeriodKey, deniedTable.RowIds[1], deniedTable.ColumnDefIds[0], deniedTable.TableDefId, 5m).ConfigureAwait(false);
        await SetAsync(db, doc.PeriodKey, deniedSheetTable.RowIds[0], deniedSheetTable.ColumnDefIds[0], deniedSheetTable.TableDefId, DeniedSheet).ConfigureAwait(false);

        var reader = NewUser("dnr");
        var plain = NewUser("dnp");
        db.Users.AddRange(reader, plain);

        var viewer = NewRole("DNR_V");
        var denier = NewRole("DNR_D");
        db.Roles.AddRange(viewer, denier);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(viewer.Id, "Document.View"));
        db.RolePermissions.Add(new RolePermission(viewer.Id, "Document.Export"));
        db.ResourceGrants.Add(new ResourceGrant(viewer.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Read));
        db.RoleAssignments.Add(new RoleAssignment(viewer.Id, reader.Id, null));
        db.RoleAssignments.Add(new RoleAssignment(viewer.Id, plain.Id, null));

        db.RoleAssignments.Add(new RoleAssignment(denier.Id, reader.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(denier.Id, ResourceKind.Column, doc.ColumnDefIds[2], GrantLevel.Read, isDeny: true));
        db.ResourceGrants.Add(new ResourceGrant(denier.Id, ResourceKind.Table, deniedTable.TableDefId, GrantLevel.Read, isDeny: true));
        db.ResourceGrants.Add(new ResourceGrant(denier.Id, ResourceKind.Sheet, deniedSheetTable.SheetDefId, GrantLevel.Read, isDeny: true));

        await db.SaveChangesAsync().ConfigureAwait(false);

        var codes = await db.ColumnDefs.AsNoTracking()
            .Where(c => doc.ColumnDefIds.Contains(c.Id))
            .OrderBy(c => c.Ordinal)
            .Select(c => c.Code)
            .ToListAsync().ConfigureAwait(false);

        return new Scenario(doc, deniedTable, deniedSheetTable, codes, reader.UserName, reader.Id, plain.UserName);
    }

    private static User NewUser(string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        return user;
    }

    private static Role NewRole(string prefix)
        => new(
            EcrCode.Create($"{prefix}_{Guid.NewGuid():N}"[..24]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = prefix }));

    private sealed record Scenario(
        TestDocument Doc,
        ExtraTable DeniedTable,
        ExtraTable DeniedSheetTable,
        IReadOnlyList<string> ColumnCodes,
        string Reader,
        int ReaderId,
        string Plain);
}
