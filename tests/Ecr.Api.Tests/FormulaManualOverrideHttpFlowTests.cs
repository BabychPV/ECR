// tests/Ecr.Api.Tests/FormulaManualOverrideHttpFlowTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ D-461 «Ручне значення замість формули» наскрізно через HTTP (справжній хост, SQL Server, публікація, черга
/// перерахунку): у таблиці з колонковою формулою <c>F = [A] * 2</c> рядки 1–3 лишаються формулами, 4–5 отримують ручні
/// значення з причиною; залежна сума на іншому аркуші бачить ручні значення; перерахунок після правки входу їх не
/// перезаписує; видалення повертає формулу; права, причина, прапорець «вимкнено», поданий аркуш і перенос версії.
/// </summary>
/// <remarks>
/// Мутаційні докази (у локальному worktree, не комітяться):
/// (а) <c>EditRules.CanEdit</c>: не вимагати <c>Document.OverrideFormula</c> (лише <c>FormulaOverridable</c>) —
///     червоніє крок «без права — 403 CalculatedCell»;
/// (б) <c>PatchCellsHandler.ValidReason</c>: пропускати порожню причину — червоніє «без причини — 422»;
/// (в) <c>RecalculationService.IsManualTarget</c>: не дивитися на <c>overridden</c> — червоніє «після правки A ручне
///     лишилося 100» (перерахунок пише 80);
/// (г) <c>RecalculationService.WithSeedWriters</c>: повертати <c>affected</c> без авторів — червоніє «R5 повернувся до
///     формули = 10» (комірка лишається порожньою);
/// (д) <c>PatchCellsHandler.PlanFormulaOverridesAsync</c>: не перевіряти <c>policy.Allows</c> — червоніє «прапорець
///     вимкнено: нове ручне — 403»;
/// (е) <c>TR_FormulaDef_Immutable</c>: повернути безумовну відмову UPDATE у замороженій версії — червоніє перемикач
///     на опублікованій версії (500 від тригера 50003);
/// (є) <c>FormulaOverrideMigration.Lost</c>: повертати порожньо — червоніє «сухий прогін: 1 втрачене ручне значення».
/// </remarks>
[Collection("SqlServer")]
public sealed class FormulaManualOverrideHttpFlowTests(SqlServerFixture sql)
{
    private const string Password = "Formula-Override-D461!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();
    private static readonly TimeSpan RecalcDeadline = TimeSpan.FromSeconds(60);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-461")]
    public async Task Ручне_значення_замість_формули_наскрізно()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var admin = await NewUserAsync(s, GrantLevel.Manage,
            "Template.View", "Template.Edit", "Template.Publish", EditRules.OverrideFormulaPermission).ConfigureAwait(true);
        var writer = await NewUserAsync(s, GrantLevel.Write).ConfigureAwait(true);
        var author = await NewUserAsync(s, GrantLevel.Submit, EditRules.OverrideFormulaPermission).ConfigureAwait(true);
        var approver = await NewUserAsync(s, GrantLevel.Approve, EditRules.OverrideFormulaPermission).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var adminClient = await SignInAsync(app, admin).ConfigureAwait(true);
        using var writerClient = await SignInAsync(app, writer).ConfigureAwait(true);
        using var authorClient = await SignInAsync(app, author).ConfigureAwait(true);
        using var approverClient = await SignInAsync(app, approver).ConfigureAwait(true);

