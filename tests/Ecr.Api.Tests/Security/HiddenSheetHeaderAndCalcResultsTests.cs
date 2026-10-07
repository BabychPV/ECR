using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// Доказ на справжньому SQL і HTTP двох фіксів коміту 9e075f6c, що мали лише код чи unit-тест:
/// (1) <c>PATCH /documents/{id}/header</c> не розкриває стан схованого від читача аркуша;
/// (2) <c>GET /documents/{id}/calculation-results</c> не віддає числа колонок схованого аркуша й виходи без прив'язки.
/// </summary>
/// <remarks>
/// Приховування двома способами: звуження ролі аркушами (D-214, "scope") і Deny на аркуш ("deny");
/// "none" — звичайна роль без обмежень (контроль паритету).
/// ⛔ Мутаційний доказ — у листі готовності lane/analiz/sec-docs-tests2.
/// </remarks>
[Collection("SqlServer")]
public sealed class HiddenSheetHeaderAndCalcResultsTests(SqlServerFixture sql)
{
    private const string Password = "Hidden-Sheet-Hdr-Calc-R!";
    private const string AreaCode = "AREA";
    private static readonly DateTime Now = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    // ── PATCH header ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("scope")]
    [InlineData("deny")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Шапка_звуженого_читача_відмовляє_однаково_для_схованого_поданого_і_погодженого_аркуша(string how)
    {
        var submitted = await ArrangeAsync(how, DocumentStatus.Submitted).ConfigureAwait(true);
        var approved = await ArrangeAsync(how, DocumentStatus.Approved).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var clientSubmitted = await SignedInAsync(app, submitted.UserName).ConfigureAwait(true);
        using var clientApproved = await SignedInAsync(app, approved.UserName).ConfigureAwait(true);

        var a = await PatchHeaderAsync(clientSubmitted, app, submitted).ConfigureAwait(true);
        var b = await PatchHeaderAsync(clientApproved, app, approved).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, a.Status);
        Assert.True(a.Status == b.Status, $"{a.Status} (Submitted) != {b.Status} (Approved)");

        // Причина не залежить від стану схованого аркуша. Роль, звужена аркушами, не має проєктного Write
        // взагалі (право на шапку не звужується) - відмова noProjectWriteGrant; Deny-роль має Write, і її причина -
        // загальна «подано» навіть для схованого Approved.
        Assert.True(Normalize(a.Body) == Normalize(b.Body), $"тіла різняться\n{a.Body}\n---\n{b.Body}");
        if (how == "deny")
        {
            Assert.Equal("DocumentSubmitted", Reason(a.Body));
            Assert.Equal("DocumentSubmitted", Reason(b.Body));
        }
        else
        {
            Assert.Null(Reason(a.Body));
        }

        foreach (var reply in new[] { a, b })
        {
            Assert.DoesNotContain("Approved", reply.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(submitted.HiddenCode[..5], reply.Body, StringComparison.Ordinal);
        }

        Assert.Null(await StoredAreaAsync(submitted).ConfigureAwait(true));
        Assert.Null(await StoredAreaAsync(approved).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Шапка_читача_із_Deny_на_аркуш_правиться_коли_схований_аркуш_не_поданий()
    {
        var s = await ArrangeAsync("deny", hiddenStatus: null).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var reply = await PatchHeaderAsync(client, app, s).ConfigureAwait(true);

        Assert.True(reply.Status == HttpStatusCode.OK, $"{reply.Status}: {reply.Body}\n{app.ErrorsText}");
        Assert.Equal("Tengiz", await StoredAreaAsync(s).ConfigureAwait(true));
    }

    [Theory]
    [InlineData(DocumentStatus.Submitted, "DocumentSubmitted")]
    [InlineData(DocumentStatus.Approved, "DocumentApproved")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Звичайна_роль_бачить_справжню_причину_блокування_шапки(DocumentStatus status, string reason)
    {
        var s = await ArrangeAsync("none", status).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var reply = await PatchHeaderAsync(client, app, s).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, reply.Status);
        Assert.Equal(reason, Reason(reply.Body));
        Assert.Null(await StoredAreaAsync(s).ConfigureAwait(true));
    }

    // ── GET calculation-results ───────────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Показники_схованого_аркуша_і_виходи_без_прив_язки_не_віддаються_а_видимого_віддаються()
    {
        var s = await ArrangeAsync("deny", DocumentStatus.Submitted).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var (status, body) = await ResultsAsync(client, s).ConfigureAwait(true);

        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");
        Assert.DoesNotContain(s.OutHidden, body, StringComparison.Ordinal);
        Assert.DoesNotContain(s.OutUnbound, body, StringComparison.Ordinal);
        Assert.Contains(s.OutVisible, OutputCodes(body));
    }

    /// <summary>
    /// Н-1: вихід прив'язаний і до видимої колонки A, і до схованої B — рядок результату, що походить із таблиці B
    /// не віддається вузькому читачу (рядок B ні в якому разі; known limitation — і рядок A).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Вихід_прив_язаний_і_до_схованої_таблиці_не_віддає_рядків_схованої_і_закрито_цілком()
    {
        var s = await ArrangeAsync("deny", DocumentStatus.Submitted).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var (status, body) = await ResultsAsync(client, s).ConfigureAwait(true);

        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");
        // Known limitation: рядок результату не знає своєї таблиці, тож вихід, прив'язаний і до схованої колонки,
        // не віддається вузькому читачу цілком (рядки ні B, ні A); точніше відсікання — backlog.
        Assert.DoesNotContain(s.OutShared, OutputCodes(body));
        Assert.DoesNotContain(s.RowKeyB, body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Роль_звужена_аркушами_не_має_Calculation_View_і_не_бачить_показників()
    {
        var s = await ArrangeAsync("scope", DocumentStatus.Submitted).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var (status, body) = await ResultsAsync(client, s).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.DoesNotContain(s.OutHidden, body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Звичайна_роль_бачить_усі_показники_і_рядки_обох_таблиць()
    {
        var s = await ArrangeAsync("none", DocumentStatus.Submitted).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var (status, body) = await ResultsAsync(client, s).ConfigureAwait(true);

        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");
        var codes = OutputCodes(body);
        Assert.Contains(s.OutVisible, codes);
        Assert.Contains(s.OutHidden, codes);
        Assert.Contains(s.OutUnbound, codes);
        Assert.Equal(2, codes.Count(c => c == s.OutShared));
        Assert.Contains(s.RowKeyB, body, StringComparison.Ordinal);
    }
    // ── допоміжне ────────────────────────────────────────────────────────────────────────

    private sealed record Reply(HttpStatusCode Status, string Body);

    private static async Task<Reply> PatchHeaderAsync(HttpClient client, EcrApiFactory app, Scenario s)
    {
        var uri = new Uri($"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/header", UriKind.Relative);
        var get = await client.GetAsync(uri).ConfigureAwait(false);
        var getBody = await get.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(get.StatusCode == HttpStatusCode.OK, $"GET header: {get.StatusCode}: {getBody}\n{app.ErrorsText}");
        var version = JsonDocument.Parse(getBody).RootElement.GetProperty("version").GetString();

        using var response = await client.PatchAsJsonAsync(
            uri,
            new { fields = new[] { new { code = AreaCode, value = (object?)"Tengiz", isEmpty = false } }, baseVersion = version })
            .ConfigureAwait(false);

        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private static async Task<(HttpStatusCode Status, string Body)> ResultsAsync(HttpClient client, Scenario s)
    {
        using var response = await client.GetAsync(
            new Uri($"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/calculation-results?periodKey={s.PeriodKey.ToString(CultureInfo.InvariantCulture)}", UriKind.Relative))
            .ConfigureAwait(false);

        return (response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private static List<string> OutputCodes(string body)
        => [.. JsonDocument.Parse(body).RootElement.EnumerateArray().Select(r => r.GetProperty("outputCode").GetString()!)];

    private static string? Reason(string body)
        => JsonDocument.Parse(body).RootElement.TryGetProperty("reason", out var reason) ? reason.GetString() : null;

    private static string Normalize(string body)
        => Regex.Replace(
            Regex.Replace(body, "\"(traceId|correlationId|requestId)\"\\s*:\\s*\"[^\"]*\",?", string.Empty),
            "[0-9]+", "N");

    private async Task<string?> StoredAreaAsync(Scenario s)
    {
        await using var db = Context();
        return await db.DocumentHeaderValues.AsNoTracking()
            .Where(v => v.DocumentId == s.DocumentId && v.HeaderFieldDefId == s.AreaFieldId)
            .Select(v => v.ValueString)
            .SingleOrDefaultAsync().ConfigureAwait(false);
    }

    private sealed record Scenario(
        long DocumentId, int PeriodKey, string UserName, int AreaFieldId, string HiddenCode,
        string OutVisible, string OutHidden, string OutUnbound, string OutShared, string RowKeyA, string RowKeyB);

    /// <summary>
    /// Документ із видимим аркушем A (чернетка; таблиця, колонка, показник OUT_V) і схованим B (стан задає тест;
    /// таблиця, колонка, показник OUT_H) та показником без прив'язки; поле шапки AREA; проєктний грант Write.
    /// </summary>
    private async Task<Scenario> ArrangeAsync(string how, DocumentStatus? hiddenStatus)
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(false);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var hiddenCode = $"HIDHC{tag}";
        var userName = $"hcr{Guid.NewGuid():N}"[..20];
        var outVisible = $"OUTV_{tag}";
        var outHidden = $"OUTH_{tag}";
        var outUnbound = $"OUTU_{tag}";
        var outShared = $"OUTS_{tag}";
        var rowKeyA = $"RWA{tag}";
        var rowKeyB = $"RWB{tag}";

        await using var db = Context();

        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));

        var sheetB = new SheetDef(b.TemplateVersionId, EcrCode.Create(hiddenCode), Name($"HiddenHcName{tag}"), 2);
        db.SheetDefs.Add(sheetB);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var tableB = new TableDef(
            sheetB.Id, EcrCode.Create($"HCTB{tag}"), Name($"HcTable{tag}"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(tableB);
        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, sheetB.Id));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var columnB = new ColumnDef(tableB.Id, EcrCode.Create($"HCCB{tag}"), Name("Col B"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(columnB);
        db.RowDefs.Add(new RowDef(tableB.Id, RowKey.Create(rowKeyB), 1, Name("Row B"), RowKind.Item));
        db.RowDefs.Add(new RowDef(b.TableDefId, RowKey.Create(rowKeyA), 9, Name("Row A"), RowKind.Item));

        var area = new HeaderFieldDef(b.TemplateVersionId, EcrCode.Create(AreaCode), Name("Area"), 0, CellDataType.String);
        db.HeaderFieldDefs.Add(area);

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Hdr calc role"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in new[] { "Document.View", "Calculation.View" })
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, b.ProjectId, GrantLevel.Write));
        if (how == "deny")
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Sheet, sheetB.Id, GrantLevel.Read, isDeny: true));
        }

        var assignment = new RoleAssignment(role.Id, user.Id, principalSid: null);
        db.RoleAssignments.Add(assignment);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (how == "scope")
        {
            var scope = RoleAssignmentScope.Create([b.ProjectId], [b.SheetCode], null, null).ToJson();
            await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE sec.RoleAssignment SET ScopeJson = {scope} WHERE Id = {assignment.Id}")
                .ConfigureAwait(false);
        }

        if (hiddenStatus is { } status)
        {
            var state = new ApprovalState(b.DocumentId, sheetB.Id, b.PeriodKey.Value);
            state.Submit(user.Id, Now);
            if (status == DocumentStatus.Approved)
            {
                state.Approve(user.Id, Now.AddMinutes(1));
            }

            db.ApprovalStates.Add(state);
        }

        // Методологія з двома прив'язками (колонка A — OUT_V, колонка B — OUT_H) і актуальний прогін із трьома числами.
        var methodology = new Methodology(EcrCode.Create($"HC{tag}"), Name("m"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        db.CalculationBindings.Add(new CalculationBinding(b.TableDefId, b.ColumnDefIds[1], methodology.Id, outVisible, "{}"));
        db.CalculationBindings.Add(new CalculationBinding(tableB.Id, columnB.Id, methodology.Id, outHidden, "{}"));

        db.CalculationBindings.Add(new CalculationBinding(b.TableDefId, b.ColumnDefIds[2], methodology.Id, outShared, "{}"));
        db.CalculationBindings.Add(new CalculationBinding(tableB.Id, columnB.Id, methodology.Id, outShared, "{}"));

        var run = new CalculationRun(b.ProjectId, b.PeriodKey.Value, triggeredByUserId: null, Now);
        run.Complete("Succeeded", Now.AddMinutes(1), "{}", errorMessage: null);
        run.MakeCurrent();
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync().ConfigureAwait(false);
        foreach (var (output, rowKey) in new[]
                 {
                     (outVisible, "row-1"), (outHidden, "row-1"), (outUnbound, "row-1"),
                     (outShared, rowKeyA), (outShared, rowKeyB),
                 })
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO calc.CalculationResult
                    (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
                VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {version.Id},
                        {b.PeriodKey.Value}, {b.DocumentId}, {rowKey}, {output}, CAST(1 AS decimal(34,16)), {unitId})
                """).ConfigureAwait(false);
        }

        return new Scenario(b.DocumentId, b.PeriodKey.Value, userName, area.Id, hiddenCode, outVisible, outHidden, outUnbound, outShared, rowKeyA, rowKeyB);
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}

