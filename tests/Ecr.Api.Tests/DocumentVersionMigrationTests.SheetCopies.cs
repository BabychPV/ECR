// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.SheetCopies.cs
using System.Globalization;
using System.Net;
using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-6 (+C-2, аудит 2026-11): аркуш вихідної версії, якого цільова версія не має, з копіями (RS1). Перенос мусить
/// відмовити НА ЕТАПІ ПЛАНУ (<c>sheetCopiesNotMapped</c>), до маркера «перенос у процесі».
/// </summary>
/// <remarks>
/// Червоне до фіксу (крок відмови в <c>MigrateDocumentVersionHandler.PlanAsync</c> прибрано): сухий прогін
/// <c>canApply = true</c> без причини, застосування — 500 (<c>SqlException 547</c> на <c>FK_TableInstance_SheetCopy</c>
/// у <c>WorkflowSql</c>: склад документа видаляє і копію, а її екземпляри таблиць ще живі), маркер <c>migratingTo</c>
/// лишається, і проєкт застрягає. Копія порожня навмисно: копія завжди має власні екземпляри таблиць, тож 547 дає
/// будь-яка копія, а Safe із даними в копії відмовив би раніше (<c>dataLoss</c>) — режиму, що допускає втрату, немає.
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-6")]
    public async Task Removed_sheet_with_copies_is_refused_before_marker()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var removed = await AddCopyableSheetAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var copyId = await AddCopyAsync(client, app, s, removed).ConfigureAwait(true);
        await MaterializeTablesAsync(client, s).ConfigureAwait(true);

        // Передумова: у копії є власний екземпляр таблиці — саме він тримає FK_TableInstance_SheetCopy.
        await using (var check = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            Assert.True(
                await check.TableInstances.AsNoTracking().AnyAsync(i => i.DocumentId == s.Doc.DocumentId && i.SheetCopyId == copyId).ConfigureAwait(true),
                "копія без екземпляра таблиці — сценарій не відтворює D-6");
        }

        var before = await SnapshotAsync(s).ConfigureAwait(true);

        var dry = await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true);
        var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody} {app.ErrorsText}");
        var report = JsonDocument.Parse(dryBody).RootElement;
        Assert.False(report.GetProperty("canApply").GetBoolean(), dryBody);
        Assert.Contains("sheetCopiesNotMapped", report.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));

        var applied = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        await AssertProblemAsync(
            applied, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateSheetCopiesNotMapped").ConfigureAwait(true);

        // ⛔ Предмет тесту: відмова нічого не лишила — ні маркера, ні нової версії, ні зміненого складу/комірок.
        Assert.False(await HasMigrationMarkerAsync(s.Doc.ProjectId).ConfigureAwait(true), "маркер migratingTo лишився — проєкт застряг");
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
        await using var after = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.True(await after.DocumentSheets.AsNoTracking().AnyAsync(x => x.Id == copyId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-6")]
    public async Task Removed_sheet_without_copies_still_migrates()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var removed = await AddCopyableSheetAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        await MaterializeTablesAsync(client, s).ConfigureAwait(true);

        var dry = await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true);
        var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody} {app.ErrorsText}");
        Assert.DoesNotContain("sheetCopiesNotMapped", JsonDocument.Parse(dryBody).RootElement.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));

        var applied = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        var body = await applied.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(applied.StatusCode == HttpStatusCode.OK, $"{applied.StatusCode}: {body} {app.ErrorsText}");

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.Equal(s.TargetVersionId, await db.Projects.AsNoTracking().Where(p => p.Id == s.Doc.ProjectId).Select(p => p.TemplateVersionId).SingleAsync().ConfigureAwait(true));
        Assert.False(await HasMigrationMarkerAsync(s.Doc.ProjectId).ConfigureAwait(true));

        // Прибраний аркуш зник зі складу разом зі своїм екземпляром таблиці; лишився аркуш-відповідник.
        Assert.Equal(
            [s.TargetSheetDefId],
            await db.DocumentSheets.AsNoTracking().Where(x => x.DocumentId == s.Doc.DocumentId).Select(x => x.SheetDefId).ToListAsync().ConfigureAwait(true));
        Assert.False(await db.TableInstances.AsNoTracking().AnyAsync(i => i.DocumentId == s.Doc.DocumentId && i.TableDefId == removed.TableDefId).ConfigureAwait(true));
    }

    /// <summary>Другий аркуш вихідної версії (копіюваний, одна таблиця без рядків), включений у документ; у цілі його немає.</summary>
    private async Task<(int SheetDefId, int TableDefId)> AddCopyableSheetAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var sheet = new SheetDef(s.Doc.TemplateVersionId, EcrCode.Create("SC" + s.Tag), Name($"Copies {s.Tag}"), 2);
        sheet.SetCopyable(true);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var table = new TableDef(
            sheet.Id, EcrCode.Create("TC" + s.Tag), Name($"Copy table {s.Tag}"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.ColumnDefs.Add(new ColumnDef(table.Id, EcrCode.Create("CC_" + s.Tag), Name("Copy column"), 1, CellDataType.Decimal));

        var document = await db.Documents.Include(d => d.Sheets).SingleAsync(d => d.Id == s.Doc.DocumentId).ConfigureAwait(false);
        document.IncludeSheet(sheet.Id);

        // Копії додає Document.Create, таблиці читає Document.View — окрема роль із грантом Write на проєкт.
        var role = new Role(EcrCode.Create($"MIGR_{Guid.NewGuid():N}"), Name("Sheet copies"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.RolePermissions.Add(new RolePermission(role.Id, DocumentSheetCopyHandler.Permission));
        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, s.UserId, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, s.Doc.ProjectId, GrantLevel.Write));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (sheet.Id, table.Id);
    }

    /// <summary>Копія аркуша через API копій (як у <c>DocumentSheetCopyApiTests</c>).</summary>
    private static async Task<long> AddCopyAsync(HttpClient client, EcrApiFactory app, Scenario s, (int SheetDefId, int TableDefId) sheet)
    {
        var response = await client.PostAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/sheets/{sheet.SheetDefId.ToString(CultureInfo.InvariantCulture)}/copies", UriKind.Relative),
            null).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{response.StatusCode}: {body} {app.ErrorsText}");

        return JsonDocument.Parse(body).RootElement.GetProperty("sheetCopyId").GetInt64();
    }

    /// <summary>Таблиці документа за період: екземпляри таблиць (і копій) заводяться при читанні.</summary>
    private static async Task MaterializeTablesAsync(HttpClient client, Scenario s)
    {
        var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/tables?periodKey={s.Doc.PeriodKey.Value.ToString(CultureInfo.InvariantCulture)}",
            UriKind.Relative)).ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync().ConfigureAwait(false)}");
    }

    private async Task<bool> HasMigrationMarkerAsync(int projectId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS Value FROM doc.Project
                WHERE  Id = {projectId} AND ISJSON(ExternalSettingsJson) = 1
                  AND  JSON_VALUE(ExternalSettingsJson, '$.migratingTo') IS NOT NULL
                """).SingleAsync().ConfigureAwait(false) > 0;
    }
}
