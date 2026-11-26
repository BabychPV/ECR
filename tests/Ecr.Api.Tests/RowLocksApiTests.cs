// tests/Ecr.Api.Tests/RowLocksApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
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
/// ✎ B75 справжнім HTTP: блокування рядків (<c>/documents/{id}/row-locks</c>) і право <c>Document.RowLock</c>; правка
/// заблокованого рядка — <c>403 RowLocked</c>; режим версії без погодження (<c>PUT …/workflow-mode</c>) і
/// <c>422 workflowDisabled</c> на поданні.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЇ (локально): (1) у <c>CreateRowLocksHandler.RequireMayLock</c> пропускати всіх — червоніє «Читач_без_права…»;
/// (2) у <c>ReleaseRowLockHandler</c> прибрати перевірку власника/права — червоніє «…чужий_писар_не_знімає…»;
/// (3) прибрати <c>WorkflowModeGuard</c> у поданні — червоніє «Подання_версії_без_погодження…»; (4) у
/// <c>SetTemplateWorkflowModeHandler</c> прибрати <c>HasDocumentsAsync</c> — червоніє «…з_документами_409».
/// </remarks>
[Collection("SqlServer")]
public sealed class RowLocksApiTests(SqlServerFixture sql)
{
    private const string Password = "Row-Locks-B75-2026!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75")]
    public async Task Автор_блокує_рядок_інший_писар_отримує_RowLocked_автор_пише_носій_права_знімає()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var author = await SignInAsync(app, s.Author).ConfigureAwait(true);
        using var writer = await SignInAsync(app, s.Writer).ConfigureAwait(true);
        using var holder = await SignInAsync(app, s.Holder).ConfigureAwait(true);

        var created = await JsonAsync(app, HttpStatusCode.OK, author.PostAsJsonAsync(LocksUri(s), LockBody(s, s.RowKeys[0])))
            .ConfigureAwait(true);
        var lockId = Assert.Single(created.EnumerateArray()).GetProperty("id").GetInt64();

        // Інший писар: заблокований рядок — 403 RowLocked; сусідній — пишеться.
        var denied = await JsonAsync(app, HttpStatusCode.Forbidden, PatchAsync(writer, s, 0, 11m)).ConfigureAwait(true);
        Assert.Contains("RowLocked", denied.ToString(), StringComparison.Ordinal);
        await JsonAsync(app, HttpStatusCode.OK, PatchAsync(writer, s, 1, 12m)).ConfigureAwait(true);

        // Q1: автор блокування пише свій заблокований рядок.
        await JsonAsync(app, HttpStatusCode.OK, PatchAsync(author, s, 0, 13m)).ConfigureAwait(true);

        // Перелік бачить і писар; зняти може носій права (лише Read на проєкт).
        var listed = await JsonAsync(app, HttpStatusCode.OK, writer.GetAsync(ListUri(s))).ConfigureAwait(true);
        var view = Assert.Single(listed.EnumerateArray());
        Assert.True(view.GetProperty("isActive").GetBoolean());
        Assert.False(view.GetProperty("canUnlock").GetBoolean());

        using (var release = await holder.DeleteAsync(LockUri(s, lockId)).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.NoContent, release.StatusCode);
        }

        await JsonAsync(app, HttpStatusCode.OK, PatchAsync(writer, s, 0, 14m)).ConfigureAwait(true);
        Assert.Contains("Unlock", await AuditOperationsAsync(lockId).ConfigureAwait(true));
    }

    /// <summary>
    /// integ-row-label-messages: рядок, доданий людиною (ключ — GUID без дефісів), уже заблоковано — 409
    /// <c>rowAlreadyLocked</c> називає його номером у зрізі («2»: другий за Id), а не 32 символами; сирий ключ лишається в
    /// <c>rowKeys</c> для клієнта. ⛔ Мутація: прибрати <c>RowDisplayNames</c> у <c>CreateRowLocksHandler</c> — у тексті
    /// «(added by a user)».
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "integ-row-label-messages")]
    public async Task Повторне_блокування_рядка_з_GUID_ключем_називає_рядок_номером()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var keys = new[] { Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") };
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            for (var i = 0; i < keys.Length; i++)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $@"UPDATE r SET RowKey = {keys[i]} FROM doc.TableRow r
                       JOIN doc.TableInstance t ON t.Id = r.TableInstanceId
                       WHERE t.DocumentId = {s.Document.DocumentId} AND r.RowKey = {s.RowKeys[i]}").ConfigureAwait(true);
            }
        }

        using var app = new EcrApiFactory(sql);
        using var author = await SignInAsync(app, s.Author).ConfigureAwait(true);
        await JsonAsync(app, HttpStatusCode.OK, author.PostAsJsonAsync(LocksUri(s), LockBody(s, keys[1]))).ConfigureAwait(true);

        var again = await JsonAsync(app, HttpStatusCode.Conflict, author.PostAsJsonAsync(LocksUri(s), LockBody(s, keys[1])))
            .ConfigureAwait(true);

        Assert.Equal("err.ECR-ROW-0409.rowAlreadyLocked", again.GetProperty("messageKey").GetString());
        Assert.Equal(keys[1], again.GetProperty("rowKeys").GetString());
        var detail = again.GetProperty("detail").GetString()!;
        Assert.DoesNotContain(keys[1], detail, StringComparison.Ordinal);
        Assert.Contains("locked for this time: 2.", detail, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75")]
    public async Task Читач_без_права_не_блокує_а_чужий_писар_не_знімає_і_повтор_409()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var author = await SignInAsync(app, s.Author).ConfigureAwait(true);
        using var writer = await SignInAsync(app, s.Writer).ConfigureAwait(true);
        using var reader = await SignInAsync(app, s.Reader).ConfigureAwait(true);

        // Читач без права й не власник; писар — не власник документа і без права.
        await JsonAsync(app, HttpStatusCode.Forbidden, reader.PostAsJsonAsync(LocksUri(s), LockBody(s, s.RowKeys[0])))
            .ConfigureAwait(true);
        await JsonAsync(app, HttpStatusCode.Forbidden, writer.PostAsJsonAsync(LocksUri(s), LockBody(s, s.RowKeys[0])))
            .ConfigureAwait(true);

        var created = await JsonAsync(app, HttpStatusCode.OK, author.PostAsJsonAsync(LocksUri(s), LockBody(s, s.RowKeys[0])))
            .ConfigureAwait(true);
        var lockId = Assert.Single(created.EnumerateArray()).GetProperty("id").GetInt64();

        var again = await JsonAsync(app, HttpStatusCode.Conflict, author.PostAsJsonAsync(LocksUri(s), LockBody(s, s.RowKeys[0])))
            .ConfigureAwait(true);
        Assert.Equal("err.ECR-ROW-0409.rowAlreadyLocked", again.GetProperty("messageKey").GetString());

        using (var foreign = await writer.DeleteAsync(LockUri(s, lockId)).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        }

        var past = LockBody(s, s.RowKeys[1], fromDays: -5, toDays: -2);
        var invalid = await JsonAsync(app, HttpStatusCode.UnprocessableEntity, author.PostAsJsonAsync(LocksUri(s), past))
            .ConfigureAwait(true);
        Assert.Equal("err.ECR-REQ-0422.lockRangeInvalid", invalid.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75")]
    public async Task Режим_без_погодження_чернетці_200_опублікованій_з_документами_409_без_права_403()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var designer = await SignInAsync(app, s.Designer).ConfigureAwait(true);
        using var writer = await SignInAsync(app, s.Writer).ConfigureAwait(true);

        var ok = await JsonAsync(app, HttpStatusCode.OK, designer.PutAsJsonAsync(ModeUri(s.DraftVersionId), new { workflowMode = "None" }))
            .ConfigureAwait(true);
        Assert.Equal("None", ok.GetProperty("workflowMode").GetString());

        await using (var db = s.Builder.CreateContext())
        {
            Assert.Equal(
                WorkflowMode.None,
                await db.TemplateVersions.Where(v => v.Id == s.DraftVersionId).Select(v => v.WorkflowMode).SingleAsync().ConfigureAwait(true));
        }

        await JsonAsync(app, HttpStatusCode.Forbidden, writer.PutAsJsonAsync(ModeUri(s.DraftVersionId), new { workflowMode = "SubmitApprove" }))
            .ConfigureAwait(true);

        // Версія документа опублікована (і має документ) — правка відхиляється конфліктом.
        await JsonAsync(app, HttpStatusCode.Conflict, designer.PutAsJsonAsync(ModeUri(s.Document.TemplateVersionId), new { workflowMode = "None" }))
            .ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75")]
    public async Task Подання_версії_без_погодження_422_workflowDisabled_а_правка_проходить()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        await using (var db = s.Builder.CreateContext())
        {
            // ✎ F13 D1: перенос версії переносить wf.ApprovalState — старий Submitted аркуша версії None не замикає правку.
            var state = new Ecr.Domain.Entities.Workflow.ApprovalState(
                s.Document.DocumentId, s.Document.SheetDefId, s.PeriodKey);
            state.Submit(1, DateTime.UtcNow.AddDays(-1));
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync().ConfigureAwait(true);

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE cfg.TemplateVersion SET WorkflowMode = 1 WHERE Id = {s.Document.TemplateVersionId}").ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var author = await SignInAsync(app, s.Author).ConfigureAwait(true);

        await JsonAsync(app, HttpStatusCode.OK, PatchAsync(author, s, 0, 21m)).ConfigureAwait(true);

        // Картка документа несе режим версії (клієнт ховає подання) — одним читанням із розкладкою.
        var card = await JsonAsync(
                app, HttpStatusCode.OK,
                author.GetAsync(new Uri($"/api/v1/documents/{s.Document.DocumentId}?periodKey={s.PeriodKey}", UriKind.Relative)))
            .ConfigureAwait(true);
        Assert.Equal("None", card.GetProperty("workflowMode").GetString());

        // ✎ B147: старий «подано» для версії без погодження не діє — картка віддає аркуш чернеткою; перелік несе той самий
        // режим у рядку, фільтр `state=NoApproval` знаходить документ (а `state=Submitted` — ні), смуга рахує його окремо.
        Assert.Equal("Draft", card.GetProperty("sheets")[0].GetProperty("state").GetString());
        var listUri = $"/api/v1/documents?projectId={s.Document.ProjectId}&periodKey={s.PeriodKey}";
        var noApproval = await JsonAsync(
                app, HttpStatusCode.OK, author.GetAsync(new Uri(listUri + "&state=NoApproval", UriKind.Relative)))
            .ConfigureAwait(true);
        var row = Assert.Single(
            noApproval.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetInt64() == s.Document.DocumentId);
        Assert.Equal("None", row.GetProperty("workflowMode").GetString());
        var submittedList = await JsonAsync(
                app, HttpStatusCode.OK, author.GetAsync(new Uri(listUri + "&state=Submitted", UriKind.Relative)))
            .ConfigureAwait(true);
        Assert.DoesNotContain(
            submittedList.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetInt64() == s.Document.DocumentId);
        var strip = await JsonAsync(
                app, HttpStatusCode.OK,
                author.GetAsync(new Uri(
                    $"/api/v1/documents/summary?projectId={s.Document.ProjectId}&periodKey={s.PeriodKey}", UriKind.Relative)))
            .ConfigureAwait(true);
        Assert.Equal(noApproval.GetProperty("items").GetArrayLength(), strip.GetProperty("noApproval").GetInt32());
        Assert.Equal(0, strip.GetProperty("submitted").GetInt32());

        var submit = await JsonAsync(
                app, HttpStatusCode.UnprocessableEntity,
                author.PostAsJsonAsync(
                    new Uri($"/api/v1/documents/{s.Document.DocumentId}/submit", UriKind.Relative),
                    new { sheetDefId = s.Document.SheetDefId, periodKey = s.PeriodKey }))
            .ConfigureAwait(true);
        Assert.Equal("err.ECR-SUB-4221.workflowDisabled", submit.GetProperty("messageKey").GetString());

        var approve = await JsonAsync(
                app, HttpStatusCode.UnprocessableEntity,
                author.PostAsJsonAsync(
                    new Uri($"/api/v1/documents/{s.Document.DocumentId}/approve", UriKind.Relative),
                    new { sheetDefId = s.Document.SheetDefId, periodKey = s.PeriodKey, approved = true }))
            .ConfigureAwait(true);
        Assert.Equal("err.ECR-SUB-4221.workflowDisabled", approve.GetProperty("messageKey").GetString());
    }

    /// <remarks>
    /// ✎ F13 мутації M2/M10/M15: власник документа лише з Read не блокує (потрібен Write на таблицю); носій права із
    /// забороною на таблицю не блокує її рядки (404, таблиця невидима) і не бачить їхніх блокувань у переліку.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75")]
    public async Task Власник_лише_з_Read_і_носій_права_без_доступу_до_таблиці_не_блокують_і_не_бачать()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var author = await SignInAsync(app, s.Author).ConfigureAwait(true);
        using var reader = await SignInAsync(app, s.Reader).ConfigureAwait(true);
        using var blind = await SignInAsync(app, s.Blind).ConfigureAwait(true);

        await JsonAsync(app, HttpStatusCode.OK, author.PostAsJsonAsync(LocksUri(s), LockBody(s, s.RowKeys[0])))
            .ConfigureAwait(true);

        var hidden = await JsonAsync(app, HttpStatusCode.OK, blind.GetAsync(ListUri(s))).ConfigureAwait(true);
        Assert.Empty(hidden.EnumerateArray());
        await JsonAsync(app, HttpStatusCode.NotFound, blind.PostAsJsonAsync(LocksUri(s), LockBody(s, s.RowKeys[1])))
            .ConfigureAwait(true);

        await using (var db = s.Builder.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE doc.Document SET CreatedByUserId = {s.ReaderId} WHERE Id = {s.Document.DocumentId}").ConfigureAwait(true);
        }

        await JsonAsync(app, HttpStatusCode.Forbidden, reader.PostAsJsonAsync(LocksUri(s), LockBody(s, s.RowKeys[1])))
            .ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75")]
    public async Task Межі_дат_422_а_не_500()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var author = await SignInAsync(app, s.Author).ConfigureAwait(true);

        var body = new
        {
            periodKey = s.PeriodKey,
            sheetCode = s.Document.SheetCode,
            tableCode = s.TableCode,
            rowKeys = new[] { s.RowKeys[0] },
            fromDate = "2026-01-01",
            toDate = "9999-12-31",
        };
        var refused = await JsonAsync(app, HttpStatusCode.UnprocessableEntity, author.PostAsJsonAsync(LocksUri(s), body))
            .ConfigureAwait(true);
        Assert.Equal("err.ECR-REQ-0422.lockRangeInvalid", refused.GetProperty("messageKey").GetString());
    }

    /// <remarks>
    /// ✎ B143 (D-538): прапорець картки <c>canLockRows</c> — те саме рішення, що й <c>POST …/row-locks</c>
    /// (<c>RowLockRules.LockDenial</c>). Диференційна пара: для кожного актора прапорець дорівнює тому, чи дозволив
    /// сервер блокування. Власні дані, період 2099xx.
    /// ⛔ Доказ: прибрати заповнення <c>CanLockRows</c> у <c>GetDocumentHandler</c> — червоніють обидва тести B143.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B143")]
    public async Task Картка_canLockRows_дорівнює_рішенню_POST_для_кожного_актора()
    {
        var s = await ArrangeAsync(209903).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var author = await SignInAsync(app, s.Author).ConfigureAwait(true);
        using var writer = await SignInAsync(app, s.Writer).ConfigureAwait(true);
        using var holder = await SignInAsync(app, s.Holder).ConfigureAwait(true);
        using var reader = await SignInAsync(app, s.Reader).ConfigureAwait(true);
        using var blind = await SignInAsync(app, s.Blind).ConfigureAwait(true);

        // Прапорець читається ДО спроби блокування; дозволені актори блокують різні рядки (повтор дав би 409).
        async Task<(bool Flag, HttpStatusCode Post)> PairAsync(HttpClient client, int row)
        {
            var flag = await CanLockRowsAsync(app, client, s, s.PeriodKey).ConfigureAwait(true);
            Assert.True(flag.HasValue, "Картка з періодом мусить нести canLockRows.");
            using var post = await client.PostAsJsonAsync(LocksUri(s), LockBody(s, s.RowKeys[row])).ConfigureAwait(true);

            // Диференційна пара: прапорець істинний рівно тоді, коли сервер блокування дозволив.
            Assert.Equal(flag.Value, post.StatusCode == HttpStatusCode.OK);
            return (flag.Value, post.StatusCode);
        }

        // Власник документа без права Document.RowLock (Write на таблицю) — може.
        Assert.Equal((true, HttpStatusCode.OK), await PairAsync(author, 0).ConfigureAwait(true));

        // Не власник без права — ні, хоч би й з Write.
        Assert.Equal((false, HttpStatusCode.Forbidden), await PairAsync(writer, 1).ConfigureAwait(true));
        Assert.Equal((false, HttpStatusCode.Forbidden), await PairAsync(reader, 1).ConfigureAwait(true));

        // Носій права (лише Read) — може.
        Assert.Equal((true, HttpStatusCode.OK), await PairAsync(holder, 1).ConfigureAwait(true));

        // Носій права, якому єдина таблиця закрита, — ні (POST: таблиця невидима, 404).
        Assert.Equal((false, HttpStatusCode.NotFound), await PairAsync(blind, 1).ConfigureAwait(true));

        // Власність вирішує: власником стає читач (лише Read) — ні йому (немає Write), ні колишньому власникові.
        await using (var db = s.Builder.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE doc.RowLock SET ReleasedAt = SYSUTCDATETIME(), ReleasedByUserId = {s.ReaderId} WHERE DocumentId = {s.Document.DocumentId}")
                .ConfigureAwait(true);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE doc.Document SET CreatedByUserId = {s.ReaderId} WHERE Id = {s.Document.DocumentId}").ConfigureAwait(true);
        }

        Assert.Equal((false, HttpStatusCode.Forbidden), await PairAsync(reader, 0).ConfigureAwait(true));
        Assert.Equal((false, HttpStatusCode.Forbidden), await PairAsync(author, 0).ConfigureAwait(true));

        // …а власником стає писар (Write) — може.
        await using (var db = s.Builder.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE doc.Document SET CreatedByUserId = {s.WriterId} WHERE Id = {s.Document.DocumentId}").ConfigureAwait(true);
        }

        Assert.Equal((true, HttpStatusCode.OK), await PairAsync(writer, 0).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B143")]
    public async Task Картка_невидимого_документа_404_без_canLockRows_а_без_періоду_прапорця_немає()
    {
        var s = await ArrangeAsync(209904).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var author = await SignInAsync(app, s.Author).ConfigureAwait(true);
        using var outsider = await SignInAsync(app, s.Outsider).ConfigureAwait(true);

        // Чужинець має і Document.View, і Document.RowLock, але не бачить проєкту: картка — 404, прапорця в тілі немає.
        using (var hidden = await outsider.GetAsync(CardUri(s, s.PeriodKey)).ConfigureAwait(true))
        {
            var body = await hidden.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(hidden.StatusCode == HttpStatusCode.NotFound, $"{(int)hidden.StatusCode}\n{body}\n{app.ErrorsText}");
            Assert.DoesNotContain("canLockRows", body, StringComparison.OrdinalIgnoreCase);
        }

        await JsonAsync(app, HttpStatusCode.NotFound, outsider.PostAsJsonAsync(LocksUri(s), LockBody(s, s.RowKeys[0])))
            .ConfigureAwait(true);

        // Власник: з періодом — true; без періоду рішення немає (воно на документ × період) — прапорець не заповнено.
        Assert.True(await CanLockRowsAsync(app, author, s, s.PeriodKey).ConfigureAwait(true));
        Assert.Null(await CanLockRowsAsync(app, author, s, periodKey: null).ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private static Uri CardUri(Scenario s, int? periodKey)
        => new(
            periodKey is { } p
                ? $"/api/v1/documents/{s.Document.DocumentId}?periodKey={p}"
                : $"/api/v1/documents/{s.Document.DocumentId}",
            UriKind.Relative);

    /// <summary>Прапорець <c>canLockRows</c> картки; <c>null</c> — поля немає або воно <c>null</c>.</summary>
    private static async Task<bool?> CanLockRowsAsync(EcrApiFactory app, HttpClient client, Scenario s, int? periodKey)
    {
        var card = await JsonAsync(app, HttpStatusCode.OK, client.GetAsync(CardUri(s, periodKey))).ConfigureAwait(false);
        return card.TryGetProperty("canLockRows", out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetBoolean()
            : null;
    }

    private sealed record Scenario(
        TestDocumentBuilder Builder,
        TestDocument Document,
        string Author,
        string Writer,
        string Holder,
        string Reader,
        string Designer,
        IReadOnlyList<string> RowKeys,
        string ColumnCode,
        string TableCode,
        int PeriodKey,
        int DraftVersionId)
    {
        /// <summary>Носій права Document.RowLock із Read на проєкт, але з забороною на таблицю.</summary>
        public string Blind { get; init; } = string.Empty;

        public int ReaderId { get; init; }

        public int WriterId { get; init; }

        /// <summary>✎ B143 (D-538): має Document.View і Document.RowLock, але жодного доступу до проєкту документа.</summary>
        public string Outsider { get; init; } = string.Empty;
    }

    private static Uri LocksUri(Scenario s) => new($"/api/v1/documents/{s.Document.DocumentId}/row-locks", UriKind.Relative);

    private static Uri ListUri(Scenario s)
        => new($"/api/v1/documents/{s.Document.DocumentId}/row-locks?periodKey={s.PeriodKey}", UriKind.Relative);

    private static Uri LockUri(Scenario s, long lockId)
        => new($"/api/v1/documents/{s.Document.DocumentId}/row-locks/{lockId}", UriKind.Relative);

    private static Uri ModeUri(int versionId) => new($"/api/v1/template-versions/{versionId}/workflow-mode", UriKind.Relative);

    private static object LockBody(Scenario s, string rowKey, int fromDays = 0, int toDays = 5)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        return new
        {
            periodKey = s.PeriodKey,
            sheetCopyId = (long?)null,
            sheetCode = s.Document.SheetCode,
            tableCode = s.TableCode,
            rowKeys = new[] { rowKey },
            fromDate = today.AddDays(fromDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            toDate = today.AddDays(toDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            note = "B75 test",
        };
    }

    private static async Task<JsonElement> JsonAsync(EcrApiFactory app, HttpStatusCode expected, Task<HttpResponseMessage> call)
    {
        using var response = await call.ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} замість {(int)expected}\n{body}\n{app.ErrorsText}");
        return string.IsNullOrWhiteSpace(body) ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Правка числової колонки рядка <paramref name="row"/> з актуальною версією рядка.</summary>
    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, Scenario s, int row, decimal value)
    {
        using var table = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Document.DocumentId.ToString(CultureInfo.InvariantCulture)}/tables/{s.Document.TableInstanceId.ToString(CultureInfo.InvariantCulture)}",
            UriKind.Relative)).ConfigureAwait(false);
        var text = await table.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(table.IsSuccessStatusCode, $"GET зрізу: {table.StatusCode}\n{text}");

        var found = JsonDocument.Parse(text).RootElement.GetProperty("rows").EnumerateArray()
            .First(r => r.GetProperty("rowKey").GetString() == s.RowKeys[row]);

        return await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId.ToString(CultureInfo.InvariantCulture)}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.Document.TableInstanceId,
                periodKey = s.PeriodKey,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey = s.RowKeys[row],
                        baseVersion = found.GetProperty("rowVersion").GetString(),
                        cells = new object[] { new { columnCode = s.ColumnCode, value = (object)value.ToString(CultureInfo.InvariantCulture) } },
                    },
                },
            }).ConfigureAwait(false);
    }

    private async Task<List<string>> AuditOperationsAsync(long lockId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var id = checked((int)lockId);
        return await db.Database
            .SqlQuery<string>($"SELECT Operation AS Value FROM aud.StructureChange WHERE EntityType = N'RowLock' AND EntityId = {id}")
            .ToListAsync().ConfigureAwait(false);
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
    /// Документ у відкритому поточному періоді активного проєкту: автор (власник, Write), писар (Write), носій права
    /// <c>Document.RowLock</c> (Read), читач (Read), проєктувальник (<c>Template.Edit</c>) і чернетка версії шаблону.
    /// </summary>
    /// <param name="ownPeriod">✎ B143 (D-538): власний період тесту (2099xx); <c>null</c> — поточний місяць майданчика.</param>
    private async Task<Scenario> ArrangeAsync(int? ownPeriod = null)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var periodKey = ownPeriod ?? ((siteToday.Year * 100) + siteToday.Month);
        var document = await builder.BuildAsync(periodKey, columnCount: 3, rowCount: 2).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));

        async Task<(int Id, string Name)> UserAsync(string prefix, GrantLevel? level, params string[] permissions)
            => await UserDenyAsync(prefix, level, denyTable: false, permissions).ConfigureAwait(false);

        async Task<(int Id, string Name)> UserDenyAsync(
            string prefix, GrantLevel? level, bool denyTable, params string[] permissions)
        {
            var name = $"{prefix}_{Guid.NewGuid():N}"[..20];
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(
                EcrCode.Create($"B75{Guid.NewGuid():N}"[..14]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "B75 row lock" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            if (level is { } l)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, l));
            }

            if (denyTable)
            {
                db.ResourceGrants.Add(new ResourceGrant(
                    role.Id, ResourceKind.Table, document.TableDefId, GrantLevel.None, isDeny: true));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
            return (user.Id, name);
        }

        var author = await UserAsync("b75a", GrantLevel.Write, "Document.View").ConfigureAwait(false);
        var writer = await UserAsync("b75w", GrantLevel.Write, "Document.View").ConfigureAwait(false);
        var holder = await UserAsync("b75h", GrantLevel.Read, "Document.View", "Document.RowLock").ConfigureAwait(false);
        var reader = await UserAsync("b75r", GrantLevel.Read, "Document.View").ConfigureAwait(false);
        var designer = await UserAsync("b75d", null, "Template.View", "Template.Edit").ConfigureAwait(false);
        var blind = await UserDenyAsync("b75x", GrantLevel.Read, denyTable: true, "Document.View", "Document.RowLock")
            .ConfigureAwait(false);
        var outsider = await UserAsync("b143o", null, "Document.View", "Document.RowLock").ConfigureAwait(false);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.Document SET CreatedByUserId = {author.Id} WHERE Id = {document.DocumentId}").ConfigureAwait(false);

        var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(now);
        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey).ConfigureAwait(false);
        period.RecomputeBoundaries(policy, SiteZone);
        period.AdvanceTo(PeriodState.Open, now);

        var draft = new TemplateVersion(document.TemplateId, $"9.{Guid.NewGuid():N}"[..12], author.Id, now);
        db.TemplateVersions.Add(draft);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var columnCode = await db.ColumnDefs.AsNoTracking()
            .Where(c => c.Id == document.ColumnDefIds[1]).Select(c => c.Code).SingleAsync().ConfigureAwait(false);
        var tableCode = await db.TableDefs.AsNoTracking()
            .Where(t => t.Id == document.TableDefId).Select(t => t.Code).SingleAsync().ConfigureAwait(false);

        var rowKeys = new List<string>();
        foreach (var rowId in document.RowIds)
        {
            rowKeys.Add(await db.TableRows.AsNoTracking()
                .Where(r => r.Id == rowId).Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(false));
        }

        return new Scenario(
            builder, document, author.Name, writer.Name, holder.Name, reader.Name, designer.Name, rowKeys, columnCode,
            tableCode, periodKey, draft.Id)
        {
            Blind = blind.Name,
            ReaderId = reader.Id,
            WriterId = writer.Id,
            Outsider = outsider.Name,
        };
    }
}
