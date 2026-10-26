// tests/Ecr.Api.Tests/MonthColumnRoutedWriteApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Q-17.3 (запис), наскрізно через HTTP на справжній базі: <c>PATCH /documents/{id}/cells</c> у стовпець з ІНШИМ
/// власником пишеться в екземпляр власника (місячний m — період m; введений не-місячний — якір року), читання
/// (<c>GET</c>) бачить те саме з будь-якого екземпляра. Прапор <c>Collection:MonthColumnRouting</c> вмикається
/// змінною оточення на час старту хоста.
/// </summary>
[Collection("SqlServer")]
public sealed class MonthColumnRoutedWriteApiTests(SqlServerFixture sql)
{
    private const string Password = "Month-Routed-Write-Q17!";
    private const string FlagVariable = "ECR_Collection__MonthColumnRouting";
    private const int Year = 2026;
    private const int Oct = 202610;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    // ── 1. PATCH M11 з 202610 → комірка в (202611, рядок 11); GET обох періодів однакове; аудит на 202611 ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Правка_M11_з_перегляду_202610_пишеться_в_екземпляр_202611_і_видна_з_обох()
    {
        var s = await ArrangeAsync(instanceMonths: [1, 10, 11]).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, Oct, ("M11", 111m))).ConfigureAwait(true);

        Assert.Equal([(202611, 111m)], await StoredAsync(s, "M11").ConfigureAwait(true));
        Assert.Equal(111m, await SliceValueAsync(client, s, Oct, "M11").ConfigureAwait(true));
        Assert.Equal(111m, await SliceValueAsync(client, s, 202611, "M11").ConfigureAwait(true));

        // Аудит — на фізичній адресі власника.
        Assert.Equal([202611], await AuditPeriodsAsync(s, "M11").ConfigureAwait(true));
    }

    // ── 3. Той самий M11 з 202611 і 202610 — одне значення без копій ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Правка_M11_з_202611_і_з_202610_дає_одне_значення_без_копій_у_перегляді()
    {
        var s = await ArrangeAsync(instanceMonths: [1, 10, 11]).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, 202611, ("M11", 5m))).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, Oct, ("M11", 6m))).ConfigureAwait(true);

        Assert.Equal([(202611, 6m)], await StoredAsync(s, "M11").ConfigureAwait(true));
        Assert.Equal(6m, await SliceValueAsync(client, s, Oct, "M11").ConfigureAwait(true));
        Assert.Equal(6m, await SliceValueAsync(client, s, 202611, "M11").ConfigureAwait(true));
    }

    // ── 4. Тіньовий рядок: у власника немає рядка з таким RowKey ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Немає_рядка_у_власника_створюється_тіньовий_рядок_і_значення_пишеться_в_нього()
    {
        var s = await ArrangeAsync(instanceMonths: [1, 10, 11], dropRowIn: [202611]).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, Oct, ("M11", 7m))).ConfigureAwait(true);

        Assert.Equal([(202611, 7m)], await StoredAsync(s, "M11").ConfigureAwait(true));
        Assert.Equal(7m, await SliceValueAsync(client, s, Oct, "M11").ConfigureAwait(true));
    }

    // ── 5a. Екземпляра власника немає → створюється ліниво ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Немає_екземпляра_власника_він_створюється_ліниво_з_рядками_якоря()
    {
        var s = await ArrangeAsync(instanceMonths: [1, 10]).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        Assert.DoesNotContain(202611, await InstancePeriodsAsync(s).ConfigureAwait(true));
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, Oct, ("M11", 8m))).ConfigureAwait(true);

        Assert.Contains(202611, await InstancePeriodsAsync(s).ConfigureAwait(true));
        Assert.Equal([(202611, 8m)], await StoredAsync(s, "M11").ConfigureAwait(true));
        Assert.Equal(8m, await SliceValueAsync(client, s, Oct, "M11").ConfigureAwait(true));
    }

    // ── 5b. Період без Period-рядка → fallback на фізичне ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Немає_рядка_періоду_власника_запис_лишається_в_екземплярі_перегляду()
    {
        var s = await ArrangeAsync(instanceMonths: [1, 10], skipPeriods: [11]).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, Oct, ("M11", 9m))).ConfigureAwait(true);

        Assert.Equal([(Oct, 9m)], await StoredAsync(s, "M11").ConfigureAwait(true));
        Assert.DoesNotContain(202611, await InstancePeriodsAsync(s).ConfigureAwait(true));
    }

    // ── 6. Batch all-or-nothing: закритий M3 + відкритий M11 → 403, нічого не записано ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Батч_із_закритим_M3_і_відкритим_M11_відхиляється_цілком_у_жодному_екземплярі_нічого()
    {
        var s = await ArrangeAsync(instanceMonths: [1, 3, 10, 11], stateOf: m => m == 3 ? PeriodState.Closed : PeriodState.Open)
            .ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var body = await ExpectAsync(app, HttpStatusCode.Forbidden, PatchAsync(client, s, Oct, ("M3", 3m), ("M11", 11m)))
            .ConfigureAwait(true);

        Assert.Equal("PeriodClosed", ReasonOf(body));
        Assert.Empty(await StoredAsync(s, "M3").ConfigureAwait(true));
        Assert.Empty(await StoredAsync(s, "M11").ConfigureAwait(true));
    }

    // ── 23. Пакет: не-місячний + M{m} Closed → відхилено, не-місячний теж не записано ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Пакет_не_місячний_плюс_закритий_M3_відхиляється_цілком()
    {
        var s = await ArrangeAsync(instanceMonths: [1, 3, 10], stateOf: m => m == 3 ? PeriodState.Closed : PeriodState.Open)
            .ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        await ExpectAsync(app, HttpStatusCode.Forbidden, PatchAsync(client, s, Oct, ("PLAIN", 1m), ("M3", 3m))).ConfigureAwait(true);

        Assert.Empty(await StoredAsync(s, "PLAIN").ConfigureAwait(true));
        Assert.Empty(await StoredAsync(s, "M3").ConfigureAwait(true));
    }

    // ── 7. Лише чужі комірки: рядок запиту захоплюється (claim), рядок власника — «дотик» ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Лише_чужі_комірки_версія_власного_рядка_змінюється_і_рядка_власника_теж()
    {
        var s = await ArrangeAsync(instanceMonths: [1, 10, 11]).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var viewBefore = await RowVersionAsync(s, Oct).ConfigureAwait(true);
        var ownerBefore = await RowVersionAsync(s, 202611).ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, Oct, ("M11", 4m))).ConfigureAwait(true);

        Assert.NotEqual(viewBefore, await RowVersionAsync(s, Oct).ConfigureAwait(true));
        Assert.NotEqual(ownerBefore, await RowVersionAsync(s, 202611).ConfigureAwait(true));

        // Застаріла baseVersion власного рядка (claim) → 409, а не мовчазний перезапис.
        using var stale = await PatchWithVersionAsync(client, s, Oct, viewBefore, ("M11", 5m)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal([(202611, 4m)], await StoredAsync(s, "M11").ConfigureAwait(true));
    }

    // ── 18. Не-місячний з 202610 → у якорі; 202603 бачить те саме ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Не_місячний_стовпець_з_202610_пишеться_в_якір_а_202603_бачить_те_саме()
    {
        var s = await ArrangeAsync(instanceMonths: [1, 3, 10]).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, Oct, ("PLAIN", 42m))).ConfigureAwait(true);

        Assert.Equal([(202601, 42m)], await StoredAsync(s, "PLAIN").ConfigureAwait(true));
        Assert.Equal(42m, await SliceValueAsync(client, s, 202603, "PLAIN").ConfigureAwait(true));
        Assert.Equal(42m, await SliceValueAsync(client, s, Oct, "PLAIN").ConfigureAwait(true));
        Assert.Equal([202601], await AuditPeriodsAsync(s, "PLAIN").ConfigureAwait(true));
    }

    // ── прапор вимкнено: усе лишається в екземплярі запиту ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Прапор_вимкнено_значення_пишеться_в_екземпляр_запиту()
    {
        var s = await ArrangeAsync(instanceMonths: [1, 10, 11]).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: false).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, Oct, ("M11", 1m), ("PLAIN", 2m))).ConfigureAwait(true);

        Assert.Equal([(Oct, 1m)], await StoredAsync(s, "M11").ConfigureAwait(true));
        Assert.Equal([(Oct, 2m)], await StoredAsync(s, "PLAIN").ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(
        TestDocumentBuilder Builder, TestDocument Doc, string UserName, string RowKey,
        Dictionary<string, int> ColumnIds, Dictionary<string, string> ColumnCodes,
        Dictionary<int, TestPeriodInstance> Others);

    private async Task<(EcrApiFactory App, HttpClient Client)> BootAsync(Scenario s, bool flag)
    {
        Environment.SetEnvironmentVariable(FlagVariable, flag ? "true" : "false");
        try
        {
            var app = new EcrApiFactory(sql);
            var client = app.CreateClient();
            var login = await client.PostAsJsonAsync(
                new Uri("/api/v1/login/local", UriKind.Relative),
                new { userName = s.UserName, password = Password }).ConfigureAwait(false);
            Assert.True(login.IsSuccessStatusCode, $"Вхід {s.UserName}: {login.StatusCode}: {app.ErrorsText}");

            return (app, client);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FlagVariable, null);
        }
    }

    private static async Task<string> ExpectAsync(EcrApiFactory app, HttpStatusCode expected, Task<HttpResponseMessage> call)
    {
        using var response = await call.ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(
            response.StatusCode == expected,
            $"{(int)response.StatusCode} замість {(int)expected}\n{body}\n{app.ErrorsText}");

        return body;
    }

    private static string ReasonOf(string body)
    {
        var root = JsonDocument.Parse(body).RootElement;
        if (root.TryGetProperty("reason", out var reason))
        {
            return reason.GetString() ?? string.Empty;
        }

        return root.TryGetProperty("details", out var details) && details.TryGetProperty("reason", out var inner)
            ? inner.GetString() ?? string.Empty
            : $"немає reason у відповіді: {body}";
    }

    private static long InstanceOf(Scenario s, int period)
        => period == Oct ? s.Doc.TableInstanceId : s.Others[period].TableInstanceId;

    private static async Task<JsonElement> GetSliceAsync(HttpClient client, Scenario s, int period)
    {
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/tables/{InstanceOf(s, period)}", UriKind.Relative)).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"GET зрізу {period}: {response.StatusCode}\n{text}");

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<decimal?> SliceValueAsync(HttpClient client, Scenario s, int period, string alias)
    {
        var slice = await GetSliceAsync(client, s, period).ConfigureAwait(false);
        var row = slice.GetProperty("rows").EnumerateArray().First(r => r.GetProperty("rowKey").GetString() == s.RowKey);
        return row.GetProperty("cells").TryGetProperty(s.ColumnCodes[alias], out var value) && value.ValueKind != JsonValueKind.Null
            ? decimal.Parse(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText(), System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Scenario s, int period, params (string Alias, decimal Value)[] cells)
        => PatchWithVersionAsync(client, s, period, version: null, cells);

    private static async Task<HttpResponseMessage> PatchWithVersionAsync(
        HttpClient client, Scenario s, int period, string? version, params (string Alias, decimal Value)[] cells)
    {
        if (version is null)
        {
            var slice = await GetSliceAsync(client, s, period).ConfigureAwait(false);
            version = slice.GetProperty("rows").EnumerateArray()
                .First(r => r.GetProperty("rowKey").GetString() == s.RowKey)
                .GetProperty("rowVersion").GetString()!;
        }

        return await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = InstanceOf(s, period),
                periodKey = period,
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
                            value = (object)c.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        }).ToArray(),
                    },
                },
            }).ConfigureAwait(false);
    }

    /// <summary>Фізичні комірки колонки в усіх екземплярах таблиці: (період, значення).</summary>
    private static async Task<List<(int Period, decimal Value)>> StoredAsync(Scenario s, string alias)
    {
        await using var db = s.Builder.CreateContext();
        var columnId = s.ColumnIds[alias];
        var rows = await (
                from cell in db.CellValues.AsNoTracking()
                where cell.ColumnDefId == columnId && cell.TableDefId == s.Doc.TableDefId && cell.ValueNumeric != null
                join row in db.TableRows.AsNoTracking()
                    on new { P = cell.PeriodKeyValue, R = cell.TableRowId } equals new { P = row.PeriodKeyValue, R = row.Id }
                join instance in db.TableInstances.AsNoTracking()
                    on new { P = row.PeriodKeyValue, I = row.TableInstanceId } equals new { P = instance.PeriodKeyValue, I = instance.Id }
                where instance.DocumentId == s.Doc.DocumentId
                orderby cell.PeriodKeyValue
                select new { cell.PeriodKeyValue, Value = cell.ValueNumeric!.Value })
            .ToListAsync().ConfigureAwait(false);

        return [.. rows.Select(r => (r.PeriodKeyValue, r.Value))];
    }

    private static async Task<List<int>> InstancePeriodsAsync(Scenario s)
    {
        await using var db = s.Builder.CreateContext();
        return await db.TableInstances.AsNoTracking()
            .Where(t => t.DocumentId == s.Doc.DocumentId && t.TableDefId == s.Doc.TableDefId)
            .Select(t => t.PeriodKeyValue).OrderBy(p => p).ToListAsync().ConfigureAwait(false);
    }

    private static async Task<string> RowVersionAsync(Scenario s, int period)
    {
        await using var db = s.Builder.CreateContext();
        var rowId = period == Oct ? s.Doc.RowIds[0] : s.Others[period].RowIds[0];
        var version = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == period && r.Id == rowId)
            .Select(r => r.RowVersion).SingleAsync().ConfigureAwait(false);
        return Convert.ToBase64String(version);
    }

    /// <summary>Періоди (PeriodKey) записів журналу комірки колонки.</summary>
    private async Task<List<int>> AuditPeriodsAsync(Scenario s, string alias)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PeriodKey FROM aud.CellChange WHERE DocumentId = @doc AND ColumnDefId = @col ORDER BY Id";
        command.Parameters.AddWithValue("@doc", s.Doc.DocumentId);
        command.Parameters.AddWithValue("@col", s.ColumnIds[alias]);

        var periods = new List<int>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            periods.Add(reader.GetInt32(0));
        }

        return periods;
    }

    private async Task<Scenario> ArrangeAsync(
        IEnumerable<int> instanceMonths,
        Func<int, PeriodState>? stateOf = null,
        IEnumerable<int>? skipPeriods = null,
        IEnumerable<int>? dropRowIn = null)
    {
        stateOf ??= _ => PeriodState.Open;
        var skipped = (skipPeriods ?? []).ToHashSet();

        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            Oct, columnCount: 2, rowCount: 1, rowMode: TableRowMode.Mixed,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12).ConfigureAwait(false);

        await using (var db = builder.CreateContext())
        {
            db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));

            for (var m = 1; m <= 12; m++)
            {
                var key = (Year * 100) + m;
                if (skipped.Contains(m))
                {
                    continue;
                }

                var period = key == Oct
                    ? await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == Oct).ConfigureAwait(false)
                    : db.Periods.Add(new Period(
                        doc.ProjectId, new PeriodKey(key), (byte)m,
                        new DateOnly(Year, m, 1), new DateOnly(Year, m, DateTime.DaysInMonth(Year, m)))).Entity;
                period.AdvanceTo(stateOf(m), Now);
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var columnIds = new Dictionary<string, int> { ["PLAIN"] = doc.ColumnDefIds[1] };
        var columnCodes = new Dictionary<string, string>();
        await using (var db = builder.CreateContext())
        {
            var columns = await db.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == doc.TableDefId)
                .ToDictionaryAsync(c => c.Id).ConfigureAwait(false);
            columnCodes["PLAIN"] = columns[doc.ColumnDefIds[1]].Code;
            for (var m = 1; m <= 12; m++)
            {
                columnIds[$"M{m}"] = doc.MonthColumnDefIds[m - 1];
                columnCodes[$"M{m}"] = columns[doc.MonthColumnDefIds[m - 1]].Code;
            }
        }

        var userName = $"q17w_{Guid.NewGuid():N}"[..20];
        await using (var db = builder.CreateContext())
        {
            var user = new User(userName, userName, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(
                EcrCode.Create($"Q17W{Guid.NewGuid():N}"[..14]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Q-17 routed write" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Submit));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        string rowKey;
        await using (var db = builder.CreateContext())
        {
            rowKey = await db.TableRows.AsNoTracking().Where(r => r.Id == doc.RowIds[0])
                .Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(false);
        }

        var others = new Dictionary<int, TestPeriodInstance>();
        foreach (var month in instanceMonths.Where(m => (Year * 100) + m != Oct))
        {
            others[(Year * 100) + month] = await builder
                .AddPeriodInstanceAsync(doc, new PeriodKey((Year * 100) + month)).ConfigureAwait(false);
        }

        // Рядок власника, якого немає (для сценарію тіньового рядка): фізично, разом з екземпляром лишається порожнім.
        foreach (var period in dropRowIn ?? [])
        {
            await using var db = builder.CreateContext();
            var rowId = others[period].RowIds[0];
            await db.TableRows.Where(r => r.PeriodKeyValue == period && r.Id == rowId).ExecuteDeleteAsync().ConfigureAwait(false);
        }

        return new Scenario(builder, doc, userName, rowKey, columnIds, columnCodes, others);
    }
}
