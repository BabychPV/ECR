// tests/Ecr.Api.Tests/MonthMirrorRowsApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Q-17.2, наскрізно через HTTP на справжній базі: дзеркальні рядки таблиць «місяці в колонках», річні стани
/// (<c>AnnualPeriodRule</c>) і впорядковані замки періодів. Прапор <c>Collection:MonthColumnRouting</c> вмикається
/// змінною оточення лише на час старту хоста.
/// </summary>
[Collection("SqlServer")]
public sealed class MonthMirrorRowsApiTests(SqlServerFixture sql)
{
    private const string Password = "Month-Mirror-Q17!";
    private const int Oct = 202610;
    private const string FlagVariable = "ECR_Collection__MonthColumnRouting";

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    // ── (19) CreateRow з 202610 → рядок в якорі й усіх наявних; новий екземпляр отримує рядки якоря ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Створення_рядка_з_перегляду_дає_рядок_в_якорі_і_всіх_наявних_екземплярах_а_новий_екземпляр_його_успадковує()
    {
        var s = await ArrangeAsync(instanceMonths: [1, 10, 11]).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var created = await ExpectAsync(app, HttpStatusCode.Created, PostRowAsync(client, s, Oct, "MIRROR1")).ConfigureAwait(true);
        Assert.Contains("MIRROR1", created, StringComparison.Ordinal);

        var rows = await LiveRowsAsync(s, "MIRROR1").ConfigureAwait(true);
        Assert.Equal([202601, 202610, 202611], rows.Select(r => r.Period).Order());
        Assert.Single(rows.Select(r => r.Ordinal).Distinct());

        // Новий екземпляр (грудень) при першому відкритті отримує рядки якоря, зокрема доданий користувачем.
        using var open = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/tables?periodKey=202612", UriKind.Relative)).ConfigureAwait(true);
        Assert.True(open.IsSuccessStatusCode, await open.Content.ReadAsStringAsync().ConfigureAwait(true));

        var afterOpen = await LiveRowsAsync(s, "MIRROR1").ConfigureAwait(true);
        Assert.Contains(202612, afterOpen.Select(r => r.Period));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Прапор_вимкнено_рядок_створюється_лише_в_екземплярі_запиту()
    {
        var s = await ArrangeAsync(instanceMonths: [1, 10, 11]).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: false).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        await ExpectAsync(app, HttpStatusCode.Created, PostRowAsync(client, s, Oct, "PLAINROW")).ConfigureAwait(true);

        var rows = await LiveRowsAsync(s, "PLAINROW").ConfigureAwait(true);
        Assert.Equal([Oct], rows.Select(r => r.Period));
    }

    // ── (20) DeleteRow при Submitted місяці з даними → 409; без даних — видаляє всюди ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Видалення_рядка_409_якщо_у_поданому_місяці_є_введене_значення_інакше_видаляє_у_всіх_екземплярах()
    {
        var s = await ArrangeAsync(instanceMonths: [1, 10, 11], submitted: [11]).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        await ExpectAsync(app, HttpStatusCode.Created, PostRowAsync(client, s, Oct, "DELME")).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.Created, PostRowAsync(client, s, Oct, "KEEPME")).ConfigureAwait(true);

