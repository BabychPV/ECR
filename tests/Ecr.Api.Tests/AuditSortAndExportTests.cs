using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Журнали аудиту: серверний порядок (типово найновіші першими), курсор прив'язаний до порядку, CSV-експорт усіх
/// вкладок (комірки, безпека, погодження): BOM, заголовок, порядок і фільтри, екранування, нейтралізація формул,
/// стеля, подія експорту без даних журналу, матриця прав.
/// </summary>
/// <remarks>
/// ⚠ База спільна: кожен тест працює у власному просторі значень (унікальний <c>origin</c> / тип події / документ).
/// ⛔ Мутаційні докази: прибрати `ORDER BY ... Id` у `AuditPaging.Order` — червоніє порядок за однакових моментів;
/// прибрати префікс `'` у `CsvFormat.Field` — червоніє перевірка формули; прибрати перевірку стелі в
/// `AuditExportRunner` — червоніє `Експорт_понад_стелю_*`; прибрати запис події — червоніє `Експорт_пише_подію_*`.
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditSortAndExportTests(SqlServerFixture sql)
{
    private const string Password = "Api-Audit-Sort-2026!";

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    private static readonly DateTime Base = DateTime.UtcNow.AddHours(-5);

    // ---------- сторінки: порядок і курсор ----------

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Журнал_комірок_типово_найновіші_першими_і_sort_змінює_порядок_з_курсором()
    {
        using var app = new EcrApiFactory(sql);
        var client = await LoginAsync(app, ["Security.ViewAudit"]);
        var origin = $"T{_tag}";

        // Три різні моменти і три з однаковим: обхід по 2 перетинає групу рівних моментів.
        int[] minutes = [0, 10, 10, 10, 20, 30];
        for (var i = 0; i < minutes.Length; i++)
        {
            await WriteCellAsync(origin, $"r{i}", Base.AddMinutes(minutes[i]), author: 1);
        }

        var desc = await WalkAsync(client, $"/api/v1/audit/cells?{Window()}&origin={origin}&limit=2", "rowKey");
        Assert.Equal(["r5", "r4", "r3", "r2", "r1", "r0"], desc);

        var asc = await WalkAsync(client, $"/api/v1/audit/cells?{Window()}&origin={origin}&limit=2&sort=ChangedAtAsc", "rowKey");
        Assert.Equal(["r0", "r1", "r2", "r3", "r4", "r5"], asc);

        // Явний типовий порядок = відсутність параметра.
        var explicitDesc = await WalkAsync(
            client, $"/api/v1/audit/cells?{Window()}&origin={origin}&limit=4&sort=ChangedAtDesc", "rowKey");
        Assert.Equal(desc, explicitDesc);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Курсор_чужого_порядку_та_невідомий_sort_відхиляються_чистою_4xx()
    {
        using var app = new EcrApiFactory(sql);
        var client = await LoginAsync(app, ["Security.ViewAudit"]);
        var origin = $"T{_tag}";
        for (var i = 0; i < 4; i++)
        {
            await WriteCellAsync(origin, $"r{i}", Base.AddMinutes(i), author: 1);
        }

        var first = await GetJsonAsync(client, $"/api/v1/audit/cells?{Window()}&origin={origin}&limit=2");
        var cursor = first.GetProperty("nextCursor").GetString()!;

        var mismatch = await client.GetAsync(new Uri(
            $"/api/v1/audit/cells?{Window()}&origin={origin}&limit=2&sort=UserAsc&cursor={Uri.EscapeDataString(cursor)}",
            UriKind.Relative));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
        Assert.Contains("err.ECR-REQ-0422.auditCursorSortMismatch", await mismatch.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var bogus = await client.GetAsync(new Uri($"/api/v1/audit/cells?{Window()}&sort=Sideways", UriKind.Relative));

        // Невідомий порядок — чиста 4xx (модельне зв'язування застосунку віддає 422), а не мовчазний типовий порядок.
        Assert.True((int)bogus.StatusCode is 400 or 422, $"{bogus.StatusCode}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Структура_і_безпека_типово_найновіші_першими_без_дублів_на_межах_сторінок()
    {
        using var app = new EcrApiFactory(sql);
        var client = await LoginAsync(app, ["Security.ViewAudit"]);

        var type = $"t.{_tag}.S";
        var eventType = $"Sort{_tag}";
        int[] minutes = [0, 5, 5, 5, 9];
        for (var i = 0; i < minutes.Length; i++)
        {
            await WriteStructureAsync(type, entityId: i + 1, Base.AddMinutes(minutes[i]), author: 1);
            await WriteSecurityAsync(eventType, targetUser: i + 1, Base.AddMinutes(minutes[i]), author: 1);
        }

        var structure = await WalkAsync(client, $"/api/v1/audit/structure?{Window()}&entityType={type}&limit=2", "entityId");
        Assert.Equal(["5", "4", "3", "2", "1"], structure);

        var structureAsc = await WalkAsync(
            client, $"/api/v1/audit/structure?{Window()}&entityType={type}&limit=2&sort=ChangedAtAsc", "entityId");
        Assert.Equal(["1", "2", "3", "4", "5"], structureAsc);

        var security = await WalkAsync(client, $"/api/v1/audit/security?{Window()}&eventType={eventType}&limit=2", "targetUserId");
        Assert.Equal(["5", "4", "3", "2", "1"], security);

        var securityAsc = await WalkAsync(
            client, $"/api/v1/audit/security?{Window()}&eventType={eventType}&limit=2&sort=ChangedAtAsc", "targetUserId");
        Assert.Equal(["1", "2", "3", "4", "5"], securityAsc);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Сортування_за_користувачем_іде_за_іменем_а_не_за_номером()
    {
        using var app = new EcrApiFactory(sql);
        var client = await LoginAsync(app, ["Security.ViewAudit"]);

        // Номер автора «Бета» менший за номер «Альфи»: порядок за іменем відрізняється від порядку за номером.
        var beta = await CreateUserAsync("Beta");
        var alpha = await CreateUserAsync("Alpha");
        var eventType = $"UserSort{_tag}";
        await WriteSecurityAsync(eventType, 1, Base, beta);
        await WriteSecurityAsync(eventType, 2, Base.AddMinutes(1), alpha);
        await WriteSecurityAsync(eventType, 3, Base.AddMinutes(2), beta);

        var asc = await WalkAsync(
            client, $"/api/v1/audit/security?{Window()}&eventType={eventType}&limit=2&sort=UserAsc", "targetUserId");
        Assert.Equal(["2", "1", "3"], asc);

        var desc = await WalkAsync(
            client, $"/api/v1/audit/security?{Window()}&eventType={eventType}&limit=2&sort=UserDesc", "targetUserId");
        Assert.Equal(["3", "1", "2"], desc);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Структура_підписує_сутність_кодом_а_видалену_лишає_без_підпису()
    {
        using var app = new EcrApiFactory(sql);
        var client = await LoginAsync(app, ["Security.ViewAudit"]);
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(ct: CancellationToken.None);

        var code = await ColumnCodeAsync(doc.ColumnDefIds[1]);
        var type = "ColumnDef";
        var author = Random.Shared.Next(1_000_000, int.MaxValue);
        await WriteStructureAsync(type, doc.ColumnDefIds[1], Base, author);
        await WriteStructureAsync(type, 2_000_000_011, Base.AddMinutes(1), author);

        var items = await ItemsAsync(client, $"/api/v1/audit/structure?{Window()}&changedByUserId={author}&sort=ChangedAtAsc");
        Assert.Equal(code, items[0].GetProperty("entityLabel").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("entityLabel").ValueKind);
    }

    // ---------- експорт ----------

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Експорт_комірок_BOM_заголовок_порядок_фільтр_екранування_і_подія_без_даних()
    {
        using var app = new EcrApiFactory(sql);
        var client = await LoginAsync(app, ["Security.ViewAudit"]);
        var origin = $"T{_tag}";

        await WriteCellAsync(origin, "r0", Base, author: 1, newValue: "перший, \"в лапках\"\r\nдругий рядок");
        await WriteCellAsync(origin, "r1", Base.AddMinutes(1), author: 1, newValue: "=HYPERLINK(\"x\")");
        await WriteCellAsync(origin, "r2", Base.AddMinutes(2), author: 1, newValue: "Кирилиця їжак");
        await WriteCellAsync($"X{_tag}", "other", Base.AddMinutes(3), author: 1);

        var (bytes, response) = await GetBytesAsync(client, $"/api/v1/audit/cells/export.csv?{Window()}&origin={origin}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Matches("^audit-cells-\\d{8}T\\d{6}Z-\\d{8}T\\d{6}Z\\.csv$", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);

        var rows = ParseCsv(Encoding.UTF8.GetString(bytes[3..]));
        Assert.Equal(
            "changedAtUtc,documentId,documentBusinessKey,periodKey,rowKey,columnDefId,columnCode,oldValue,newValue,changedByUserId,changedBy,origin,isLateEdit,isOutOfWindow,isOverwrite,fromArchive",
            string.Join(',', rows[0]));

        // Порядок типовий (найновіші першими); фільтр origin не пускає чужий рядок.
        var body = rows[1..];
        Assert.Equal(["r2", "r1", "r0"], body.Select(r => r[4]));

        // Екранування: коми, лапки, переноси й кирилиця повертаються без втрат; формула нейтралізована апострофом.
        Assert.Equal("перший, \"в лапках\"\r\nдругий рядок", body[2][8]);
        Assert.Equal("Кирилиця їжак", body[0][8]);
        Assert.Equal("'=HYPERLINK(\"x\")", body[1][8]);

        // Час — однозначний ISO 8601 UTC.
        Assert.All(body, r => Assert.Matches("^\\d{4}-\\d\\d-\\d\\dT\\d\\d:\\d\\d:\\d\\d\\.\\d{3}Z$", r[0]));

        // Подія експорту: вкладка, фільтр, порядок і кількість — але НЕ вміст журналу.
        var details = await ExportEventAsync("AuditCellsExported", origin);
        using var doc = JsonDocument.Parse(details);
        Assert.Equal("cells", doc.RootElement.GetProperty("tab").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("rows").GetInt32());
        Assert.Equal("ChangedAtDesc", doc.RootElement.GetProperty("sort").GetString());
        Assert.DoesNotContain("HYPERLINK", details, StringComparison.Ordinal);
        Assert.DoesNotContain("Кирилиця", details, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Експорт_дотримується_sort_у_обидва_боки()
    {
        using var app = new EcrApiFactory(sql);
        var client = await LoginAsync(app, ["Security.ViewAudit"]);
        var origin = $"T{_tag}";
        foreach (var (i, m) in new[] { (0, 0), (1, 5), (2, 5), (3, 9) })
        {
            await WriteCellAsync(origin, $"r{i}", Base.AddMinutes(m), author: 1);
        }

        var asc = ParseCsv(await GetCsvTextAsync(client, $"/api/v1/audit/cells/export.csv?{Window()}&origin={origin}&sort=ChangedAtAsc"));
        Assert.Equal(["r0", "r1", "r2", "r3"], asc[1..].Select(r => r[4]));

        var desc = ParseCsv(await GetCsvTextAsync(client, $"/api/v1/audit/cells/export.csv?{Window()}&origin={origin}&sort=ChangedAtDesc"));
        Assert.Equal(["r3", "r2", "r1", "r0"], desc[1..].Select(r => r[4]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Експорт_безпеки_і_погодження_віддає_CSV_з_подією_експорту()
    {
        using var app = new EcrApiFactory(sql);
        var client = await LoginAsync(app, ["Security.ViewAudit"]);

        var eventType = $"Exp{_tag}";
        await WriteSecurityAsync(eventType, 7, Base, author: 1, details: "{\"k\":\"=1+1\"}");
        await WriteSecurityAsync(eventType, 8, Base.AddMinutes(1), author: 1);

        var security = ParseCsv(await GetCsvTextAsync(client, $"/api/v1/audit/security/export.csv?{Window()}&eventType={eventType}"));
        Assert.Equal("changedAtUtc,eventType,targetUserId,targetRoleId,changedByUserId,changedBy,correlationId,detailsJson", string.Join(',', security[0]));
        Assert.Equal(["8", "7"], security[1..].Select(r => r[2]));
        Assert.Equal("{\"k\":\"=1+1\"}", security[2][7]);
        Assert.Equal(2, JsonDocument.Parse(await ExportEventAsync("AuditSecurityExported", eventType)).RootElement.GetProperty("rows").GetInt32());

        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(ct: CancellationToken.None);
        await WriteWorkflowAsync(doc, Base, "e0", "Нижче, ніж \"треба\"");
        await WriteWorkflowAsync(doc, Base.AddMinutes(1), "e1", "-1 заперечення");

        var url = $"/api/v1/audit/workflow/export.csv?{Window()}&documentId={doc.DocumentId}";
        var workflow = ParseCsv(await GetCsvTextAsync(client, url));
        Assert.Equal(
            "changedAtUtc,action,fromStatus,toStatus,comment,changedByUserId,changedBy,documentId,documentBusinessKey,sheetCode,periodKey,stepOrdinal",
            string.Join(',', workflow[0]));
        Assert.Equal(["'-1 заперечення", "Нижче, ніж \"треба\""], workflow[1..].Select(r => r[4]));
        Assert.All(workflow[1..], r => Assert.Equal("Reject", r[1]));

        var list = await ItemsAsync(client, $"/api/v1/audit/workflow?{Window()}&documentId={doc.DocumentId}");
        Assert.Equal(2, list.Count);
        Assert.Equal("-1 заперечення", list[0].GetProperty("comment").GetString());

        var sorted = await ItemsAsync(client, $"/api/v1/audit/workflow?{Window()}&documentId={doc.DocumentId}&sort=ChangedAtAsc");
        Assert.Equal("Нижче, ніж \"треба\"", sorted[0].GetProperty("comment").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Експорт_понад_стелю_відхиляється_422_без_файлу_на_кожній_вкладці()
    {
        using var app = new EcrApiFactory(sql);
        using var limited = app.WithWebHostBuilder(b => b.UseSetting("Audit:ExportMaxRows", "2"));
        var client = await LoginAsync(limited, ["Security.ViewAudit"]);

        var origin = $"T{_tag}";
        var eventType = $"Cap{_tag}";
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(ct: CancellationToken.None);
        for (var i = 0; i < 3; i++)
        {
            await WriteCellAsync(origin, $"r{i}", Base.AddMinutes(i), author: 1);
            await WriteSecurityAsync(eventType, i, Base.AddMinutes(i), author: 1);
            await WriteWorkflowAsync(doc, Base.AddMinutes(i), $"e{i}", "c");
        }

        foreach (var url in new[]
                 {
                     $"/api/v1/audit/cells/export.csv?{Window()}&origin={origin}",
                     $"/api/v1/audit/security/export.csv?{Window()}&eventType={eventType}",
                     $"/api/v1/audit/workflow/export.csv?{Window()}&documentId={doc.DocumentId}",
                 })
        {
            var response = await client.GetAsync(new Uri(url, UriKind.Relative));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("err.ECR-REQ-0422.auditExportTooLarge", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // Відмова не пише подію «експортовано»: файл не віддавався.
        Assert.Equal("{}", await ExportEventAsync("AuditCellsExported", origin));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Матриця_прав_переглядач_аудиту_бачить_усі_вкладки_і_експорти_решта_отримує_403()
    {
        using var app = new EcrApiFactory(sql);
        var viewer = await LoginAsync(app, ["Security.ViewAudit"]);

        var stranger = app.CreateClient();
        var other = await LoginAsync(app, ["Template.Edit"], suffix: "x");

        var urls = new[]
        {
            "/api/v1/audit/cells", "/api/v1/audit/structure", "/api/v1/audit/security", "/api/v1/audit/workflow",
            "/api/v1/audit/cells/export.csv", "/api/v1/audit/structure/export.csv",
            "/api/v1/audit/security/export.csv", "/api/v1/audit/workflow/export.csv",
        };

        foreach (var path in urls)
        {
            var allowed = await viewer.GetAsync(new Uri($"{path}?{Window()}&limit=1", UriKind.Relative));
            Assert.True(allowed.StatusCode == HttpStatusCode.OK, $"{path}: {allowed.StatusCode}");

            var denied = await other.GetAsync(new Uri($"{path}?{Window()}&limit=1", UriKind.Relative));
            Assert.True(denied.StatusCode == HttpStatusCode.Forbidden, $"{path}: {denied.StatusCode}");

            var anonymous = await stranger.GetAsync(new Uri($"{path}?{Window()}&limit=1", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
    }

    // ---------- допоміжне ----------

    private static string Window()
        => $"from={Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("O", CultureInfo.InvariantCulture))}"
           + $"&to={Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(1).ToString("O", CultureInfo.InvariantCulture))}";

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<List<JsonElement>> ItemsAsync(HttpClient client, string url)
        => (await GetJsonAsync(client, url)).GetProperty("items").EnumerateArray().ToList();

    /// <summary>Обхід усіх сторінок; повертає значення поля в порядку видачі.</summary>
    private static async Task<List<string>> WalkAsync(HttpClient client, string url, string field)
    {
        var values = new List<string>();
        string? cursor = null;
        var guard = 0;
        do
        {
            var page = await GetJsonAsync(client, url + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)));
            values.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty(field).ToString()));
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null && ++guard < 50);

        return values;
    }

    private static async Task<(byte[] Bytes, HttpResponseMessage Response)> GetBytesAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {response.StatusCode} {Encoding.UTF8.GetString(bytes)}");

        return (bytes, response);
    }

    private static async Task<string> GetCsvTextAsync(HttpClient client, string url)
    {
        var (bytes, _) = await GetBytesAsync(client, url);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);

        return Encoding.UTF8.GetString(bytes[3..]);
    }

    /// <summary>Мінімальний розбір RFC 4180: лапки, подвоєні лапки, коми й переноси в полях.</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
                i++;
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private async Task WriteCellAsync(string origin, string rowKey, DateTime at, int author, string newValue = "2")
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            VALUES (@at, 202603, 0, 0, @rowKey, 0, N'1', @newValue, @author, @origin, 0);
            """;
        command.Parameters.Add("@at", System.Data.SqlDbType.DateTime2).Value = at;
        command.Parameters.AddWithValue("@rowKey", rowKey);
        command.Parameters.AddWithValue("@newValue", newValue);
        command.Parameters.AddWithValue("@author", author);
        command.Parameters.AddWithValue("@origin", origin);
        await command.ExecuteNonQueryAsync();
    }

    private async Task WriteStructureAsync(string entityType, int entityId, DateTime at, int author)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO aud.StructureChange
                (ChangedAt, TemplateVersionId, EntityType, EntityId, ChangeClass, Operation, ChangedByUserId)
            VALUES (@at, 0, @type, @id, 0, N'Update', @author);
            """;
        command.Parameters.Add("@at", System.Data.SqlDbType.DateTime2).Value = at;
        command.Parameters.AddWithValue("@type", entityType);
        command.Parameters.AddWithValue("@id", entityId);
        command.Parameters.AddWithValue("@author", author);
        await command.ExecuteNonQueryAsync();
    }

    private async Task WriteSecurityAsync(string eventType, int targetUser, DateTime at, int author, string? details = null)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO aud.SecurityEvent (ChangedAt, EventType, TargetUserId, DetailsJson, ChangedByUserId)
            VALUES (@at, @type, @target, @details, @author);
            """;
        command.Parameters.Add("@at", System.Data.SqlDbType.DateTime2).Value = at;
        command.Parameters.AddWithValue("@type", eventType);
        command.Parameters.AddWithValue("@target", targetUser);
        command.Parameters.AddWithValue("@details", (object?)details ?? DBNull.Value);
        command.Parameters.AddWithValue("@author", author);
        await command.ExecuteNonQueryAsync();
    }

    private async Task WriteWorkflowAsync(TestDocument doc, DateTime at, string tag, string reason)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO wf.ApprovalEvent (DocumentId, SheetDefId, PeriodKey, FromStatus, ToStatus, Action, ByUserId, At, Reason)
            VALUES (@doc, @sheet, 202603, 1, 2, 4, 1, @at, @reason);
            """;
        command.Parameters.AddWithValue("@doc", doc.DocumentId);
        command.Parameters.AddWithValue("@sheet", doc.SheetDefId);
        command.Parameters.Add("@at", System.Data.SqlDbType.DateTime2).Value = at;
        command.Parameters.AddWithValue("@reason", tag.Length > 0 ? reason : reason);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> ColumnCodeAsync(int columnDefId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Code FROM cfg.ColumnDef WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", columnDefId);

        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> ExportEventAsync(string eventType, string marker)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) DetailsJson FROM aud.SecurityEvent
             WHERE EventType = @type AND DetailsJson LIKE N'%' + @marker + N'%'
             ORDER BY Id DESC;
            """;
        command.Parameters.AddWithValue("@type", eventType);
        command.Parameters.AddWithValue("@marker", marker);

        return (string?)await command.ExecuteScalarAsync() ?? "{}";
    }

    private async Task<int> CreateUserAsync(string displayName)
    {
        var unique = Guid.NewGuid().ToString("N");
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sec.[User] (UserName, DisplayName, Provider, WindowsSid, SecurityStamp, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@u, @d, 0, @sid, @stamp, SYSUTCDATETIME());
            """;
        command.Parameters.AddWithValue("@u", $"sort-{unique}");
        command.Parameters.AddWithValue("@d", displayName);
        command.Parameters.AddWithValue("@sid", $"S-1-5-21-{unique}");
        command.Parameters.AddWithValue("@stamp", unique);

        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task<HttpClient> LoginAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, string[] permissions, string suffix = "")
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

        var role = new Role(
            EcrCode.Create($"AUSX_{_tag}{suffix}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Role" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        foreach (var permission in permissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        var name = $"ausx_{_tag}{suffix}";
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
        await db.SaveChangesAsync();

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {(app as EcrApiFactory)?.ErrorsText}");

        return client;
    }
}
