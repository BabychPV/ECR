// tests/Ecr.Api.Tests/MonthColumnRoleLeakApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

namespace Ecr.Api.Tests;

/// <summary>
/// Q-17, умови вмикання прапора: роль, звужена ПЕРІОДОМ j, не бачить і не змінює через ЖОДЕН канал значення, що належать
/// власнику поза її областю (M{m}, m≠j — екземпляр періоду m; введені не-місячні — якір року), і не створює/видаляє
/// рядки, що належать якорю поза областю. Усе наскрізно через справжній Api (<c>Collection:MonthColumnRouting</c> = true).
/// </summary>
[Collection("SqlServer")]
public sealed class MonthColumnRoleLeakApiTests(SqlServerFixture sql)
{
    private const string Password = "Month-Role-Leak-Q17!";
    private const string FlagVariable = "ECR_Collection__MonthColumnRouting";
    private const int Oct = 202610;

    private const decimal NovSecret = 7777771m;
    private const decimal AnchorSecret = 7777772m;
    private const decimal HistorySecret = 7777773m;
    private const decimal OwnVisible = 1234m;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    // ── (1) права на рядок: рядок належить якорю ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Роль_лише_на_жовтень_не_створює_і_не_видаляє_рядок_якоря_січня()
    {
        var s = await ArrangeAsync(from: Oct, to: Oct).ConfigureAwait(true);
        var (app, scoped) = await BootAsync(s, s.UserName, flag: true).ConfigureAwait(true);
        using var _a = app;
        using var _b = scoped;

        using var create = await scoped.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/rows", UriKind.Relative),
            new { tableInstanceId = s.Doc.TableInstanceId, rowKey = "LEAKROW" }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Empty(await LiveRowCountAsync(s, "LEAKROW").ConfigureAwait(true));

        using var delete = await scoped.DeleteAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/tables/{s.Doc.TableInstanceId}/rows/{s.RowKey}", UriKind.Relative))
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.NotEmpty(await LiveRowCountAsync(s, s.RowKey).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Роль_з_областю_січень_жовтень_створює_і_видаляє_рядок()
    {
        var s = await ArrangeAsync(from: 202601, to: Oct).ConfigureAwait(true);
        var (app, scoped) = await BootAsync(s, s.UserName, flag: true).ConfigureAwait(true);
        using var _a = app;
        using var _b = scoped;

        using var create = await scoped.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/rows", UriKind.Relative),
            new { tableInstanceId = s.Doc.TableInstanceId, rowKey = "OKROW" }).ConfigureAwait(true);
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync().ConfigureAwait(true));
        Assert.Equal(3, (await LiveRowCountAsync(s, "OKROW").ConfigureAwait(true)).Count);

        using var delete = await scoped.DeleteAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/tables/{s.Doc.TableInstanceId}/rows/OKROW", UriKind.Relative))
            .ConfigureAwait(true);
        Assert.True(delete.StatusCode == HttpStatusCode.OK, await delete.Content.ReadAsStringAsync().ConfigureAwait(true));
        Assert.Empty(await LiveRowCountAsync(s, "OKROW").ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Прапор_вимкнено_роль_лише_на_жовтень_створює_рядок_у_своєму_екземплярі_як_раніше()
    {
        var s = await ArrangeAsync(from: Oct, to: Oct).ConfigureAwait(true);
        var (app, scoped) = await BootAsync(s, s.UserName, flag: false).ConfigureAwait(true);
        using var _a = app;
        using var _b = scoped;

        using var create = await scoped.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/rows", UriKind.Relative),
            new { tableInstanceId = s.Doc.TableInstanceId, rowKey = "OLDROW" }).ConfigureAwait(true);
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync().ConfigureAwait(true));
        Assert.Equal([Oct], (await LiveRowCountAsync(s, "OLDROW").ConfigureAwait(true)).Select(r => r).ToList());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Прапор_вимкнено_роль_лише_на_жовтень_видаляє_рядок_свого_екземпляра_без_відмови_якоря()
    {
        var s = await ArrangeAsync(from: Oct, to: Oct).ConfigureAwait(true);
        var (app, scoped) = await BootAsync(s, s.UserName, flag: false).ConfigureAwait(true);
        using var _a = app;
        using var _b = scoped;

        using var create = await scoped.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/rows", UriKind.Relative),
            new { tableInstanceId = s.Doc.TableInstanceId, rowKey = "OLDDEL" }).ConfigureAwait(true);
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync().ConfigureAwait(true));

