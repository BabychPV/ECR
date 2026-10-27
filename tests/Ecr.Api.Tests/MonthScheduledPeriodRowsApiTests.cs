// tests/Ecr.Api.Tests/MonthScheduledPeriodRowsApiTests.cs
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
/// HU-16 (Q-17): екземпляр таблиці «місяці в колонках» у НЕ-якірному періоді (напр. <c>Scheduled</c> 202612) без
/// дзеркал рядків якоря (створений до прапора або до появи якоря) показує у перегляді ті самі рядки, що й
/// <c>Open</c>: рядки якоря — авторитетні за складом. Значення місячних колонок — за правилами власника (маска ролі
/// як в <c>Open</c>). Прапор <c>Collection:MonthColumnRouting</c> вимкнено — як і раніше (рядки не з'являються).
/// </summary>
[Collection("SqlServer")]
public sealed class MonthScheduledPeriodRowsApiTests(SqlServerFixture sql)
{
    private const string Password = "Month-Scheduled-Rows-HU16!";
    private const string FlagVariable = "ECR_Collection__MonthColumnRouting";
    private const int Anchor = 202601;
    private const int Dec = 202612;
    private const int AnchorRowCount = 14;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Scheduled_період_без_дзеркал_показує_рядки_якоря_і_значення_власника()
    {
        var s = await ArrangeAsync(scope: null).ConfigureAwait(true);
        await PutAsync(s.Builder, s.Doc, Anchor, s.Doc.RowIds[0], s.ColumnIds["M1"], 5m).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var slice = await ReadSliceAsync(client, s, app).ConfigureAwait(true);

        Assert.Equal(AnchorRowCount, slice.GetProperty("rows").GetArrayLength());
        var first = slice.GetProperty("rows").EnumerateArray().First(r => r.GetProperty("rowKey").GetString() == s.RowKey);
        Assert.True(first.GetProperty("cells").TryGetProperty(s.ColumnCodes["M1"], out var m1), "M1 — значення власника (якоря)");
        Assert.Equal("5", m1.ToString().TrimEnd('0').TrimEnd('.'));

        // Другий перегляд нічого не додає (ідемпотентність): склад рівно якір.
        var again = await ReadSliceAsync(client, s, app).ConfigureAwait(true);
        Assert.Equal(AnchorRowCount, again.GetProperty("rows").GetArrayLength());
        Assert.Equal(AnchorRowCount, await LiveRowCountAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Scheduled_період_обмежена_роль_бачить_рядки_але_M1_чужого_місяця_замасковано()
    {
        var s = await ArrangeAsync(scope: (Dec, Dec)).ConfigureAwait(true);
        await PutAsync(s.Builder, s.Doc, Anchor, s.Doc.RowIds[0], s.ColumnIds["M1"], 5m).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: true).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var slice = await ReadSliceAsync(client, s, app).ConfigureAwait(true);

        Assert.Equal(AnchorRowCount, slice.GetProperty("rows").GetArrayLength());
        var first = slice.GetProperty("rows").EnumerateArray().First(r => r.GetProperty("rowKey").GetString() == s.RowKey);
        Assert.False(first.GetProperty("cells").TryGetProperty(s.ColumnCodes["M1"], out var _m1), "M1 поза областю ролі мусить бути замаскований");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Прапор_вимкнено_Scheduled_період_лишається_без_рядків_і_нічого_не_створюється()
    {
        var s = await ArrangeAsync(scope: null).ConfigureAwait(true);
        var (app, client) = await BootAsync(s, flag: false).ConfigureAwait(true);
        using var _ = app;
        using var __ = client;

        var slice = await ReadSliceAsync(client, s, app).ConfigureAwait(true);

        Assert.Equal(0, slice.GetProperty("rows").GetArrayLength());
        Assert.Equal(0, await LiveRowCountAsync(s).ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(
        TestDocumentBuilder Builder, TestDocument Doc, string UserName, string RowKey, TestPeriodInstance December,
        Dictionary<string, int> ColumnIds, Dictionary<string, string> ColumnCodes);

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

    private static async Task<JsonElement> ReadSliceAsync(HttpClient client, Scenario s, EcrApiFactory app)
    {
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/tables/{s.December.TableInstanceId}?periodKey={Dec}",
            UriKind.Relative)).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}\n{text}\n{app.ErrorsText}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<int> LiveRowCountAsync(Scenario s)
    {
        await using var db = s.Builder.CreateContext();
        return await db.TableRows.AsNoTracking()
            .CountAsync(r => r.PeriodKeyValue == Dec && r.TableInstanceId == s.December.TableInstanceId && !r.IsDeleted)
            .ConfigureAwait(false);
    }

    private static async Task PutAsync(
        TestDocumentBuilder builder, TestDocument doc, int period, long rowId, int columnId, decimal value)
    {
        await using var db = builder.CreateContext();
        db.CellValues.Add(new CellValue(
            new CellAddress(new PeriodKey(period), rowId, columnId), doc.TableDefId,
            new CellValueData { ValueNumeric = value }));
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Документ-якір 202601 з <see cref="AnchorRowCount"/> рядками; календар 1..12 (1..11 Open, 12 Scheduled);
    /// екземпляр 202612 заведено БЕЗ рядків (як екземпляр, створений до прапора/якоря).
    /// </summary>
    private async Task<Scenario> ArrangeAsync((int From, int To)? scope)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            Anchor, columnCount: 2, rowCount: AnchorRowCount, rowMode: TableRowMode.Mixed,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12).ConfigureAwait(false);

        await using (var db = builder.CreateContext())
        {
            db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
            for (var m = 1; m <= 12; m++)
            {
                var key = (2026 * 100) + m;
                var period = key == Anchor
                    ? await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == Anchor).ConfigureAwait(false)
                    : db.Periods.Add(new Period(
                        doc.ProjectId, new PeriodKey(key), (byte)m,
                        new DateOnly(2026, m, 1), new DateOnly(2026, m, DateTime.DaysInMonth(2026, m)))).Entity;
                if (m < 12)
                {
                    period.AdvanceTo(PeriodState.Open, Now);
                }
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var december = await builder.AddPeriodInstanceAsync(doc, new PeriodKey(Dec)).ConfigureAwait(false);
        await using (var db = builder.CreateContext())
        {
            await db.TableRows.Where(r => r.PeriodKeyValue == Dec && r.TableInstanceId == december.TableInstanceId)
                .ExecuteDeleteAsync().ConfigureAwait(false);
        }

        var codes = new Dictionary<string, string>();
        var ids = new Dictionary<string, int>();
        string rowKey;
        await using (var db = builder.CreateContext())
        {
            var columns = await db.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == doc.TableDefId)
                .ToDictionaryAsync(c => c.Id).ConfigureAwait(false);
            for (var m = 1; m <= 12; m++)
            {
                ids[$"M{m}"] = doc.MonthColumnDefIds[m - 1];
                codes[$"M{m}"] = columns[doc.MonthColumnDefIds[m - 1]].Code;
            }

            rowKey = await db.TableRows.AsNoTracking().Where(r => r.Id == doc.RowIds[0])
                .Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(false);
        }

        var userName = $"hu16_{Guid.NewGuid():N}"[..20];
        await using (var db = builder.CreateContext())
        {
            var user = new User(userName, userName, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(
                EcrCode.Create($"HU16{Guid.NewGuid():N}"[..14]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "HU-16 scheduled rows" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
            var assignment = new RoleAssignment(role.Id, user.Id, null);
            if (scope is { } range)
            {
                assignment.SetScope(RoleAssignmentScope.Create(
                    [doc.ProjectId], periodFrom: new PeriodKey(range.From), periodTo: new PeriodKey(range.To)));
            }

            db.RoleAssignments.Add(assignment);
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Submit));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return new Scenario(builder, doc, userName, rowKey, december, ids, codes);
    }
}
