// tests/Ecr.Api.Tests/DocumentCalcStatusApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Integration;
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
/// ✎ D-577 (UX-огляд екрана документа, L3): <c>GET /documents/{id}/calc-status</c> і Q3 у картці — наскрізно через HTTP.
/// </summary>
/// <remarks>
/// Мутації (локально, не комітити):
/// <list type="bullet">
///   <item><c>DocumentStaleMonths.LockedReason</c>: прибрати гілку <c>Closed</c> (повертати <c>null</c>) —
///   <c>L3_1_закритий_застарілий_місяць_річного_документа_довідка_а_не_застарілість</c> червоніє (закритий січень «перерахується»,
///   <c>resultsStale = true</c>);</item>
///   <item><c>GetDocumentHandler</c>: прибрати блок D-577 (Q3) — той самий тест червоніє на картці (<c>resultsStale = true</c>);</item>
///   <item><c>DocumentCalcStatusStore.ActiveAsync</c>: фільтр <c>p.State == Queued || p.State == Running</c> замінити на
///   <c>p.State == Running</c> — <c>L3_3_задача_в_черзі_й_провал_видно_з_сервера</c> червоніє.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentCalcStatusApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Calc-Status-2026!";
    private const int Anchor = 202601;
    private const int Month = 202609;
    private static readonly DateTime RunAt = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-577")]
    public async Task L3_1_закритий_застарілий_місяць_річного_документа_довідка_а_не_застарілість()
    {
        var s = await ArrangeAsync(yearLayout: true).ConfigureAwait(true);
        try
        {
            await using (var db = s.Builder.CreateContext())
            {
                var version = await VersionAsync(db).ConfigureAwait(true);
                await SetStateAsync(db, s.Document, Anchor, PeriodState.Closed).ConfigureAwait(true);
                await SetStateAsync(db, s.Document, Month, PeriodState.Open).ConfigureAwait(true);
                await MonthRunAsync(db, s.Document, version, Anchor, RunAt).ConfigureAwait(true);
                await MonthRunAsync(db, s.Document, version, Month, RunAt).ConfigureAwait(true);
                await EditAsync(db, s.Document, Anchor, RunAt.AddHours(1)).ConfigureAwait(true);
                await EditAsync(db, s.Document, Month, RunAt.AddHours(2)).ConfigureAwait(true);
            }

            using var app = new EcrApiFactory(sql);
            using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

            // Обидва місяці застарілі: вересень відкритий — «застаріло», січень закритий — довідка.
            var both = await StatusAsync(client, s.Document.DocumentId, Anchor).ConfigureAwait(true);
            Assert.Equal(2026, both.GetProperty("year").GetInt32());
            Assert.True(both.GetProperty("resultsStale").GetBoolean(), both.ToString());
            var months = both.GetProperty("staleMonths").EnumerateArray().ToList();
            Assert.Equal(2, months.Count);
            Assert.Equal(Anchor, months[0].GetProperty("periodKey").GetInt32());
            Assert.False(months[0].GetProperty("recalculable").GetBoolean());
            Assert.Equal("closed", months[0].GetProperty("lockedReason").GetString());
            Assert.Equal(Month, months[1].GetProperty("periodKey").GetInt32());
            Assert.True(months[1].GetProperty("recalculable").GetBoolean());
            Assert.Equal(JsonValueKind.Null, months[1].GetProperty("lockedReason").ValueKind);

            // Вересень перераховано (новіший прогін) — лишився лише закритий січень: не застаріло, але довідка є.
            await using (var db = s.Builder.CreateContext())
            {
                var version = await VersionAsync(db).ConfigureAwait(true);
                await MonthRunAsync(db, s.Document, version, Month, RunAt.AddHours(3)).ConfigureAwait(true);
            }

            var closedOnly = await StatusAsync(client, s.Document.DocumentId, Anchor).ConfigureAwait(true);
            Assert.False(closedOnly.GetProperty("resultsStale").GetBoolean(), closedOnly.ToString());
            Assert.Equal(JsonValueKind.Null, closedOnly.GetProperty("resultsStaleSince").ValueKind);
            var note = Assert.Single(closedOnly.GetProperty("staleMonths").EnumerateArray());
            Assert.Equal("closed", note.GetProperty("lockedReason").GetString());
            Assert.Equal(JsonValueKind.String, closedOnly.GetProperty("calculatedAt").ValueKind);

            // BUG-2: банер картки (resultsStale) після перерахунку відкритих місяців зникає.
            var card = await client.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/v1/documents/{Num(s.Document.DocumentId)}?periodKey={Num(Anchor)}", UriKind.Relative)).ConfigureAwait(true);
            Assert.False(card.GetProperty("resultsStale").GetBoolean(), card.ToString());
        }
        finally
        {
            await ReleaseAsync(s).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-577")]
    public async Task L3_3_задача_в_черзі_й_провал_видно_з_сервера()
    {
        var s = await ArrangeAsync(yearLayout: false).ConfigureAwait(true);
        var period = s.Document.PeriodKey.Value;
        var queuedId = $"IRecalculationJob~doc{Num(s.Document.DocumentId)}-p{Num(period)}~{Guid.NewGuid():N}";
        var failedId = $"IRecalculationJob~doc{Num(s.Document.DocumentId)}-p{Num(period)}-auto~{Guid.NewGuid():N}";
        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

            var idle = await StatusAsync(client, s.Document.DocumentId, period).ConfigureAwait(true);
            Assert.Empty(idle.GetProperty("activeJobs").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, idle.GetProperty("lastFailure").ValueKind);
            Assert.Equal(JsonValueKind.Null, idle.GetProperty("year").ValueKind);

            await using (var db = s.Builder.CreateContext())
            {
                var now = DateTime.UtcNow;
                var failed = new JobProgress(failedId, JobListKinds.RecalculationJobCode, now.AddMinutes(-5), createdByUserId: null);
                failed.Queue(now.AddMinutes(-5), "corr-failed", s.Document.DocumentId);
                failed.Begin(now.AddMinutes(-4));
                failed.Finish("Failed", "boom", now.AddMinutes(-3), "ECR-SYS-0500");
                db.JobProgresses.Add(failed);

                var queued = new JobProgress(queuedId, JobListKinds.RecalculationJobCode, now, s.UserId);
                queued.Queue(now, "corr-queued", s.Document.DocumentId);
                db.JobProgresses.Add(queued);
                await db.SaveChangesAsync().ConfigureAwait(true);
            }

            var busy = await StatusAsync(client, s.Document.DocumentId, period).ConfigureAwait(true);
            var job = Assert.Single(busy.GetProperty("activeJobs").EnumerateArray());
            Assert.Equal(queuedId, job.GetProperty("jobId").GetString());
            Assert.Equal("Queued", job.GetProperty("state").GetString());
            Assert.Equal("manual", job.GetProperty("kind").GetString());
            Assert.True(job.GetProperty("own").GetBoolean());
            Assert.Equal(s.UserName, job.GetProperty("startedByDisplayName").GetString());

            var failure = busy.GetProperty("lastFailure");
            Assert.Equal(failedId, failure.GetProperty("jobId").GetString());
            Assert.Equal("ECR-SYS-0500", failure.GetProperty("errorCode").GetString());
            Assert.Equal("corr-failed", failure.GetProperty("correlationId").GetString());
            Assert.Equal("auto", failure.GetProperty("kind").GetString());
            Assert.False(failure.GetProperty("own").GetBoolean());

            // Невидимий документ — 404 (як картка).
            var missing = await client.GetAsync(new Uri(
                $"/api/v1/documents/{Num(s.Document.DocumentId + 100_000)}/calc-status?periodKey={Num(period)}", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            await using var db = s.Builder.CreateContext();
            await db.JobProgresses.Where(p => p.JobId == queuedId || p.JobId == failedId).ExecuteDeleteAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// ✎ D-577 (огляд «тестові прогалини»): читач, якому в проєкті документа сховано колонку, застарілості не отримує
    /// (<c>resultsStale = null</c>, причина <c>PartiallyHidden</c>), а ім'я автора задачі — теж; стан задачі лишається.
    /// </summary>
    /// <remarks>Мутація (локально): у <c>GetDocumentCalcStatusHandler</c> <c>hidden</c> → <c>false</c> — червоний.</remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-577")]
    public async Task L3_звужений_читач_без_застарілості_й_без_імені_автора()
    {
        var s = await ArrangeAsync(yearLayout: false).ConfigureAwait(true);
        var period = s.Document.PeriodKey.Value;
        var queuedId = $"IRecalculationJob~doc{Num(s.Document.DocumentId)}-p{Num(period)}~{Guid.NewGuid():N}";
        try
        {
            await using (var db = s.Builder.CreateContext())
            {
                var version = await VersionAsync(db).ConfigureAwait(true);
                await MonthRunAsync(db, s.Document, version, period, RunAt).ConfigureAwait(true);
                await EditAsync(db, s.Document, period, RunAt.AddHours(1)).ConfigureAwait(true);

                var role = await db.RoleAssignments.Where(a => a.UserId == s.UserId).Select(a => a.RoleId).FirstAsync().ConfigureAwait(true);
                db.ResourceGrants.Add(new ResourceGrant(role, ResourceKind.Column, s.Document.ColumnDefIds[0], GrantLevel.Read, isDeny: true));
                var queued = new JobProgress(queuedId, JobListKinds.RecalculationJobCode, DateTime.UtcNow, s.UserId);
                queued.Queue(DateTime.UtcNow, "corr-hidden", s.Document.DocumentId);
                db.JobProgresses.Add(queued);
                await db.SaveChangesAsync().ConfigureAwait(true);
            }

            using var app = new EcrApiFactory(sql);
            using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

            var status = await StatusAsync(client, s.Document.DocumentId, period).ConfigureAwait(true);
            Assert.Equal(JsonValueKind.Null, status.GetProperty("resultsStale").ValueKind);
            Assert.Equal("PartiallyHidden", status.GetProperty("resultsStaleUndetermined").GetString());
            Assert.Empty(status.GetProperty("staleMonths").EnumerateArray());
            var job = Assert.Single(status.GetProperty("activeJobs").EnumerateArray());
            Assert.Equal("Queued", job.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, job.GetProperty("startedByDisplayName").ValueKind);
        }
        finally
        {
            await using var db = s.Builder.CreateContext();
            await db.JobProgresses.Where(p => p.JobId == queuedId).ExecuteDeleteAsync().ConfigureAwait(true);
            await ReleaseAsync(s).ConfigureAwait(true);
        }
    }

    private static async Task<JsonElement> StatusAsync(HttpClient client, long documentId, int periodKey)
    {
        var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{Num(documentId)}/calc-status?periodKey={Num(periodKey)}", UriKind.Relative)).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<Scenario> ArrangeAsync(bool yearLayout)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: Anchor).ConfigureAwait(true);
        await builder.AddPeriodAsync(document, new PeriodKey(Month)).ConfigureAwait(true);

        await using var db = builder.CreateContext();
        if (yearLayout)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE cfg.TemplateVersion SET Layout = {(int)TemplateLayout.YearWithMonthColumns} WHERE Id = {document.TemplateVersionId};")
                .ConfigureAwait(true);
        }

        await SetStateAsync(db, document, Anchor, PeriodState.Open).ConfigureAwait(true);

        var userName = $"calcst_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(
            EcrCode.Create($"CALCST_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Calc status" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(true);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Write));
        await db.SaveChangesAsync().ConfigureAwait(true);

        return new Scenario(builder, document, userName, user.Id);
    }

    private static async Task SetStateAsync(EcrDbContext db, TestDocument document, int periodKey, PeriodState state)
    {
        var period = await db.Periods
            .SingleAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey).ConfigureAwait(true);
        period.AdvanceTo(state, DateTime.UtcNow);
        await db.SaveChangesAsync().ConfigureAwait(true);
    }

    private static async Task<int> VersionAsync(EcrDbContext db)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var methodology = new Methodology(
            EcrCode.Create($"CS_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(true);

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, RunAt);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(true);
        return version.Id;
    }

    /// <summary>Актуальний прогін документа за місяць з одним числом (попередній актуальний того самого місяця — знято).</summary>
    private static async Task MonthRunAsync(EcrDbContext db, TestDocument document, int versionId, int periodKey, DateTime startedAt)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE calc.CalculationRun SET Status = N'Superseded' WHERE ProjectId = {document.ProjectId} AND PeriodKey = {periodKey} AND Status = N'Current';")
            .ConfigureAwait(true);

        var run = new CalculationRun(document.ProjectId, periodKey, triggeredByUserId: null, startedAt, document.DocumentId);
        run.Complete("Succeeded", startedAt.AddMinutes(1), "{}", errorMessage: null);
        run.MakeCurrent();
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync().ConfigureAwait(true);

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync().ConfigureAwait(true);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO calc.CalculationResult
                (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
            VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {versionId},
                    {periodKey}, {document.DocumentId}, N'row-1', N'E_CO2', CAST(1 AS decimal(34,16)), {unitId})
            """).ConfigureAwait(true);
    }

    private static Task<int> EditAsync(EcrDbContext db, TestDocument document, int periodKey, DateTime changedAt)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                 OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            VALUES ({changedAt}, {periodKey}, {document.DocumentId}, 1, N'R1', {document.ColumnDefIds[0]}, N'1', N'2', 1, N'UserEdit', 0);
            """);

    /// <summary>
    /// Ізоляція колекції «SqlServer»: актуальні прогони тесту знімаються з актуальних (прогрів карти застарілості бере
    /// два найновіші періоди з актуальним прогоном по всій базі — див. <c>DocumentStaleResultsStoreTests.ReleaseMonthRunsAsync</c>).
    /// </summary>
    private static async Task ReleaseAsync(Scenario s)
    {
        await using var db = s.Builder.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE calc.CalculationRun SET Status = N'Superseded' WHERE ProjectId = {s.Document.ProjectId} AND Status = N'Current';")
            .ConfigureAwait(true);
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed record Scenario(TestDocumentBuilder Builder, TestDocument Document, string UserName, int UserId);
}
