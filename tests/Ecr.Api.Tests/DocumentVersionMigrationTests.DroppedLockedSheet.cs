// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.DroppedLockedSheet.cs
using System.Net;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// B181 (AU F13, мутація H8): затверджений аркуш, якого цільова версія НЕ МАЄ, — це зачеплений аркуш: перенос
/// відмовляє <c>409 ECR-DOC-0409</c> (<c>migrateSheetsLocked</c>) і нічого не змінює — ні версії документа, ні
/// стану погодження, ні комірок/рядків.
/// </summary>
/// <remarks>
/// ⚠ Справжнім HTTP на справжньому SQL Server. Аркуш документа (S1) план НЕ зачіпає (лише підписи) і не поданий;
/// затверджений — лише другий аркуш S2, якого в цілі немає. Два варіанти:
/// <list type="bullet">
/// <item><c>sheetLiveInSource = true</c> — S2 живий у вихідній версії: планувальник сам кладе його в
/// <c>TouchedSheets</c> (прибраний аркуш).</item>
/// <item><c>sheetLiveInSource = false</c> — S2 у вихідній версії м'яко видалений: планувальник його не бачить
/// (<c>Live(source.Sheets)</c>), тож у <c>TouchedSheets</c> його немає, і відмову дає ЛИШЕ умова
/// <c>!plan.Sheets.ContainsKey(l.SheetDefId)</c> у <c>MigrateDocumentVersionHandler.ClassifyLocked</c>.</item>
/// </list>
/// Мутаційний доказ H8: прибрати <c>!plan.Sheets.ContainsKey(l.SheetDefId) ||</c> — червоніє варіант <c>false</c>
/// (перенос проходить, затверджений стан осиротів би на аркуші, якого в новій версії немає).
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    [Trait("Task", "B181")]
    public async Task Затверджений_аркуш_якого_немає_в_цільовій_версії_дає_409_і_нічого_не_змінюється(bool sheetLiveInSource)
    {
        // S1 (аркуш документа) не поданий, план його не зачіпає (лише підписи колонок).
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var now = new DateTime(2026, 1, 16, 9, 0, 0, DateTimeKind.Utc);
        var s2Code = $"S2_{s.Tag}";
        int s2Id;

        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            // S2 — другий аркуш ВИХІДНОЇ версії; ціль (ArrangeAsync) має лише S1.
            var s2 = new SheetDef(s.Doc.TemplateVersionId, EcrCode.Create(s2Code), Name($"Sheet 2 {s.Tag}"), 2);
            db.SheetDefs.Add(s2);
            await db.SaveChangesAsync().ConfigureAwait(true);
            s2Id = s2.Id;

            if (!sheetLiveInSource)
            {
                s2.SoftDelete(1, now);
            }

            var document = await db.Documents.Include(d => d.Sheets).SingleAsync(d => d.Id == s.Doc.DocumentId).ConfigureAwait(true);
            document.IncludeSheet(s2Id);

            var state = new ApprovalState(s.Doc.DocumentId, s2Id, s.Doc.PeriodKey.Value);
            state.Submit(1, now);
            state.Approve(1, now.AddMinutes(5));
            db.ApprovalStates.Add(state);
            db.ApprovalEvents.Add(new ApprovalEvent(
                s.Doc.DocumentId, s2Id, s.Doc.PeriodKey.Value, DocumentStatus.Draft, DocumentStatus.Submitted,
                ApprovalAction.Submit, 1, now));
            db.ApprovalEvents.Add(new ApprovalEvent(
                s.Doc.DocumentId, s2Id, s.Doc.PeriodKey.Value, DocumentStatus.Submitted, DocumentStatus.Approved,
                ApprovalAction.Approve, 1, now.AddMinutes(5)));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var before = await SnapshotAsync(s).ConfigureAwait(true);
        var stateBefore = await ApprovalStatesTextAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true);
        var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody} {app.ErrorsText}");
        var report = JsonDocument.Parse(dryBody).RootElement;
        Assert.False(report.GetProperty("canApply").GetBoolean(), dryBody);
        Assert.Contains("sheetsLocked", report.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(1, report.GetProperty("lockedSheetsAffected").GetInt32());
        Assert.Equal(0, report.GetProperty("lockedSheetsCarried").GetInt32());

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        var problem = await AssertProblemAsync(
            response, HttpStatusCode.Conflict, "ECR-DOC-0409", "err.ECR-DOC-0409.migrateSheetsLocked").ConfigureAwait(true);

        var root = JsonDocument.Parse(problem).RootElement;
        Assert.Equal("1", root.GetProperty("lockedSheetsAffected").GetString());
        var listed = Assert.Single(root.GetProperty("lockedSheetItems").EnumerateArray().Select(i => i.GetString()));
        // М'яко видаленого аркуша знімок версії не містить — звіт називає його за Id («#<id>»).
        var listedSheet = sheetLiveInSource ? s2Code : $"#{s2Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        Assert.Equal($"{listedSheet}|{s.Doc.PeriodKey.Value}|Approved", listed);

        // Нічого не змінилося: версія проєкту, склад документа, комірки, рядки, екземпляр таблиці…
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
        Assert.Contains($"v{s.Doc.TemplateVersionId}|", before, StringComparison.Ordinal);

        // …і стан погодження S2 — той самий рядок (Approved, на S2, з тими самими мітками часу).
        Assert.Equal(stateBefore, await ApprovalStatesTextAsync(s).ConfigureAwait(true));
        Assert.Contains($"{s2Id}/{s.Doc.PeriodKey.Value}/{DocumentStatus.Approved}", stateBefore, StringComparison.Ordinal);
    }

    private async Task<string> ApprovalStatesTextAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var states = await db.ApprovalStates.AsNoTracking()
            .Where(a => a.DocumentId == s.Doc.DocumentId)
            .OrderBy(a => a.SheetDefId).ThenBy(a => a.PeriodKey)
            .Select(a => $"{a.SheetDefId}/{a.PeriodKey}/{a.Status}/{a.SubmittedAt}/{a.ApprovedAt}")
            .ToListAsync().ConfigureAwait(false);
        var events = await db.ApprovalEvents.AsNoTracking()
            .CountAsync(e => e.DocumentId == s.Doc.DocumentId).ConfigureAwait(false);
        return $"{string.Join(";", states)}|events={events}";
    }
}