        // ── Шаблон: F = [A] * 2 (таблиця A), S = SUM(F по R1..R5) (аркуш B); публікація ──
        await ExpectAsync(app, HttpStatusCode.OK, SaveFormulaAsync(adminClient, s.VersionId, s.TableA, s.FColumnId,
            $"[{s.ACode}] * 2")).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.OK, SaveFormulaAsync(adminClient, s.VersionId, s.TableB, s.SColumnId,
            $"SUM([{s.SheetA}].[{s.TableACode}].[{s.Rows[0]}:{s.Rows[4]}].[{s.FCode}])")).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, PublishAsync(adminClient, s.VersionId)).ConfigureAwait(true);

        var formulaId = await FormulaIdAsync(s, s.FColumnId).ConfigureAwait(true);

        // Без прапорця формула не замінюється навіть із правом.
        var off = await ExpectAsync(app, HttpStatusCode.Forbidden,
            PatchAsync(adminClient, s, s.Rows[3], s.FCode, "100", "r4", await VersionAsync(adminClient, s, s.Rows[3]).ConfigureAwait(true)))
            .ConfigureAwait(true);
        Assert.Equal(nameof(EditDenyReason.CalculatedCell), ReasonOf(off));

        // ── Перемикач на ОПУБЛІКОВАНІЙ версії: без нової версії, лічильник 0 ──
        var toggled = Json(await ExpectAsync(app, HttpStatusCode.OK, ToggleAsync(adminClient, s.VersionId, formulaId, true, false))
            .ConfigureAwait(true));
        Assert.True(toggled.GetProperty("changed").GetBoolean());
        Assert.True(toggled.GetProperty("allowManualOverride").GetBoolean());
        Assert.Equal(0, toggled.GetProperty("overriddenCells").GetInt32());
        Assert.Equal(TemplateVersionStatus.Published, await VersionStatusAsync(s, s.VersionId).ConfigureAwait(true));
        Assert.Equal(1, await StructureChangesAsync(s, formulaId).ConfigureAwait(true));

        // ── Входи 1..5 → F = 2..10, S = 30 ──
        for (var i = 0; i < 5; i++)
        {
            await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(adminClient, s, s.Rows[i], s.ACode,
                (i + 1).ToString(CultureInfo.InvariantCulture), null,
                await VersionAsync(adminClient, s, s.Rows[i]).ConfigureAwait(true))).ConfigureAwait(true);
        }

        await WaitAsync(app, adminClient, s, f: [2m, 4m, 6m, 8m, 10m], sum: 30m).ConfigureAwait(true);

        // ── Без права (лише Write) — 403 CalculatedCell; без причини — 422 overrideReasonRequired ──
        var denied = await ExpectAsync(app, HttpStatusCode.Forbidden,
            PatchAsync(writerClient, s, s.Rows[2], s.FCode, "55", "w", await VersionAsync(writerClient, s, s.Rows[2]).ConfigureAwait(true)))
            .ConfigureAwait(true);
        Assert.Equal(nameof(EditDenyReason.CalculatedCell), ReasonOf(denied));

        var noReason = await ExpectAsync(app, HttpStatusCode.UnprocessableEntity,
            PatchAsync(adminClient, s, s.Rows[3], s.FCode, "100", "   ", await VersionAsync(adminClient, s, s.Rows[3]).ConfigureAwait(true)))
            .ConfigureAwait(true);
        Assert.Equal("err.ECR-CELL-0422.overrideReasonRequired", Json(noReason).GetProperty("messageKey").GetString());
        Assert.DoesNotContain("   ", Json(noReason).ToString(), StringComparison.Ordinal);

        var control = await ExpectAsync(app, HttpStatusCode.UnprocessableEntity,
            PatchAsync(adminClient, s, s.Rows[3], s.FCode, "100", "bad\u0007reason", await VersionAsync(adminClient, s, s.Rows[3]).ConfigureAwait(true)))
            .ConfigureAwait(true);
        Assert.Equal("err.ECR-CELL-0422.overrideReasonRequired", Json(control).GetProperty("messageKey").GetString());

        // ── Ручні значення R4 = 100, R5 = 200 з причинами ──
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(adminClient, s, s.Rows[3], s.FCode, "100", "  ручна r4  ",
            await VersionAsync(adminClient, s, s.Rows[3]).ConfigureAwait(true))).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(adminClient, s, s.Rows[4], s.FCode, "200", "ручна r5",
            await VersionAsync(adminClient, s, s.Rows[4]).ConfigureAwait(true))).ConfigureAwait(true);

        await WaitAsync(app, adminClient, s, f: [2m, 4m, 6m, 100m, 200m], sum: 312m).ConfigureAwait(true);
        Assert.Equal((false, true), await StoredFlagsAsync(s, s.Rows[3]).ConfigureAwait(true));
        Assert.Equal((CellChangeOrigins.FormulaOverride, "ручна r4"), await LastChangeAsync(s, s.Rows[3]).ConfigureAwait(true));

        var slice = Json(await SliceAsync(app, adminClient, s).ConfigureAwait(true)).GetProperty("formulaOverride");
        Assert.True(slice.GetProperty("canOverride").GetBoolean());
        Assert.Contains(s.FCode, slice.GetProperty("overridableColumns").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("ручна r5", slice.GetProperty("manualCells").GetProperty($"{s.Rows[4]}:{s.FCode}").GetProperty("reason").GetString());

        // ── Правка входу: перерахунок рахує R1 (A = 10 → F = 20), а ручні R4/R5 не чіпає ──
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(adminClient, s, s.Rows[3], s.ACode, "40", null,
            await VersionAsync(adminClient, s, s.Rows[3]).ConfigureAwait(true))).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(adminClient, s, s.Rows[0], s.ACode, "10", null,
            await VersionAsync(adminClient, s, s.Rows[0]).ConfigureAwait(true))).ConfigureAwait(true);
        await WaitAsync(app, adminClient, s, f: [20m, 4m, 6m, 100m, 200m], sum: 330m).ConfigureAwait(true);

        // ── Видалення в ручній комірці R5 — повернення формули (5 * 2 = 10) ──
        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(adminClient, s, s.Rows[4], s.FCode, null, null,
            await VersionAsync(adminClient, s, s.Rows[4]).ConfigureAwait(true))).ConfigureAwait(true);
        await WaitAsync(app, adminClient, s, f: [20m, 4m, 6m, 100m, 10m], sum: 140m).ConfigureAwait(true);
        Assert.Equal((CellChangeOrigins.FormulaRestored, (string?)null), await LastChangeAsync(s, s.Rows[4]).ConfigureAwait(true));
        Assert.Equal(1, await MarksAsync(s).ConfigureAwait(true));

        // ── Вимкнення: dryRun рахує наявні (1 документ, 1 комірка), вимкнення їх не стирає, нові — 403 ──
        var dry = Json(await ExpectAsync(app, HttpStatusCode.OK, ToggleAsync(adminClient, s.VersionId, formulaId, false, true))
            .ConfigureAwait(true));
        Assert.False(dry.GetProperty("changed").GetBoolean());
        Assert.Equal(1, dry.GetProperty("overriddenDocuments").GetInt32());
        Assert.Equal(1, dry.GetProperty("overriddenCells").GetInt32());
        await ExpectAsync(app, HttpStatusCode.OK, ToggleAsync(adminClient, s.VersionId, formulaId, false, false)).ConfigureAwait(true);

        var refused = await ExpectAsync(app, HttpStatusCode.Forbidden,
            PatchAsync(adminClient, s, s.Rows[2], s.FCode, "66", "after off", await VersionAsync(adminClient, s, s.Rows[2]).ConfigureAwait(true)))
            .ConfigureAwait(true);
        Assert.Equal(nameof(EditDenyReason.CalculatedCell), ReasonOf(refused));
        Assert.Equal(100m, await StoredAsync(s, s.Rows[3], s.FColumnId).ConfigureAwait(true));
        var offSlice = Json(await SliceAsync(app, adminClient, s).ConfigureAwait(true)).GetProperty("formulaOverride");
        Assert.Empty(offSlice.GetProperty("overridableColumns").EnumerateArray());
        Assert.True(offSlice.GetProperty("manualCells").TryGetProperty($"{s.Rows[3]}:{s.FCode}", out _));

        // ── Поданий аркуш: автор — DocumentSubmitted; погоджувач поточного кроку з правом — дозволено ──
        await ExpectAsync(app, HttpStatusCode.OK, ToggleAsync(adminClient, s.VersionId, formulaId, true, false)).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, authorClient.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/submit", UriKind.Relative),
            new { sheetDefId = s.Document.SheetDefId, periodKey = s.PeriodKey })).ConfigureAwait(true);

        var byAuthor = await ExpectAsync(app, HttpStatusCode.Forbidden,
            PatchAsync(authorClient, s, s.Rows[1], s.FCode, "41", "author", await VersionAsync(authorClient, s, s.Rows[1]).ConfigureAwait(true)))
            .ConfigureAwait(true);
        Assert.Equal(nameof(EditDenyReason.DocumentSubmitted), ReasonOf(byAuthor));

        await ExpectAsync(app, HttpStatusCode.OK, PatchAsync(approverClient, s, s.Rows[1], s.FCode, "42", "погоджувач",
            await VersionAsync(approverClient, s, s.Rows[1]).ConfigureAwait(true))).ConfigureAwait(true);
        Assert.Equal((CellChangeOrigins.FormulaOverride, "погоджувач"), await LastChangeAsync(s, s.Rows[1]).ConfigureAwait(true));

        // ── Перенос версії: клон, де формула НЕ дозволяє ручне значення, — сухий прогін звітує втрати ──
        var clone = Json(await ExpectAsync(app, HttpStatusCode.Created, adminClient.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{s.VersionId}/clone", UriKind.Relative),
            new { newVersion = $"2.0.0.{Random.Shared.Next(1000, 999999)}" })).ConfigureAwait(true)).GetProperty("versionId").GetInt32();
        var cloneFormula = await FormulaIdInVersionAsync(s, clone, s.FCode).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.OK, ToggleAsync(adminClient, clone, cloneFormula, false, false)).ConfigureAwait(true);
        await ExpectAsync(app, HttpStatusCode.NoContent, PublishAsync(adminClient, clone)).ConfigureAwait(true);

        var dryRun = Json(await ExpectAsync(app, HttpStatusCode.OK, adminClient.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/migrate-version", UriKind.Relative),
            new { targetVersionId = clone, mode = "Safe", dryRun = true })).ConfigureAwait(true));
        Assert.Equal(2, dryRun.GetProperty("lostFormulaOverrides").GetInt64());
        Assert.Contains(
            dryRun.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("kind").GetString() == "OverrideLost" && i.GetProperty("values").GetInt64() == 2);
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(
        TestDocumentBuilder Builder, TestDocument Document, int PeriodKey, int VersionId,
        string SheetA, int TableA, string TableACode, string ACode, int FColumnId, string FCode,
        IReadOnlyList<string> Rows, int TableB, long InstanceB, string RowB, int SColumnId, string SCode);

    private static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement;

    private static string ReasonOf(string body)
    {
        var root = Json(body);
        return root.TryGetProperty("reason", out var reason) ? reason.GetString() ?? string.Empty : body;
    }

    private static Task<HttpResponseMessage> SaveFormulaAsync(HttpClient client, int versionId, int tableId, int columnId, string expression)
        => client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/tables/{tableId}/formulas/column/{columnId}", UriKind.Relative),
            new { dialect = "Template", expression });

    private static Task<HttpResponseMessage> PublishAsync(HttpClient client, int versionId)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/publish", UriKind.Relative), new { reason = "D-461" });

    private static Task<HttpResponseMessage> ToggleAsync(HttpClient client, int versionId, int formulaId, bool allow, bool dryRun)
        => client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/formulas/{formulaId}/manual-override", UriKind.Relative),
            new { allow, dryRun });

    private static Task<HttpResponseMessage> PatchAsync(
        HttpClient client, Scenario s, string rowKey, string columnCode, string? value, string? reason, string baseVersion)
        => client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.Document.TableInstanceId,
                periodKey = s.PeriodKey,
                origin = "UserEdit",
                rows = new[]
                {
                    new { rowKey, baseVersion, cells = new object[] { new { columnCode, value, overrideReason = reason } } },
                },
            });

    private static async Task<string> SliceAsync(EcrApiFactory app, HttpClient client, Scenario s, long? instance = null)
        => await ExpectAsync(app, HttpStatusCode.OK, client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Document.DocumentId}/tables/{instance ?? s.Document.TableInstanceId}", UriKind.Relative)))
            .ConfigureAwait(false);

    private static async Task<string> VersionAsync(HttpClient client, Scenario s, string rowKey)
    {
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Document.DocumentId}/tables/{s.Document.TableInstanceId}", UriKind.Relative)).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, text);

        return Json(text).GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("rowKey").GetString() == rowKey)
            .GetProperty("rowVersion").GetString()!;
    }

    /// <summary>Опитує зрізи, доки F (таблиця A) і S (аркуш B) не покажуть очікуване (черга асинхронна).</summary>
    private static async Task WaitAsync(EcrApiFactory app, HttpClient client, Scenario s, decimal[] f, decimal sum)
    {
        var deadline = DateTime.UtcNow + RecalcDeadline;
        string seen;
        while (true)
        {
            var rows = Json(await SliceAsync(app, client, s).ConfigureAwait(false)).GetProperty("rows").EnumerateArray()
                .ToDictionary(r => r.GetProperty("rowKey").GetString()!, r => Number(r.GetProperty("cells"), s.FCode));
            var b = Json(await SliceAsync(app, client, s, s.InstanceB).ConfigureAwait(false)).GetProperty("rows").EnumerateArray()
                .Single(r => r.GetProperty("rowKey").GetString() == s.RowB);
            var actualF = s.Rows.Select(k => rows.GetValueOrDefault(k)).ToArray();
            var actualS = Number(b.GetProperty("cells"), s.SCode);

            if (actualF.SequenceEqual(f.Select(v => (decimal?)v)) && actualS == sum)
            {
                return;
            }

            seen = $"F=[{string.Join(", ", actualF)}], S={actualS}";
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Очікувано F=[{string.Join(", ", f)}], S={sum}; є {seen}\n{app.ErrorsText}");
            }

            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    private static decimal? Number(JsonElement cells, string code)
        => !cells.TryGetProperty(code, out var value)
            ? null
            : value.ValueKind switch
            {
                JsonValueKind.Number => value.GetDecimal(),
                JsonValueKind.String => decimal.Parse(value.GetString()!, CultureInfo.InvariantCulture),
                _ => null,
            };

    private static async Task<string> ExpectAsync(EcrApiFactory app, HttpStatusCode expected, Task<HttpResponseMessage> call)
    {
        using var response = await call.ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} замість {(int)expected}\n{body}\n{app.ErrorsText}");
        return body;
    }

    private static async Task<int> FormulaIdAsync(Scenario s, int columnId)
    {
        await using var db = s.Builder.CreateContext();
        return await db.FormulaDefs.AsNoTracking().Where(f => f.ColumnDefId == columnId && !f.IsDeleted).Select(f => f.Id)
            .SingleAsync().ConfigureAwait(false);
    }

    private static async Task<int> FormulaIdInVersionAsync(Scenario s, int versionId, string columnCode)
    {
        await using var db = s.Builder.CreateContext();
        return await (
                from f in db.FormulaDefs.AsNoTracking()
                join c in db.ColumnDefs.AsNoTracking() on f.ColumnDefId equals c.Id
                join t in db.TableDefs.AsNoTracking() on c.TableDefId equals t.Id
                join sh in db.SheetDefs.AsNoTracking() on t.SheetDefId equals sh.Id
                where sh.TemplateVersionId == versionId && !f.IsDeleted
                select new { f.Id, c.Code })
            .ToListAsync().ContinueWith(t => t.Result.Single(x => x.Code == columnCode).Id, TaskScheduler.Default)
            .ConfigureAwait(false);
    }

    private static async Task<TemplateVersionStatus> VersionStatusAsync(Scenario s, int versionId)
    {
        await using var db = s.Builder.CreateContext();
        return await db.TemplateVersions.AsNoTracking().Where(v => v.Id == versionId).Select(v => v.Status).SingleAsync()
            .ConfigureAwait(false);
    }

    private static async Task<int> StructureChangesAsync(Scenario s, int formulaId)
    {
        await using var db = s.Builder.CreateContext();
        return await db.Database.SqlQuery<int>(
                $"SELECT COUNT(*) AS Value FROM aud.StructureChange WHERE EntityType = N'FormulaDef' AND EntityId = {formulaId} AND NewJson LIKE N'%AllowManualOverride%'")
            .SingleAsync().ConfigureAwait(false);
    }

    private static async Task<int> MarksAsync(Scenario s)
    {
        await using var db = s.Builder.CreateContext();
        return await db.CellFormulaOverrides.AsNoTracking().CountAsync(o => o.TableInstanceId == s.Document.TableInstanceId)
            .ConfigureAwait(false);
    }

    private static async Task<decimal?> StoredAsync(Scenario s, string rowKey, int columnId)
    {
        await using var db = s.Builder.CreateContext();
        return await (
                from c in db.CellValues.AsNoTracking()
                join r in db.TableRows.AsNoTracking() on new { c.PeriodKeyValue, Id = c.TableRowId } equals new { r.PeriodKeyValue, r.Id }
                where c.PeriodKeyValue == s.PeriodKey && c.ColumnDefId == columnId && r.RowKeyValue == rowKey
                      && r.TableInstanceId == s.Document.TableInstanceId
                select c.ValueNumeric)
            .SingleAsync().ConfigureAwait(false);
    }

    /// <summary>(IsCalculated комірки F, чи є позначка).</summary>
    private static async Task<(bool IsCalculated, bool Marked)> StoredFlagsAsync(Scenario s, string rowKey)
    {
        await using var db = s.Builder.CreateContext();
        var row = await db.TableRows.AsNoTracking()
            .SingleAsync(r => r.PeriodKeyValue == s.PeriodKey && r.TableInstanceId == s.Document.TableInstanceId && r.RowKeyValue == rowKey)
            .ConfigureAwait(false);
        var calculated = await db.CellValues.AsNoTracking()
            .Where(c => c.PeriodKeyValue == s.PeriodKey && c.TableRowId == row.Id && c.ColumnDefId == s.FColumnId)
            .Select(c => c.IsCalculated).SingleAsync().ConfigureAwait(false);
        var marked = await db.CellFormulaOverrides.AsNoTracking()
            .AnyAsync(o => o.PeriodKeyValue == s.PeriodKey && o.TableRowId == row.Id && o.ColumnDefId == s.FColumnId)
            .ConfigureAwait(false);
        return (calculated, marked);
    }

    private static async Task<(string Origin, string? Reason)> LastChangeAsync(Scenario s, string rowKey)
    {
        await using var db = s.Builder.CreateContext();
        var row = await db.Database.SqlQuery<ChangeRow>(
                $"""
                SELECT TOP (1) Origin, Reason FROM aud.CellChange
                WHERE DocumentId = {s.Document.DocumentId} AND ColumnDefId = {s.FColumnId} AND RowKey = {rowKey}
                  AND Origin <> N'Recalculation'
                ORDER BY Id DESC
                """)
            .SingleAsync().ConfigureAwait(false);
        return (row.Origin, row.Reason);
    }

    private sealed record ChangeRow(string Origin, string? Reason);

    private static async Task<string> NewUserAsync(Scenario s, GrantLevel level, params string[] permissions)
    {
        var name = $"d461_{Guid.NewGuid():N}"[..20];
        await using var db = s.Builder.CreateContext();
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(
            EcrCode.Create($"R{Guid.NewGuid():N}"[..12].ToUpperInvariant()),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "D-461" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in permissions.Prepend("Document.View").Distinct(StringComparer.Ordinal))
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, s.Document.ProjectId, level));
        await db.SaveChangesAsync().ConfigureAwait(false);
        return name;
    }

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    /// <summary>
    /// Таблиця A (Mixed, рядки R1..R5): C1 текст, C2 = вхід A (Decimal), FX = формула (Formula). Аркуш B: таблиця з
    /// одним рядком і формулою S. Версія — чернетка (формули пише й публікує тест). Період поточний і відкритий.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var periodKey = (siteToday.Year * 100) + siteToday.Month;
        var document = await builder.BuildAsync(periodKey, columnCount: 2, rowCount: 5, rowMode: TableRowMode.Mixed)
            .ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await using var db = builder.CreateContext();
        var tableA = await db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == document.TableDefId).ConfigureAwait(false);
        var input = await db.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == document.ColumnDefIds[1]).ConfigureAwait(false);
        var rows = await db.RowDefs.AsNoTracking().Where(r => r.TableDefId == document.TableDefId).OrderBy(r => r.Ordinal)
            .Select(r => r.RowKeyValue).ToListAsync().ConfigureAwait(false);

        var f = new ColumnDef(document.TableDefId, EcrCode.Create("FX"), Name("F"), 3, CellDataType.Formula);
        db.ColumnDefs.Add(f);

        var sheetB = new SheetDef(document.TemplateVersionId, EcrCode.Create($"XB{tag}"), Name("Sheet B"), 2);
        db.SheetDefs.Add(sheetB);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var tableB = new TableDef(sheetB.Id, EcrCode.Create($"TB{tag}"), Name("Table B"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(tableB);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var sum = new ColumnDef(tableB.Id, EcrCode.Create("SUMF"), Name("S"), 1, CellDataType.Formula);
        db.ColumnDefs.Add(sum);
        const string rowB = "B1";
        db.RowDefs.Add(new RowDef(tableB.Id, RowKey.Create(rowB), 1, Name(rowB), RowKind.Item));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceB = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(false);
        var rowIdB = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None).ConfigureAwait(false);
        var key = new PeriodKey(periodKey);
        db.TableInstances.Add(new TableInstance(key, instanceB, document.DocumentId, tableB.Id, now));
        db.TableRows.Add(new TableRow(key, rowIdB, instanceB, RowKey.Create(rowB), 1, now));
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, sheetB.Id));

        var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(now);
        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods.FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey)
            .ConfigureAwait(false);
        period.RecomputeBoundaries(policy, SiteZone);
        period.AdvanceTo(PeriodState.Open, now);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(
            builder, document, periodKey, document.TemplateVersionId, document.SheetCode, document.TableDefId,
            tableA.Code, input.Code, f.Id, f.Code, rows, tableB.Id, instanceB, rowB, sum.Id, sum.Code);
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
