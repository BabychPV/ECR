// tests/Ecr.Api.Tests/MonthColumnPeriodStatePatchTests.cs
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
/// P2-A (слово людини 26.10: «заборона лише для поданих/погоджених/закритих»), наскрізно через HTTP на справжній
/// базі: <c>PATCH /documents/{id}/cells</c> у колонку місяця k таблиці <c>MonthsInColumns</c> підлягає стану
/// СВОГО періоду k, а не лише періоду екземпляра (202610).
/// </summary>
/// <remarks>
/// ⛔ На старому коді (тестер, прохід A, P2-1): подано/погоджено період 202609 — PATCH M9 з <c>periodKey</c> 202610
/// давав <c>200</c>; те саме для закритого M3; журнал: <c>IsLateEdit = 0</c>. Проєкт будівника — чернетка: рішення
/// бере ЗБЕРЕЖЕНІ стани періодів, тож тест не залежить від годинника (як <c>SubmissionTargetApiTests</c>).
/// Мутаційні докази (detached-worktree): див. <c>MonthColumnPeriodStateDecisionTests</c>; плюс (4) у
/// <c>CheckAccess</c> не збирати <c>ColumnPeriods</c> — червоніє «Grace → IsLateEdit».
/// </remarks>
[Collection("SqlServer")]
public sealed class MonthColumnPeriodStatePatchTests(SqlServerFixture sql)
{
    private const string Password = "Month-Column-P2A!";
    private const int Oct = 202610;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "P2-A")]
    public async Task Поданий_місяць_колонки_403_DocumentSubmitted_а_погоджений_403_DocumentApproved_ДО_було_200()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var submitted = await ExpectAsync(app, HttpStatusCode.Forbidden, PatchAsync(client, s, ("M9", 92m))).ConfigureAwait(true);
        Assert.Equal("DocumentSubmitted", ReasonOf(submitted));
        Assert.Null(await StoredAsync(s, "M9").ConfigureAwait(true));

        var approved = await ExpectAsync(app, HttpStatusCode.Forbidden, PatchAsync(client, s, ("M7", 77m))).ConfigureAwait(true);
        Assert.Equal("DocumentApproved", ReasonOf(approved));
        Assert.Null(await StoredAsync(s, "M7").ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "P2-A")]
    public async Task Закритий_місяць_колонки_403_PeriodClosed_ДО_було_200()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var body = await ExpectAsync(app, HttpStatusCode.Forbidden, PatchAsync(client, s, ("M3", 33m))).ConfigureAwait(true);

        Assert.Equal("PeriodClosed", ReasonOf(body));
        Assert.Null(await StoredAsync(s, "M3").ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "P2-A")]
    public async Task Grace_місяць_колонки_200_і_пізня_правка_IsLateEdit_за_періодом_колонки()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, ("M8", 88m))).ConfigureAwait(true);

        Assert.Equal(88m, await StoredAsync(s, "M8").ConfigureAwait(true));
        Assert.Equal([(true, false)], await MarksAsync(s, "M8").ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "P2-A")]
    public async Task Відкритий_власний_і_майбутній_Scheduled_місяць_200_без_пізньої_правки()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        // M11 — відкритий інший місяць; M10 — місяць самого екземпляра; M12 — Scheduled (внесення наперед).
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, ("M11", 11m))).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, ("M10", 10m))).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, ("M12", 12m))).ConfigureAwait(true);

        foreach (var (code, value) in new[] { ("M11", 11m), ("M10", 10m), ("M12", 12m) })
        {
            Assert.Equal(value, await StoredAsync(s, code).ConfigureAwait(true));
            Assert.Equal([(false, false)], await MarksAsync(s, code).ConfigureAwait(true));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "P2-A")]
    public async Task Land_стиль_перенесення_значень_у_відкриті_і_майбутні_місяці_одним_батчем_проходить()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        await ExpectAsync(
            app, HttpStatusCode.OK,
            PatchAsync(client, s, ("M10", 1m), ("M11", 2m), ("M12", 3m), ("PLAIN", 4m))).ConfigureAwait(true);

        Assert.Equal(1m, await StoredAsync(s, "M10").ConfigureAwait(true));
        Assert.Equal(2m, await StoredAsync(s, "M11").ConfigureAwait(true));
        Assert.Equal(3m, await StoredAsync(s, "M12").ConfigureAwait(true));
        Assert.Equal(4m, await StoredAsync(s, "PLAIN").ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "P2-A")]
    public async Task Батч_із_закритим_місяцем_відхиляється_цілком_все_або_нічого()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var body = await ExpectAsync(
            app, HttpStatusCode.Forbidden, PatchAsync(client, s, ("M10", 10m), ("M3", 33m))).ConfigureAwait(true);

        Assert.Equal("PeriodClosed", ReasonOf(body));
        Assert.Null(await StoredAsync(s, "M10").ConfigureAwait(true));
        Assert.Null(await StoredAsync(s, "M3").ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "P2-A")]
    public async Task Поданий_період_екземпляра_не_блокує_відкритий_місяць_іншої_колонки_але_блокує_свої_комірки()
    {
        // Аркуш за 202610 (період екземпляра) подано. Колонка M11 належить періоду 11 (Open, не подано) —
        // слово людини: колонка підлягає стану СВОГО періоду; власна колонка M10 і звичайна — стану екземпляра.
        var s = await ArrangeAsync(submitInstancePeriod: true).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var plain = await ExpectAsync(app, HttpStatusCode.Forbidden, PatchAsync(client, s, ("PLAIN", 1m))).ConfigureAwait(true);
        Assert.Equal("DocumentSubmitted", ReasonOf(plain));
        var own = await ExpectAsync(app, HttpStatusCode.Forbidden, PatchAsync(client, s, ("M10", 1m))).ConfigureAwait(true);
        Assert.Equal("DocumentSubmitted", ReasonOf(own));

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, ("M11", 11m))).ConfigureAwait(true);
        Assert.Equal(11m, await StoredAsync(s, "M11").ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(TestDocumentBuilder Builder, TestDocument Doc, string UserName, string RowKey, Dictionary<string, int> ColumnIds, Dictionary<string, string> ColumnCodes);

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

    /// <summary>PATCH у період екземпляра (202610); <c>cells</c> — псевдоніми колонок (M3, M10, PLAIN…).</summary>
    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, Scenario s, params (string Alias, decimal Value)[] cells)
    {
        var version = await RowVersionAsync(client, s).ConfigureAwait(false);

        return await client.PatchAsJsonAsync(
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
    }

    private static async Task<string> RowVersionAsync(HttpClient client, Scenario s)
    {
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId}/tables/{s.Doc.TableInstanceId}", UriKind.Relative))
            .ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"GET зрізу: {response.StatusCode}\n{text}");

        return JsonDocument.Parse(text).RootElement.GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("rowKey").GetString() == s.RowKey)
            .GetProperty("rowVersion").GetString()!;
    }

    private async Task<decimal?> StoredAsync(Scenario s, string alias)
    {
        await using var db = s.Builder.CreateContext();

        return await db.CellValues.AsNoTracking()
            .Where(c => c.PeriodKeyValue == Oct && c.TableRowId == s.Doc.RowIds[0] && c.ColumnDefId == s.ColumnIds[alias])
            .Select(c => c.ValueNumeric)
            .SingleOrDefaultAsync()
            .ConfigureAwait(false);
    }

    /// <summary>(IsLateEdit, IsOutOfWindow) записів журналу комірки колонки.</summary>
    private async Task<List<(bool Late, bool OutOfWindow)>> MarksAsync(Scenario s, string alias)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT IsLateEdit, IsOutOfWindow FROM aud.CellChange WHERE DocumentId = @doc AND ColumnDefId = @col ORDER BY Id";
        command.Parameters.AddWithValue("@doc", s.Doc.DocumentId);
        command.Parameters.AddWithValue("@col", s.ColumnIds[alias]);

        var marks = new List<(bool, bool)>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            marks.Add((reader.GetBoolean(0), reader.GetBoolean(1)));
        }

        return marks;
    }

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    /// <summary>
    /// Документ у жовтні 2026 (Open) — таблиця <c>MonthsInColumns</c>: PLAIN (звичайна) та місячні колонки M3, M7, M8,
    /// M9, M10, M11, M12. Періоди: 03 Closed, 07 Open + Approved, 08 Grace, 09 Open + Submitted, 11 Open, 12 Scheduled.
    /// Користувач — з грантом <c>Submit</c> (він містить <c>Write</c>) на проєкт.
    /// </summary>
    private async Task<Scenario> ArrangeAsync(bool submitInstancePeriod = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(Oct, columnCount: 9, rowCount: 1).ConfigureAwait(false);

        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));

        var table = await db.TableDefs.SingleAsync(t => t.Id == doc.TableDefId).ConfigureAwait(false);
        table.SetLayout(TableLayoutKind.MonthsInColumns);

        var columns = await db.ColumnDefs.Where(c => c.TableDefId == doc.TableDefId).ToDictionaryAsync(c => c.Id).ConfigureAwait(false);
        var aliases = new[] { "STR", "PLAIN", "M3", "M7", "M8", "M9", "M10", "M11", "M12" };
        var columnIds = new Dictionary<string, int>();
        var columnCodes = new Dictionary<string, string>();
        for (var i = 0; i < aliases.Length; i++)
        {
            var column = columns[doc.ColumnDefIds[i]];
            if (i > 1)
            {
                column.SetMonth(true, byte.Parse(aliases[i][1..], System.Globalization.CultureInfo.InvariantCulture));
            }

            columnIds[aliases[i]] = column.Id;
            columnCodes[aliases[i]] = column.Code;
        }

        (await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == Oct).ConfigureAwait(false))
            .AdvanceTo(PeriodState.Open, Now);

        foreach (var (month, state) in new (byte, PeriodState)[]
                 {
                     (3, PeriodState.Closed), (7, PeriodState.Open), (8, PeriodState.Grace), (9, PeriodState.Open),
                     (11, PeriodState.Open), (12, PeriodState.Scheduled),
                 })
        {
            var period = new Period(
                doc.ProjectId, new PeriodKey((2026 * 100) + month), month,
                new DateOnly(2026, month, 1), new DateOnly(2026, month, DateTime.DaysInMonth(2026, month)));
            period.AdvanceTo(state, Now);
            db.Periods.Add(period);
        }

        var approved = new ApprovalState(doc.DocumentId, doc.SheetDefId, 202607);
        approved.Submit(userId: 1, Now);
        approved.Approve(userId: 2, Now);
        var submitted = new ApprovalState(doc.DocumentId, doc.SheetDefId, 202609);
        submitted.Submit(userId: 1, Now);
        db.ApprovalStates.AddRange(approved, submitted);

        if (submitInstancePeriod)
        {
            var instance = new ApprovalState(doc.DocumentId, doc.SheetDefId, Oct);
            instance.Submit(userId: 1, Now);
            db.ApprovalStates.Add(instance);
        }

        var userName = $"p2a_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"P2A{Guid.NewGuid():N}"[..14]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "P2-A month column" }));
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

        return new Scenario(builder, doc, userName, rowKey, columnIds, columnCodes);
    }
}