        using var delete = await scoped.DeleteAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/tables/{s.Doc.TableInstanceId}/rows/OLDDEL", UriKind.Relative))
            .ConfigureAwait(true);
        var body = await delete.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(delete.StatusCode == HttpStatusCode.OK, body);
        Assert.DoesNotContain("deleteRowAnchorPeriod", body, StringComparison.Ordinal);
        Assert.Empty(await LiveRowCountAsync(s, "OLDDEL").ConfigureAwait(true));
    }

    // ── (2) експорт: csv/json не віддають значення власників поза областю ──

    [Theory]
    [InlineData("csv")]
    [InlineData("json")]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Експорт_жовтня_не_віддає_M11_листопада_і_якір_січня_поза_областю_ролі(string format)
    {
        var s = await ArrangeAsync(from: Oct, to: Oct).ConfigureAwait(true);
        await PutAsync(s, 202611, "M11", NovSecret).ConfigureAwait(true);
        await PutAsync(s, 202601, "PLAIN", AnchorSecret).ConfigureAwait(true);
        await PutAsync(s, Oct, "M10", OwnVisible).ConfigureAwait(true);
        var (app, scoped) = await BootAsync(s, s.UserName, flag: true).ConfigureAwait(true);
        using var _a = app;
        using var _b = scoped;

        var text = await ExportTextAsync(app, scoped, s, format).ConfigureAwait(true);
        Assert.Contains(OwnVisible.ToString(CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
        Assert.DoesNotContain(Digits(NovSecret), text, StringComparison.Ordinal);
        Assert.DoesNotContain(Digits(AnchorSecret), text, StringComparison.Ordinal);

        // Регресія: користувач без звуження бачить усе.
        var (app2, admin) = await BootAsync(s, s.AdminName, flag: true).ConfigureAwait(true);
        using var _c = app2;
        using var _d = admin;
        var full = await ExportTextAsync(app2, admin, s, format).ConfigureAwait(true);
        Assert.Contains(Digits(NovSecret), full, StringComparison.Ordinal);
        Assert.Contains(Digits(AnchorSecret), full, StringComparison.Ordinal);
    }

    // ── (3) історія комірки: журнал не віддає значення власника поза областю ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Історія_комірки_M11_не_віддає_значення_листопада_ролі_лише_на_жовтень()
    {
        var s = await ArrangeAsync(from: Oct, to: Oct).ConfigureAwait(true);
        var (appAdmin, admin) = await BootAsync(s, s.AdminName, flag: true).ConfigureAwait(true);
        using var _a = appAdmin;
        using var _b = admin;
        var ok = await PatchAsync(admin, s, ("M11", HistorySecret)).ConfigureAwait(true);
        Assert.True(ok.Status == HttpStatusCode.OK, $"{(int)ok.Status}\n{ok.Body}\n{appAdmin.ErrorsText}");

        var (app, scoped) = await BootAsync(s, s.UserName, flag: true).ConfigureAwait(true);
        using var _c = app;
        using var _d = scoped;

        var from = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("O", CultureInfo.InvariantCulture));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(5).ToString("O", CultureInfo.InvariantCulture));
        using var response = await scoped.GetAsync(new Uri(
            $"/api/v1/audit/cells?from={from}&to={to}&limit=50&documentId={s.Doc.DocumentId}&rowKey={s.RowKey}&columnDefId={s.ColumnIds["M11"]}",
            UriKind.Relative)).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.DoesNotContain(Digits(HistorySecret), body, StringComparison.Ordinal);
    }

    // ── (4) порівняння версій: збережений зріз (до прапора) містить усі колонки, роль бачить лише свої ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Порівняння_версій_жовтня_не_віддає_колонки_інших_власників_ролі_лише_на_жовтень()
    {
        var s = await ArrangeAsync(from: Oct, to: Oct).ConfigureAwait(true);
        var rowId = s.Doc.RowIds[0];
        string[] aliases = ["M1", "M9", "M10", "M11", "M12", "PLAIN"];

        // Зріз подання, зроблений до прапора: усі колонки в екземплярі жовтня (старі значення 1..).
        await using (var db = s.Builder.CreateContext())
        {
            foreach (var alias in aliases)
            {
                db.CellValues.Add(new CellValue(
                    new CellAddress(new PeriodKey(Oct), rowId, s.ColumnIds[alias]), s.Doc.TableDefId,
                    new CellValueData { ValueNumeric = 1m }));
            }

            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var versionId = await SnapshotOctAsync(s).ConfigureAwait(true);

        var secrets = new Dictionary<string, decimal>
        {
            ["M1"] = 8800001m, ["M9"] = 8800009m, ["M10"] = OwnVisible, ["M11"] = 8800011m, ["M12"] = 8800012m,
            ["PLAIN"] = 8800099m,
        };
        await using (var db = s.Builder.CreateContext())
        {
            foreach (var alias in aliases)
            {
                var cell = await db.CellValues.SingleAsync(c =>
                    c.TableRowId == rowId && c.ColumnDefId == s.ColumnIds[alias]).ConfigureAwait(true);
                cell.Apply(new CellValueData { ValueNumeric = secrets[alias] });
            }

            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var (app, scoped) = await BootAsync(s, s.UserName, flag: true).ConfigureAwait(true);
        using var _a = app;
        using var _b = scoped;
        var (app2, admin) = await BootAsync(s, s.AdminName, flag: true).ConfigureAwait(true);
        using var _c = app2;
        using var _d = admin;
        var full = await CompareAsync(admin, s, versionId).ConfigureAwait(true);
        foreach (var alias in aliases)
        {
            Assert.True(full.Contains(Digits(secrets[alias]), StringComparison.Ordinal), $"{alias}: {full}");
        }

        var narrow = await CompareAsync(scoped, s, versionId).ConfigureAwait(true);
        Assert.True(
            narrow.Contains(OwnVisible.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal), narrow);
        foreach (var alias in aliases.Where(a => a != "M10"))
        {
            Assert.DoesNotContain(Digits(secrets[alias]), narrow, StringComparison.Ordinal);
            Assert.DoesNotContain($"\"{s.ColumnCodes[alias]}\"", narrow, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Прапор_вимкнено_порівняння_версій_не_маскує_колонок_ні_ролі_ні_адміну()
    {
        var s = await ArrangeAsync(from: Oct, to: Oct).ConfigureAwait(true);
        var rowId = s.Doc.RowIds[0];
        string[] aliases = ["M1", "M9", "M10", "M11", "M12", "PLAIN"];

        await using (var db = s.Builder.CreateContext())
        {
            foreach (var alias in aliases)
            {
                db.CellValues.Add(new CellValue(
                    new CellAddress(new PeriodKey(Oct), rowId, s.ColumnIds[alias]), s.Doc.TableDefId,
                    new CellValueData { ValueNumeric = 1m }));
            }

            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var versionId = await SnapshotOctAsync(s).ConfigureAwait(true);
        var secrets = aliases.Select((a, i) => (Alias: a, Value: 8810001m + i)).ToDictionary(x => x.Alias, x => x.Value);
        await using (var db = s.Builder.CreateContext())
        {
            foreach (var alias in aliases)
            {
                var cell = await db.CellValues.SingleAsync(c =>
                    c.TableRowId == rowId && c.ColumnDefId == s.ColumnIds[alias]).ConfigureAwait(true);
                cell.Apply(new CellValueData { ValueNumeric = secrets[alias] });
            }

            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var (app, scoped) = await BootAsync(s, s.UserName, flag: false).ConfigureAwait(true);
        using var _a = app;
        using var _b = scoped;
        var (app2, admin) = await BootAsync(s, s.AdminName, flag: false).ConfigureAwait(true);
        using var _c = app2;
        using var _d = admin;

        var narrow = await CompareAsync(scoped, s, versionId).ConfigureAwait(true);
        var full = await CompareAsync(admin, s, versionId).ConfigureAwait(true);
        foreach (var alias in aliases)
        {
            Assert.True(full.Contains(Digits(secrets[alias]), StringComparison.Ordinal), $"admin {alias}: {full}");
        }

        // До Q-17 (і з вимкненим прапором) межі «без періоду» для ролі, звуженої періодом, порожні: compare лишається
        // на безпечному боці й не віддає їй нічого. Прапор НЕ повинен цього міняти ні в який бік.
        foreach (var alias in aliases)
        {
            Assert.DoesNotContain(Digits(secrets[alias]), narrow, StringComparison.Ordinal);
        }
    }

    // ── (5) відмова на рядок називає період-якір ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Відмова_на_рядок_без_якоря_в_області_називає_період_якір()
    {
        var s = await ArrangeAsync(from: Oct, to: Oct).ConfigureAwait(true);
        var (app, scoped) = await BootAsync(s, s.UserName, flag: true).ConfigureAwait(true);
        using var _a = app;
        using var _b = scoped;

        using var create = await scoped.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/rows", UriKind.Relative),
            new { tableInstanceId = s.Doc.TableInstanceId, rowKey = "ANCHORMSG" }).ConfigureAwait(true);
        var createBody = await create.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Contains("err.ECR-ACCS-0403.addRowAnchorPeriod", createBody, StringComparison.Ordinal);
        Assert.Contains("202601", createBody, StringComparison.Ordinal);

        using var delete = await scoped.DeleteAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/tables/{s.Doc.TableInstanceId}/rows/{s.RowKey}", UriKind.Relative))
            .ConfigureAwait(true);
        var deleteBody = await delete.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Contains("err.ECR-ACCS-0403.deleteRowAnchorPeriod", deleteBody, StringComparison.Ordinal);
        Assert.Contains("202601", deleteBody, StringComparison.Ordinal);
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private static async Task<string> CompareAsync(HttpClient client, Scenario s, long from)
    {
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/compare?from={from}&to=current", UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"compare: {response.StatusCode}\n{body}");
        return body;
    }

    /// <summary>Зріз подання жовтня з поточних комірок екземпляра жовтня.</summary>
    private static async Task<long> SnapshotOctAsync(Scenario s)
    {
        await using var db = s.Builder.CreateContext();
        var cells = await db.CellValues.AsNoTracking()
            .Where(c => c.TableRowId == s.Doc.RowIds[0]).ToListAsync().ConfigureAwait(false);
        var payload = SubmissionPayload.Write(cells.Select(c => new CellRecord(
            new CellAddress(new PeriodKey(Oct), c.TableRowId, c.ColumnDefId), c.TableDefId,
            new CellValueData { ValueNumeric = c.ValueNumeric, ValueString = c.ValueString })));
        var userId = await db.Users.AsNoTracking().Where(u => u.UserName == s.UserName)
            .Select(u => u.Id).SingleAsync().ConfigureAwait(false);
        var snapshot = new SubmissionSnapshot(
            s.Doc.DocumentId, s.Doc.SheetDefId, Oct, s.Doc.TemplateVersionId,
            "[]", null, null, payload, new byte[32], DateTime.UtcNow, userId);
        db.SubmissionSnapshots.Add(snapshot);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return snapshot.Id;
    }

    private sealed record Scenario(
        TestDocumentBuilder Builder, TestDocument Doc, string UserName, string AdminName, string RowKey,
        Dictionary<string, int> ColumnIds, Dictionary<string, string> ColumnCodes, TestPeriodInstance November,
        TestPeriodInstance January);

    private static string Digits(decimal value) => decimal.Truncate(value).ToString(CultureInfo.InvariantCulture);

    private async Task<(EcrApiFactory App, HttpClient Client)> BootAsync(Scenario s, string userName, bool flag)
    {
        Environment.SetEnvironmentVariable(FlagVariable, flag ? "true" : "false");
        try
        {
            var app = new EcrApiFactory(sql);
            var client = app.CreateClient();
            var login = await client.PostAsJsonAsync(
                new Uri("/api/v1/login/local", UriKind.Relative),
                new { userName, password = Password }).ConfigureAwait(false);
            Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");

            return (app, client);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FlagVariable, null);
        }
    }

    private static async Task<(HttpStatusCode Status, string Body)> PatchAsync(
        HttpClient client, Scenario s, params (string Alias, decimal Value)[] cells)
    {
        using var read = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/tables/{s.Doc.TableInstanceId}", UriKind.Relative)).ConfigureAwait(false);
        var text = await read.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(read.IsSuccessStatusCode, $"GET зрізу: {read.StatusCode}\n{text}");
        var version = JsonDocument.Parse(text).RootElement.GetProperty("rows").EnumerateArray()
            .First(r => r.GetProperty("rowKey").GetString() == s.RowKey)
            .GetProperty("rowVersion").GetString()!;

        using var response = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.Doc.TableInstanceId,
                periodKey = Oct,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey = s.RowKey,
                        baseVersion = version,
                        cells = cells.Select(c => new
                        {
                            columnCode = s.ColumnCodes[c.Alias],
                            value = (object)c.Value.ToString(CultureInfo.InvariantCulture),
                        }).ToArray(),
                    },
                },
            }).ConfigureAwait(false);

        return (response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    /// <summary>Періоди, в екземплярах яких є ЖИВИЙ рядок із цим ключем.</summary>
    private static async Task<List<int>> LiveRowCountAsync(Scenario s, string rowKey)
    {
        await using var db = s.Builder.CreateContext();
        return await (
                from instance in db.TableInstances.AsNoTracking()
                where instance.DocumentId == s.Doc.DocumentId && instance.TableDefId == s.Doc.TableDefId
                join row in db.TableRows.AsNoTracking()
                    on new { P = instance.PeriodKeyValue, I = instance.Id }
                    equals new { P = row.PeriodKeyValue, I = row.TableInstanceId }
                where row.RowKeyValue == rowKey && !row.IsDeleted
                orderby instance.PeriodKeyValue
                select instance.PeriodKeyValue)
            .ToListAsync().ConfigureAwait(false);
    }

    private static async Task PutAsync(Scenario s, int period, string alias, decimal value)
    {
        var rowId = period switch
        {
            Oct => s.Doc.RowIds[0],
            202611 => s.November.RowIds[0],
            _ => s.January.RowIds[0],
        };
        await using var db = s.Builder.CreateContext();
        db.CellValues.Add(new CellValue(
            new CellAddress(new PeriodKey(period), rowId, s.ColumnIds[alias]), s.Doc.TableDefId,
            new CellValueData { ValueNumeric = value }));
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Вивантажує період документа й повертає ВЕСЬ текст файлу (json — як є; csv — склеєний zip).</summary>
    private static async Task<string> ExportTextAsync(EcrApiFactory app, HttpClient client, Scenario s, string format)
    {
        var start = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/export", UriKind.Relative),
            new { includeFormulas = false, includeStyles = false, language = "en", periodKey = Oct, format })
            .ConfigureAwait(false);
        Assert.True(start.StatusCode == HttpStatusCode.Accepted, $"експорт: {start.StatusCode}: {app.ErrorsText}");
        var jobId = (await start.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false)).GetProperty("jobId").GetString()!;

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
            using var reader = new StreamReader(entry.Open());
            all.Append(entry.FullName).Append('\n').Append(await reader.ReadToEndAsync().ConfigureAwait(false));
        }

        return all.ToString();
    }

    /// <summary>
    /// Документ 202610 (Mixed «місяці в колонках»), календар 1..12 Open, екземпляри січня (якір), жовтня, листопада;
    /// <c>UserName</c> — роль з областю періодів [from..to] (Submit), <c>AdminName</c> — роль без звуження.
    /// </summary>
    private async Task<Scenario> ArrangeAsync(int from, int to)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            Oct, columnCount: 2, rowCount: 1, rowMode: TableRowMode.Mixed,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12).ConfigureAwait(false);

        await using (var db = builder.CreateContext())
        {
            db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
            for (var m = 1; m <= 12; m++)
            {
                var key = (2026 * 100) + m;
                var period = key == Oct
                    ? await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == Oct).ConfigureAwait(false)
                    : db.Periods.Add(new Period(
                        doc.ProjectId, new PeriodKey(key), (byte)m,
                        new DateOnly(2026, m, 1), new DateOnly(2026, m, DateTime.DaysInMonth(2026, m)))).Entity;
                period.AdvanceTo(PeriodState.Open, Now);
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var codes = new Dictionary<string, string>();
        var ids = new Dictionary<string, int>();
        await using (var db = builder.CreateContext())
        {
            var columns = await db.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == doc.TableDefId)
                .ToDictionaryAsync(c => c.Id).ConfigureAwait(false);
            ids["PLAIN"] = doc.ColumnDefIds[1];
            codes["PLAIN"] = columns[doc.ColumnDefIds[1]].Code;
            for (var m = 1; m <= 12; m++)
            {
                ids[$"M{m}"] = doc.MonthColumnDefIds[m - 1];
                codes[$"M{m}"] = columns[doc.MonthColumnDefIds[m - 1]].Code;
            }
        }

        var userName = $"q17l_{Guid.NewGuid():N}"[..20];
        var adminName = $"q17a_{Guid.NewGuid():N}"[..20];
        await using (var db = builder.CreateContext())
        {
            foreach (var (name, narrowed) in new[] { (userName, true), (adminName, false) })
            {
                var user = new User(name, name, AuthProvider.Local);
                user.SetPassword(new PasswordHasher().Hash(Password));
                db.Users.Add(user);

                var role = new Role(
                    EcrCode.Create($"Q17{(narrowed ? "N" : "A")}{Guid.NewGuid():N}"[..14]),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = "Q-17 role leak" }));
                db.Roles.Add(role);
                await db.SaveChangesAsync().ConfigureAwait(false);

                db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
                db.RolePermissions.Add(new RolePermission(role.Id, "Document.Export"));
                var assignment = new RoleAssignment(role.Id, user.Id, null);
                if (narrowed)
                {
                    assignment.SetScope(RoleAssignmentScope.Create(
                        [doc.ProjectId], periodFrom: new PeriodKey(from), periodTo: new PeriodKey(to)));
                }

                db.RoleAssignments.Add(assignment);
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Submit));
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
        }

        string rowKey;
        await using (var db = builder.CreateContext())
        {
            rowKey = await db.TableRows.AsNoTracking().Where(r => r.Id == doc.RowIds[0])
                .Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(false);
        }

        var january = await builder.AddPeriodInstanceAsync(doc, new PeriodKey(202601)).ConfigureAwait(false);
        var november = await builder.AddPeriodInstanceAsync(doc, new PeriodKey(202611)).ConfigureAwait(false);
        return new Scenario(builder, doc, userName, adminName, rowKey, ids, codes, november, january);
    }
}