        // Значення M11 у екземплярі 202611 для DELME (місяць 11 подано).
        await PutAsync(s, 202611, "DELME", "M11", 5m).ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.Conflict, DeleteRowAsync(client, s, Oct, "DELME")).ConfigureAwait(true);
        Assert.Equal(3, (await LiveRowsAsync(s, "DELME").ConfigureAwait(true)).Count);

        // Подання без введеного значення місячного стовпця не заважає: видаляється в усіх екземплярах.
        await ExpectAsync(app, HttpStatusCode.OK, DeleteRowAsync(client, s, Oct, "KEEPME")).ConfigureAwait(true);
        Assert.Empty(await LiveRowsAsync(s, "KEEPME").ConfigureAwait(true));
    }

    // ── (21) якір Closed, місяць 10 Open: проходить; усі Closed → 403 ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Закритий_якір_не_блокує_створення_рядка_доки_є_відкритий_місяць_а_всі_закриті_дають_403()
    {
        var s = await ArrangeAsync(
            instanceMonths: [1, 10, 11],
            stateOf: m => m == 1 ? PeriodState.Closed : PeriodState.Open).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        await ExpectAsync(app, HttpStatusCode.Created, PostRowAsync(client, s, Oct, "CLOSEDANCHOR")).ConfigureAwait(true);
        Assert.Equal(3, (await LiveRowsAsync(s, "CLOSEDANCHOR").ConfigureAwait(true)).Count);

        var allClosed = await ArrangeAsync(instanceMonths: [1, 10], stateOf: _ => PeriodState.Closed).ConfigureAwait(true);
        var (app2, client2) = await BootAsync(allClosed, flag: true).ConfigureAwait(true);
        using var ___ = app2;
        using var ____ = client2;

        var body = await ExpectAsync(app2, HttpStatusCode.Forbidden, PostRowAsync(client2, allClosed, Oct, "NOPE")).ConfigureAwait(true);
        Assert.Equal("PeriodClosed", ReasonOf(body));
        Assert.Empty(await LiveRowsAsync(allClosed, "NOPE").ConfigureAwait(true));
    }

    // ── (22) Grace без Open → пізня правка не-місячного стовпця ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Немає_Open_але_є_Grace_правка_не_місячного_стовпця_проходить_з_IsLateEdit()
    {
        var s = await ArrangeAsync(
            instanceMonths: [1, 10],
            stateOf: m => m == 9 ? PeriodState.Grace : PeriodState.Closed).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, Oct, s.RowKey, ("PLAIN", 4m))).ConfigureAwait(true);

        Assert.Equal([true], await LateMarksAsync(s, "PLAIN").ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Подання_одного_місяця_не_блокує_не_місячний_стовпець_а_подання_всіх_блокує()
    {
        var some = await ArrangeAsync(instanceMonths: [1, 10], submitted: [10]).ConfigureAwait(true);
        var (app, client) = await BootAsync(some, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, some, Oct, some.RowKey, ("PLAIN", 4m))).ConfigureAwait(true);

        var all = await ArrangeAsync(instanceMonths: [1, 10], submitted: [.. Enumerable.Range(1, 12)]).ConfigureAwait(true);
        var (app2, client2) = await BootAsync(all, flag: true).ConfigureAwait(true);
        using var ___ = app2;
        using var ____ = client2;

        var body = await ExpectAsync(app2, HttpStatusCode.Forbidden, PatchAsync(client2, all, Oct, all.RowKey, ("PLAIN", 4m))).ConfigureAwait(true);
        Assert.Equal("DocumentSubmitted", ReasonOf(body));
    }

    // ── (17) документ створено посеред року: GET порожньо, якір не створюється ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Документ_посеред_року_GET_не_створює_екземпляр_якоря()
    {
        var s = await ArrangeAsync(instanceMonths: [10]).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/tables/{s.Doc.TableInstanceId}", UriKind.Relative)).ConfigureAwait(true);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(true));

        await using var db = s.Builder.CreateContext();
        var periods = await db.TableInstances.AsNoTracking()
            .Where(t => t.DocumentId == s.Doc.DocumentId && t.TableDefId == s.Doc.TableDefId)
            .Select(t => t.PeriodKeyValue)
            .ToListAsync().ConfigureAwait(true);
        Assert.Equal([Oct], periods);
    }

    // ── (8) паралельні PATCH 3→5 і 5→3 без дедлоку ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Паралельні_PATCH_3_у_5_і_5_у_3_не_дають_дедлоку_двадцять_разів_поспіль()
    {
        var s = await ArrangeAsync(instanceMonths: [3, 5], view: 3).ConfigureAwait(true);
        // Прапор увімкнено: третій учасник — створення рядка (ВИНЯТКОВІ замки періодів за зростанням). Спільні замки
        // 3→5 і 5→3 самі по собі не блокуються; дедлок з'являється, коли між ними стає виняткове блокування.
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var failures = new List<string>();
        for (var i = 1; i <= 20; i++)
        {
            var a = PatchAsync(client, s, 202603, s.RowKeyOf(202603), ("M5", i));
            var b = PatchAsync(client, s, 202605, s.RowKeyOf(202605), ("M3", i));
            var c = PostRowAsync(client, s, 202603, $"RACE{i}");
            var results = await Task.WhenAll(a, b, c.ContinueWith(t => t.Result, TaskScheduler.Default)).ConfigureAwait(true);
            foreach (var response in results)
            {
                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created))
                {
                    failures.Add($"#{i}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync().ConfigureAwait(true)}");
                }

                response.Dispose();
            }
        }

        Assert.True(failures.Count == 0, $"{20 - failures.Count}/20 пройшло. Збої:\n{string.Join("\n", failures)}\n{app.ErrorsText}");
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(
        TestDocumentBuilder Builder, TestDocument Doc, string UserName, string RowKey,
        Dictionary<string, int> ColumnIds, Dictionary<string, string> ColumnCodes,
        Dictionary<int, TestPeriodInstance> Others, int ViewPeriod)
    {
        /// <summary>Ключ першого рядка екземпляра періоду (рядки будівника мають однакові ключі в усіх екземплярах).</summary>
        public string RowKeyOf(int period) => RowKey;
    }

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

    private static Task<HttpResponseMessage> PostRowAsync(HttpClient client, Scenario s, int period, string rowKey)
    {
        var instanceId = period == s.ViewPeriod ? s.Doc.TableInstanceId : s.Others[period].TableInstanceId;
        return client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/rows", UriKind.Relative),
            new { tableInstanceId = instanceId, rowKey });
    }

    private static Task<HttpResponseMessage> DeleteRowAsync(HttpClient client, Scenario s, int period, string rowKey)
    {
        var instanceId = period == s.ViewPeriod ? s.Doc.TableInstanceId : s.Others[period].TableInstanceId;
        return client.DeleteAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/tables/{instanceId}/rows/{rowKey}", UriKind.Relative));
    }

    private static async Task<HttpResponseMessage> PatchAsync(
        HttpClient client, Scenario s, int period, string rowKey, params (string Alias, decimal Value)[] cells)
    {
        var instanceId = period == s.ViewPeriod ? s.Doc.TableInstanceId : s.Others[period].TableInstanceId;
        using var read = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/tables/{instanceId}", UriKind.Relative)).ConfigureAwait(false);
        var text = await read.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(read.IsSuccessStatusCode, $"GET зрізу: {read.StatusCode}\n{text}");
        var version = JsonDocument.Parse(text).RootElement.GetProperty("rows").EnumerateArray()
            .First(r => r.GetProperty("rowKey").GetString() == rowKey)
            .GetProperty("rowVersion").GetString()!;

        return await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = instanceId,
                periodKey = period,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey,
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

    /// <summary>Живі рядки з цим ключем у всіх екземплярах таблиці: (період, ordinal).</summary>
    private static async Task<List<(int Period, int Ordinal)>> LiveRowsAsync(Scenario s, string rowKey)
    {
        await using var db = s.Builder.CreateContext();
        var rows = await (
                from instance in db.TableInstances.AsNoTracking()
                where instance.DocumentId == s.Doc.DocumentId && instance.TableDefId == s.Doc.TableDefId
                join row in db.TableRows.AsNoTracking()
                    on new { P = instance.PeriodKeyValue, I = instance.Id }
                    equals new { P = row.PeriodKeyValue, I = row.TableInstanceId }
                where row.RowKeyValue == rowKey && !row.IsDeleted
                select new { Period = instance.PeriodKeyValue, row.Ordinal })
            .ToListAsync().ConfigureAwait(false);

        return [.. rows.Select(r => (r.Period, r.Ordinal)).OrderBy(r => r.Period)];
    }

    private static async Task PutAsync(Scenario s, int period, string rowKey, string alias, decimal value)
    {
        await using var db = s.Builder.CreateContext();
        var rowId = await (
                from instance in db.TableInstances.AsNoTracking()
                where instance.DocumentId == s.Doc.DocumentId && instance.TableDefId == s.Doc.TableDefId
                      && instance.PeriodKeyValue == period
                join row in db.TableRows.AsNoTracking()
                    on new { P = instance.PeriodKeyValue, I = instance.Id }
                    equals new { P = row.PeriodKeyValue, I = row.TableInstanceId }
                where row.RowKeyValue == rowKey
                select row.Id)
            .SingleAsync().ConfigureAwait(false);

        db.CellValues.Add(new CellValue(
            new CellAddress(new PeriodKey(period), rowId, s.ColumnIds[alias]),
            s.Doc.TableDefId,
            new CellValueData { ValueNumeric = value }));
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>IsLateEdit записів журналу комірки колонки.</summary>
    private async Task<List<bool>> LateMarksAsync(Scenario s, string alias)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT IsLateEdit FROM aud.CellChange WHERE DocumentId = @doc AND ColumnDefId = @col ORDER BY Id";
        command.Parameters.AddWithValue("@doc", s.Doc.DocumentId);
        command.Parameters.AddWithValue("@col", s.ColumnIds[alias]);

        var marks = new List<bool>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            marks.Add(reader.GetBoolean(0));
        }

        return marks;
    }

    /// <summary>
    /// Документ перегляду <paramref name="view"/> (за замовчуванням 202610) з таблицею «місяці в колонках»
    /// (Mixed: PLAIN + M1..M12). Календар — усі 12 місяців зі станами <paramref name="stateOf"/> (за замовчуванням
    /// Open); екземпляри — лише <paramref name="instanceMonths"/>; <paramref name="submitted"/> — місяці з поданим
    /// аркушем. Користувач — з грантом Submit на проєкт.
    /// </summary>
    private async Task<Scenario> ArrangeAsync(
        IEnumerable<int> instanceMonths,
        Func<int, PeriodState>? stateOf = null,
        IEnumerable<int>? submitted = null,
        int view = 10)
    {
        stateOf ??= _ => PeriodState.Open;
        var viewPeriod = (2026 * 100) + view;

        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            viewPeriod, columnCount: 2, rowCount: 1, rowMode: TableRowMode.Mixed,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12).ConfigureAwait(false);

        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));

        var columns = await db.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == doc.TableDefId)
            .ToDictionaryAsync(c => c.Id).ConfigureAwait(false);
        var columnIds = new Dictionary<string, int> { ["STR"] = doc.ColumnDefIds[0], ["PLAIN"] = doc.ColumnDefIds[1] };
        var columnCodes = new Dictionary<string, string>
        {
            ["STR"] = columns[doc.ColumnDefIds[0]].Code,
            ["PLAIN"] = columns[doc.ColumnDefIds[1]].Code,
        };
        for (var m = 1; m <= 12; m++)
        {
            var id = doc.MonthColumnDefIds[m - 1];
            columnIds[$"M{m}"] = id;
            columnCodes[$"M{m}"] = columns[id].Code;
        }

        for (var m = 1; m <= 12; m++)
        {
            var key = (2026 * 100) + m;
            var period = key == viewPeriod
                ? await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == viewPeriod).ConfigureAwait(false)
                : db.Periods.Add(new Period(
                    doc.ProjectId, new PeriodKey(key), (byte)m,
                    new DateOnly(2026, m, 1), new DateOnly(2026, m, DateTime.DaysInMonth(2026, m)))).Entity;
            period.AdvanceTo(stateOf(m), Now);
        }

        foreach (var month in submitted ?? [])
        {
            var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, (2026 * 100) + month);
            state.Submit(userId: 1, Now);
            db.ApprovalStates.Add(state);
        }

        var userName = $"q17_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"Q17{Guid.NewGuid():N}"[..14]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Q-17 mirror rows" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Submit));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var rowKey = await db.TableRows.AsNoTracking()
            .Where(r => r.Id == doc.RowIds[0])
            .Select(r => r.RowKeyValue)
            .SingleAsync().ConfigureAwait(false);

        var others = new Dictionary<int, TestPeriodInstance>();
        foreach (var month in instanceMonths.Where(m => (2026 * 100) + m != viewPeriod))
        {
            others[(2026 * 100) + month] = await builder
                .AddPeriodInstanceAsync(doc, new PeriodKey((2026 * 100) + month)).ConfigureAwait(false);
        }

        return new Scenario(builder, doc, userName, rowKey, columnIds, columnCodes, others, viewPeriod);
    }
}
