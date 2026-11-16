// tests/Ecr.Api.Tests/DependentMonthsApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
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
/// ✎ B79 (рішення людини 13.11), наскрізно через HTTP на справжній базі: <c>PATCH /documents/{id}/cells</c> у загальний
/// (не місячний) стовпець річної таблиці, який читає формула, відхиляється <c>DependentMonthsLocked</c>, поки хоч один
/// місяць року ЗАТВЕРДЖЕНО; поданий, чернетка й відхилений місяць не блокують.
/// </summary>
/// <remarks>
/// <para>
/// Диференційні тести на справжньому шляху: кожна відмова має поруч контроль, що відрізняється рівно одним входом
/// (стан місяця, стовпець без залежних, режим версії, користувач). Рік 2099 (періоди 209901…209912), власний документ
/// на тест; маршрутизація вмикається змінною оточення лише на час старту СВОГО хоста (правило 394: розкладка
/// <c>YearWithMonthColumns</c> у спільній базі не лишається).
/// </para>
/// <para>
/// ⛔ МУТАЦІЇ (локально, не комітити): (1) у <c>AccessDecisionService.Decide</c> прибрати виклик
/// <c>DependentMonthsRule.Apply</c> — червоніють «Затверджений_місяць…», «Усі_місяці_затверджено…»; (2) там само
/// прибрати зведення стану до <c>Draft</c> для стовпця з залежними — червоніє «Усі_місяці_подано…»; (3)
/// <c>DependentMonthsRule.Blocks</c> додати <c>Submitted</c> — червоніє «Поданий_місяць…».
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class DependentMonthsApiTests(SqlServerFixture sql)
{
    private const string Password = "Dependent-Months-B79!";
    private const string FlagVariable = "ECR_Collection__MonthColumnRouting";
    private const int Year = 2099;
    private const int View = 209910;
    private const int Anchor = 209901;

    /// <summary>Загальний стовпець, який читає формула (є залежні розрахунки).</summary>
    private const string Fuel = "FUEL";

    /// <summary>Загальний числовий стовпець без жодного читача.</summary>
    private const string Free = "FREE";

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    // ── 1. Затверджений місяць блокує; перелік місяців у відмові; нічого не записано ──

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Затверджений_місяць_блокує_загальний_стовпець_із_залежними_і_названий_у_відмові()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        await SetStatusAsync(s, 1, DocumentStatus.Approved).ConfigureAwait(true);
        await SetStatusAsync(s, 2, DocumentStatus.Submitted).ConfigureAwait(true);
        await SetStatusAsync(s, 3, DocumentStatus.Approved).ConfigureAwait(true);
        using var app = Boot();
        using var client = await SignInAsync(app, s.Writer).ConfigureAwait(true);

        var body = await ExpectAsync(app, HttpStatusCode.Forbidden, PatchAsync(client, s, (Fuel, 7m))).ConfigureAwait(true);
        var root = JsonDocument.Parse(body).RootElement;

        Assert.Equal("ECR-ACCS-0403", root.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-ACCS-0403.deniedCells", root.GetProperty("messageKey").GetString());
        Assert.Equal("DependentMonthsLocked", root.GetProperty("reason").GetString());
        Assert.Equal(new[] { 209901, 209903 }, root.GetProperty("months").EnumerateArray().Select(m => m.GetInt32()));
        Assert.Equal("209901, 209903", root.GetProperty("monthsText").GetString());
        Assert.Empty(await StoredAsync(s, Fuel).ConfigureAwait(true));

        // Зріз для клієнта: комірка сіра з тією самою причиною (підказка `deny.DependentMonthsLocked`).
        Assert.Equal("DependentMonthsLocked", await SliceReasonAsync(client, s, Fuel).ConfigureAwait(true));
        Assert.Null(await SliceReasonAsync(client, s, Free).ConfigureAwait(true));
    }

    // ── 2–4. Поданий, чернетка, відхилений — не блокують (той самий запит, інший стан місяця) ──

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    [InlineData(DocumentStatus.Submitted)]
    [InlineData(DocumentStatus.Draft)]
    [InlineData(DocumentStatus.Rejected)]
    public async Task Поданий_чернетка_й_відхилений_місяць_не_блокують(DocumentStatus status)
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        await SetStatusAsync(s, 1, status).ConfigureAwait(true);
        using var app = Boot();
        using var client = await SignInAsync(app, s.Writer).ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, (Fuel, 8m))).ConfigureAwait(true);

        Assert.Equal([(Anchor, 8m)], await StoredAsync(s, Fuel).ConfigureAwait(true));
    }

    // ── 5. Контроль: стовпець без залежних і місячний стовпець чернетки — затверджений місяць їх не чіпає ──

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Стовпець_без_залежних_і_місячний_стовпець_при_затвердженому_місяці_пишуться()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        await SetStatusAsync(s, 1, DocumentStatus.Approved).ConfigureAwait(true);
        using var app = Boot();
        using var client = await SignInAsync(app, s.Writer).ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, (Free, 9m))).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, ("M10", 10m))).ConfigureAwait(true);

        Assert.Equal([(Anchor, 9m)], await StoredAsync(s, Free).ConfigureAwait(true));
        Assert.Equal([(View, 10m)], await StoredAsync(s, "M10").ConfigureAwait(true));

        // Батч «без залежних + із залежними» відхиляється цілком: усе або нічого.
        var body = await ExpectAsync(app, HttpStatusCode.Forbidden, PatchAsync(client, s, (Free, 11m), (Fuel, 12m)))
            .ConfigureAwait(true);
        Assert.Equal("DependentMonthsLocked", ReasonOf(body));
        Assert.Equal([(Anchor, 9m)], await StoredAsync(s, Free).ConfigureAwait(true));
    }

    // ── Q1 і R8: рік цілком поданий / цілком затверджений — стовпець із залежними проти стовпця без них ──

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Усі_місяці_подано_стовпець_із_залежними_пишеться_а_без_залежних_лишається_DocumentSubmitted()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        for (var month = 1; month <= 12; month++)
        {
            await SetStatusAsync(s, month, DocumentStatus.Submitted).ConfigureAwait(true);
        }

        using var app = Boot();
        using var client = await SignInAsync(app, s.Writer).ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, (Fuel, 13m))).ConfigureAwait(true);
        var plain = await ExpectAsync(app, HttpStatusCode.Forbidden, PatchAsync(client, s, (Free, 14m))).ConfigureAwait(true);

        Assert.Equal("DocumentSubmitted", ReasonOf(plain));
        Assert.Equal([(Anchor, 13m)], await StoredAsync(s, Fuel).ConfigureAwait(true));
        Assert.Empty(await StoredAsync(s, Free).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Усі_місяці_затверджено_із_залежними_DependentMonthsLocked_з_усіма_місяцями_без_залежних_DocumentApproved()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        for (var month = 1; month <= 12; month++)
        {
            await SetStatusAsync(s, month, DocumentStatus.Approved).ConfigureAwait(true);
        }

        using var app = Boot();
        using var client = await SignInAsync(app, s.Writer).ConfigureAwait(true);

        var dependent = await ExpectAsync(app, HttpStatusCode.Forbidden, PatchAsync(client, s, (Fuel, 15m))).ConfigureAwait(true);
        var plain = await ExpectAsync(app, HttpStatusCode.Forbidden, PatchAsync(client, s, (Free, 16m))).ConfigureAwait(true);

        Assert.Equal("DependentMonthsLocked", ReasonOf(dependent));
        Assert.Equal(
            Enumerable.Range(1, 12).Select(m => (Year * 100) + m),
            JsonDocument.Parse(dependent).RootElement.GetProperty("months").EnumerateArray().Select(m => m.GetInt32()));
        Assert.Equal("DocumentApproved", ReasonOf(plain));
        Assert.Empty(await StoredAsync(s, Fuel).ConfigureAwait(true));
    }

    // ── 6. WorkflowMode = None: старий Approved не блокує ──

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Версія_без_погодження_старий_затверджений_місяць_не_блокує()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        await SetStatusAsync(s, 1, DocumentStatus.Approved).ConfigureAwait(true);
        await using (var db = s.Builder.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE cfg.TemplateVersion SET WorkflowMode = {(byte)WorkflowMode.None} WHERE Id = {s.Doc.TemplateVersionId}")
                .ConfigureAwait(true);
        }

        using var app = Boot();
        using var client = await SignInAsync(app, s.Writer).ConfigureAwait(true);

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(client, s, (Fuel, 17m))).ConfigureAwait(true);

        Assert.Equal([(Anchor, 17m)], await StoredAsync(s, Fuel).ConfigureAwait(true));
    }

    // ── 7. Порядок причин: чужий замок рядка перемагає; незаблокований рядок — DependentMonthsLocked ──

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Рядок_заблокований_іншою_людиною_відповідає_RowLocked_а_сусідній_DependentMonthsLocked()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        await SetStatusAsync(s, 1, DocumentStatus.Approved).ConfigureAwait(true);
        await using (var db = s.Builder.CreateContext())
        {
            var now = DateTime.UtcNow;
            db.RowLocks.Add(RowLock.Create(
                s.Doc.DocumentId, View, null, s.Doc.SheetCode, s.TableCode, s.RowKeys[0],
                now.AddMinutes(-5), now.AddDays(1), s.LockerId, "B79", now));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        using var app = Boot();
        using var client = await SignInAsync(app, s.Writer).ConfigureAwait(true);

        var locked = await ExpectAsync(app, HttpStatusCode.Forbidden, PatchAsync(client, s, row: 0, (Fuel, 18m))).ConfigureAwait(true);
        var free = await ExpectAsync(app, HttpStatusCode.Forbidden, PatchAsync(client, s, row: 1, (Fuel, 19m))).ConfigureAwait(true);

        Assert.Equal("RowLocked", ReasonOf(locked));
        Assert.False(JsonDocument.Parse(locked).RootElement.TryGetProperty("months", out _));
        Assert.Equal("DependentMonthsLocked", ReasonOf(free));
    }

    // ── 8. Порядок причин: людина без гранта на стовпець бачить NoGrant і не дізнається про місяці ──

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Користувач_без_гранта_на_стовпець_отримує_NoGrant_без_переліку_місяців()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        await SetStatusAsync(s, 1, DocumentStatus.Approved).ConfigureAwait(true);
        using var app = Boot();
        using var denied = await SignInAsync(app, s.ColumnDenied).ConfigureAwait(true);

        var body = await ExpectAsync(app, HttpStatusCode.Forbidden, PatchAsync(denied, s, (Fuel, 20m))).ConfigureAwait(true);

        Assert.Equal("NoGrant", ReasonOf(body));
        Assert.False(JsonDocument.Parse(body).RootElement.TryGetProperty("months", out _));
        Assert.Empty(await StoredAsync(s, Fuel).ConfigureAwait(true));
    }

    // ── 9. Невидимий документ лишається 404 (стан місяців не розкривається) ──

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Невидимий_документ_лишається_404()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        await SetStatusAsync(s, 1, DocumentStatus.Approved).ConfigureAwait(true);
        using var app = Boot();
        using var stranger = await SignInAsync(app, s.Stranger).ConfigureAwait(true);

        var body = await ExpectAsync(app, HttpStatusCode.NotFound, PatchAsync(stranger, s, (Fuel, 21m))).ConfigureAwait(true);

        Assert.DoesNotContain("DependentMonthsLocked", body, StringComparison.Ordinal);
        Assert.DoesNotContain("209901", body, StringComparison.Ordinal);
        Assert.Empty(await StoredAsync(s, Fuel).ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(
        TestDocumentBuilder Builder, TestDocument Doc, string Writer, int WriterId, string ColumnDenied, string Stranger,
        int LockerId, IReadOnlyList<string> RowKeys, string TableCode,
        Dictionary<string, int> ColumnIds, Dictionary<string, string> ColumnCodes);

    /// <summary>Хост із маршрутизацією місячних стовпців: змінна оточення діє лише на час його старту.</summary>
    private EcrApiFactory Boot()
    {
        Environment.SetEnvironmentVariable(FlagVariable, "true");
        try
        {
            var app = new EcrApiFactory(sql);
            using var warm = app.CreateClient();
            return app;
        }
        finally
        {
            Environment.SetEnvironmentVariable(FlagVariable, null);
        }
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
        return root.TryGetProperty("reason", out var reason)
            ? reason.GetString() ?? string.Empty
            : $"немає reason у відповіді: {body}";
    }

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Scenario s, params (string Alias, decimal Value)[] cells)
        => PatchAsync(client, s, row: 0, cells);

    /// <summary>PATCH з перегляду 209910; версія рядка — з бази (користувач без доступу зрізу не прочитає).</summary>
    private static async Task<HttpResponseMessage> PatchAsync(
        HttpClient client, Scenario s, int row, params (string Alias, decimal Value)[] cells)
    {
        string version;
        await using (var db = s.Builder.CreateContext())
        {
            var rowId = s.Doc.RowIds[row];
            version = Convert.ToBase64String(await db.TableRows.AsNoTracking()
                .Where(r => r.PeriodKeyValue == View && r.Id == rowId)
                .Select(r => r.RowVersion).SingleAsync().ConfigureAwait(false));
        }

        return await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.Doc.TableInstanceId,
                periodKey = View,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey = s.RowKeys[row],
                        baseVersion = version,
                        cells = cells.Select(c => new
                        {
                            columnCode = s.ColumnCodes[c.Alias],
                            value = (object)c.Value.ToString(CultureInfo.InvariantCulture),
                        }).ToArray(),
                    },
                },
            }).ConfigureAwait(false);
    }

    /// <summary>Причина заборони комірки першого рядка в зрізі (<c>null</c> — редагується).</summary>
    private static async Task<string?> SliceReasonAsync(HttpClient client, Scenario s, string alias)
    {
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/tables/{s.Doc.TableInstanceId.ToString(CultureInfo.InvariantCulture)}",
            UriKind.Relative)).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"GET зрізу: {response.StatusCode}\n{text}");

        var permissions = JsonDocument.Parse(text).RootElement.GetProperty("cellPermissions");
        return permissions.TryGetProperty($"{s.RowKeys[0]}:{s.ColumnCodes[alias]}", out var reason)
            ? reason.GetString()
            : null;
    }

    /// <summary>Фізичні комірки колонки в усіх екземплярах таблиці документа: (період, значення).</summary>
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

    /// <summary>Стан аркуша місяця <paramref name="month"/> року 2099 (оригінал аркуша, без копії).</summary>
    private static async Task SetStatusAsync(Scenario s, int month, DocumentStatus status)
    {
        if (status == DocumentStatus.Draft)
        {
            return;
        }

        await using var db = s.Builder.CreateContext();
        var state = new ApprovalState(s.Doc.DocumentId, s.Doc.SheetDefId, (Year * 100) + month);
        state.Submit(s.WriterId, Now);
        if (status == DocumentStatus.Approved)
        {
            state.Approve(s.WriterId, Now);
        }
        else if (status == DocumentStatus.Rejected)
        {
            state.Reject(s.WriterId, "B79", Now);
        }

        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Документ року 2099 з таблицею «місяці в колонках» (екземпляри 01, 02, 10; перегляд — 10, якір — 01), усі місяці
    /// календаря відкриті; формула місячного стовпця M12 читає загальний стовпець FUEL; FREE ніхто не читає.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            View, columnCount: 3, rowCount: 2, rowMode: TableRowMode.Mixed,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12).ConfigureAwait(false);

        var columnIds = new Dictionary<string, int> { [Fuel] = doc.ColumnDefIds[1], [Free] = doc.ColumnDefIds[2] };
        var columnCodes = new Dictionary<string, string>();
        string tableCode;
        List<string> rowKeys;

        await using (var db = builder.CreateContext())
        {
            db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));

            for (var m = 1; m <= 12; m++)
            {
                var key = (Year * 100) + m;
                var period = key == View
                    ? await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == View).ConfigureAwait(false)
                    : db.Periods.Add(new Period(
                        doc.ProjectId, new PeriodKey(key), (byte)m,
                        new DateOnly(Year, m, 1), new DateOnly(Year, m, DateTime.DaysInMonth(Year, m)))).Entity;
                period.AdvanceTo(PeriodState.Open, Now);
            }

            var columns = await db.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == doc.TableDefId)
                .ToDictionaryAsync(c => c.Id).ConfigureAwait(false);
            columnCodes[Fuel] = columns[columnIds[Fuel]].Code;
            columnCodes[Free] = columns[columnIds[Free]].Code;
            for (var m = 1; m <= 12; m++)
            {
                columnIds[$"M{m}"] = doc.MonthColumnDefIds[m - 1];
                columnCodes[$"M{m}"] = columns[doc.MonthColumnDefIds[m - 1]].Code;
            }

            tableCode = (await db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == doc.TableDefId).ConfigureAwait(false)).Code;
            rowKeys = await db.TableRows.AsNoTracking()
                .Where(r => r.PeriodKeyValue == View && r.TableInstanceId == doc.TableInstanceId)
                .OrderBy(r => r.Ordinal).Select(r => r.RowKeyValue).ToListAsync().ConfigureAwait(false);

            // Формула M12 = FUEL * 2 і її ребро в графі версії: саме воно робить FUEL стовпцем із залежними.
            var formula = new FormulaDef(
                doc.TableDefId, FormulaScope.Column, $"[{columnCodes[Fuel]}] * 2", ExpressionDialect.Template);
            formula.AssignColumn(doc.MonthColumnDefIds[11]);
            db.FormulaDefs.Add(formula);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.FormulaDependencies.Add(FormulaDependency.ForFormula(
                formula.Id, dependsOnKind: 0, doc.TableDefId, rowKey: null, columnIds[Fuel],
                filterJson: null, periodOffset: null, sortOrder: 0));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        int writerId;
        int lockerId;
        string writer;
        string columnDenied;
        string stranger;
        await using (var db = builder.CreateContext())
        {
            async Task<(int Id, string Name)> UserAsync(string prefix, GrantLevel? level, int? denyColumn)
            {
                var name = $"{prefix}_{Guid.NewGuid():N}"[..20];
                var user = new User(name, name, AuthProvider.Local);
                user.SetPassword(new PasswordHasher().Hash(Password));
                db.Users.Add(user);

                var role = new Role(
                    EcrCode.Create($"B79{Guid.NewGuid():N}"[..14]),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = "B79 dependent months" }));
                db.Roles.Add(role);
                await db.SaveChangesAsync().ConfigureAwait(false);

                db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
                db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
                if (level is { } granted)
                {
                    db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, granted));
                }

                if (denyColumn is { } column)
                {
                    db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Column, column, GrantLevel.None, isDeny: true));
                }

                await db.SaveChangesAsync().ConfigureAwait(false);
                return (user.Id, name);
            }

            (writerId, writer) = await UserAsync("b79w", GrantLevel.Submit, denyColumn: null).ConfigureAwait(false);
            (_, columnDenied) = await UserAsync("b79d", GrantLevel.Write, columnIds[Fuel]).ConfigureAwait(false);
            (_, stranger) = await UserAsync("b79s", level: null, denyColumn: null).ConfigureAwait(false);
            (lockerId, _) = await UserAsync("b79l", GrantLevel.Read, denyColumn: null).ConfigureAwait(false);
        }

        foreach (var month in new[] { 1, 2 })
        {
            await builder.AddPeriodInstanceAsync(doc, new PeriodKey((Year * 100) + month)).ConfigureAwait(false);
        }

        return new Scenario(
            builder, doc, writer, writerId, columnDenied, stranger, lockerId, rowKeys, tableCode, columnIds, columnCodes);
    }
}
