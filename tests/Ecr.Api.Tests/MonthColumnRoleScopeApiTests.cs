// tests/Ecr.Api.Tests/MonthColumnRoleScopeApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Q-17 (аудит P2-B): область ролі за ПЕРІОДАМИ (<c>RoleAssignmentScope</c>, D-214) для комірки колонки з ІНШИМ власником
/// перевіряється за періодом ВЛАСНИКА (місячний m → період m; введений не-місячний → якір), а не за періодом екземпляра
/// запиту — і для запису, і для читання. Прапор <c>Collection:MonthColumnRouting</c> вимкнено — як і раніше.
/// </summary>
[Collection("SqlServer")]
public sealed class MonthColumnRoleScopeApiTests(SqlServerFixture sql)
{
    private const string Password = "Month-Role-Scope-Q17!";
    private const string FlagVariable = "ECR_Collection__MonthColumnRouting";
    private const int Oct = 202610;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Роль_лише_на_жовтень_не_пише_M11_чужого_місяця_але_пише_власний_M10()
    {
        var s = await ArrangeAsync(from: Oct, to: Oct).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var denied = await PatchAsync(client, s, ("M11", 11m)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("NoGrant", ReasonOf(denied.Body));
        Assert.Empty(await StoredAsync(s, "M11").ConfigureAwait(true));

        var own = await PatchAsync(client, s, ("M10", 10m)).ConfigureAwait(true);
        Assert.True(own.Status == HttpStatusCode.OK, $"{(int)own.Status}\n{own.Body}\n{app.ErrorsText}");
        Assert.Equal([(Oct, 10m)], await StoredAsync(s, "M10").ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Роль_з_областю_жовтень_листопад_пише_M11()
    {
        var s = await ArrangeAsync(from: Oct, to: 202611).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var ok = await PatchAsync(client, s, ("M11", 11m)).ConfigureAwait(true);

        Assert.True(ok.Status == HttpStatusCode.OK, $"{(int)ok.Status}\n{ok.Body}\n{app.ErrorsText}");
        Assert.Equal([(202611, 11m)], await StoredAsync(s, "M11").ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Читання_маскує_M11_чужого_місяця_поза_областю_ролі_а_M10_віддає()
    {
        var s = await ArrangeAsync(from: Oct, to: Oct).ConfigureAwait(true);
        await PutAsync(s, 202611, "M11", 111m).ConfigureAwait(true);
        await PutAsync(s, Oct, "M10", 10m).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/tables/{s.Doc.TableInstanceId}", UriKind.Relative)).ConfigureAwait(true);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.IsSuccessStatusCode, text);

        var cells = JsonDocument.Parse(text).RootElement.GetProperty("rows").EnumerateArray()
            .First(r => r.GetProperty("rowKey").GetString() == s.RowKey).GetProperty("cells");
        Assert.True(cells.TryGetProperty(s.ColumnCodes["M10"], out var _x1), "власний M10 мусить бути видимий");
        Assert.False(cells.TryGetProperty(s.ColumnCodes["M11"], out var _x2), "M11 чужого місяця поза областю ролі мусить бути замаскований");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Прапор_вимкнено_роль_лише_на_жовтень_пише_M11_у_свій_екземпляр_як_раніше()
    {
        var s = await ArrangeAsync(from: Oct, to: Oct).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: false).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var ok = await PatchAsync(client, s, ("M11", 11m)).ConfigureAwait(true);

        Assert.True(ok.Status == HttpStatusCode.OK, $"{(int)ok.Status}\n{ok.Body}\n{app.ErrorsText}");
        Assert.Equal([(Oct, 11m)], await StoredAsync(s, "M11").ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(
        TestDocumentBuilder Builder, TestDocument Doc, string UserName, string RowKey,
        Dictionary<string, int> ColumnIds, Dictionary<string, string> ColumnCodes, TestPeriodInstance November);

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
                            value = (object)c.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        }).ToArray(),
                    },
                },
            }).ConfigureAwait(false);

        return (response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private static async Task<List<(int Period, decimal Value)>> StoredAsync(Scenario s, string alias)
    {
        await using var db = s.Builder.CreateContext();
        var columnId = s.ColumnIds[alias];
        var rows = await (
                from cell in db.CellValues.AsNoTracking()
                where cell.ColumnDefId == columnId && cell.TableDefId == s.Doc.TableDefId && cell.ValueNumeric != null
                orderby cell.PeriodKeyValue
                select new { cell.PeriodKeyValue, Value = cell.ValueNumeric!.Value })
            .ToListAsync().ConfigureAwait(false);

        return [.. rows.Select(r => (r.PeriodKeyValue, r.Value))];
    }

    private static async Task PutAsync(Scenario s, int period, string alias, decimal value)
    {
        var rowId = period == Oct ? s.Doc.RowIds[0] : s.November.RowIds[0];
        await using var db = s.Builder.CreateContext();
        db.CellValues.Add(new CellValue(
            new CellAddress(new PeriodKey(period), rowId, s.ColumnIds[alias]), s.Doc.TableDefId,
            new CellValueData { ValueNumeric = value }));
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Документ 202610 (таблиця «місяці в колонках», Mixed) з календарем 1..12 (усі Open); роль з областю періодів.</summary>
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
            for (var m = 1; m <= 12; m++)
            {
                ids[$"M{m}"] = doc.MonthColumnDefIds[m - 1];
                codes[$"M{m}"] = columns[doc.MonthColumnDefIds[m - 1]].Code;
            }
        }

        var userName = $"q17r_{Guid.NewGuid():N}"[..20];
        await using (var db = builder.CreateContext())
        {
            var user = new User(userName, userName, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(
                EcrCode.Create($"Q17R{Guid.NewGuid():N}"[..14]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Q-17 role scope" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
            var assignment = new RoleAssignment(role.Id, user.Id, null);
            assignment.SetScope(RoleAssignmentScope.Create(
                [doc.ProjectId], periodFrom: new PeriodKey(from), periodTo: new PeriodKey(to)));
            db.RoleAssignments.Add(assignment);
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Submit));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        string rowKey;
        await using (var db = builder.CreateContext())
        {
            rowKey = await db.TableRows.AsNoTracking().Where(r => r.Id == doc.RowIds[0])
                .Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(false);
        }

        var november = await builder.AddPeriodInstanceAsync(doc, new PeriodKey(202611)).ConfigureAwait(false);
        return new Scenario(builder, doc, userName, rowKey, ids, codes, november);
    }
}
