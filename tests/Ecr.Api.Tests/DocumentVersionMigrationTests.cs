// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Documents.VersionMigration;
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
/// <c>POST /api/v1/documents/{id}/migrate-version</c> — перенос документа на нову
/// версію шаблону (ФВ-7.5), справжнім HTTP на справжньому SQL Server.
/// </summary>
/// <remarks>
/// Документ будівника: три колонки (<c>C1</c> текст, <c>C2</c>/<c>C3</c> числа),
/// два рядки, значення «a» в <c>C1</c> і 42 в <c>C2</c> першого рядка, порожня
/// комірка в <c>C3</c>. Цільова версія будується тут же — з тими самими кодами,
/// крім того, що змінює сценарій.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentVersionMigrationTests(SqlServerFixture sql)
{
    private const string Password = "Api-Doc-Migrate-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Сухий_прогін_звітує_і_нічого_не_змінює()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");

        var report = JsonDocument.Parse(body).RootElement;
        Assert.True(report.GetProperty("dryRun").GetBoolean());
        Assert.False(report.GetProperty("applied").GetBoolean());
        Assert.True(report.GetProperty("canApply").GetBoolean(), body);
        Assert.Equal(2, report.GetProperty("transferredValues").GetInt64());
        Assert.Equal(0, report.GetProperty("lostValues").GetInt64());
        Assert.Equal(1, report.GetProperty("documentCount").GetInt32());

        var kinds = report.GetProperty("items").EnumerateArray()
            .Select(i => $"{i.GetProperty("kind").GetString()}:{i.GetProperty("path").GetString()}")
            .ToList();
        Assert.Contains(kinds, k => k.StartsWith("Removed:", StringComparison.Ordinal) && k.EndsWith(".C3_" + s.Tag, StringComparison.Ordinal));
        Assert.Contains(kinds, k => k.StartsWith("Added:", StringComparison.Ordinal) && k.EndsWith(".C4_" + s.Tag, StringComparison.Ordinal));

        // ⛔ Предмет тесту: сухий прогін нічого не записав — ні версії проєкту,
        // ні комірок, ні складу аркушів, ні нових рядків.
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Safe_переносить_значення_за_кодами_і_перемикає_версію_проєкту()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("applied").GetBoolean());

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == s.Doc.ProjectId).ConfigureAwait(true);
        Assert.Equal(s.TargetVersionId, project.TemplateVersionId);

        var cells = await db.CellValues.AsNoTracking()
            .Where(c => c.PeriodKeyValue == s.Doc.PeriodKey.Value && s.Doc.RowIds.Contains(c.TableRowId))
            .ToListAsync().ConfigureAwait(true);

        // Значення на НОВИХ колонках тих самих кодів; порожня комірка прибраної C3 зникла.
        Assert.Equal(2, cells.Count);
        Assert.All(cells, c => Assert.Equal(s.TargetTableDefId, c.TableDefId));
        Assert.Equal("a", cells.Single(c => c.ColumnDefId == s.TargetColumns["C1"]).ValueString);
        Assert.Equal(42m, cells.Single(c => c.ColumnDefId == s.TargetColumns["C2"]).ValueNumeric);

        var instance = await db.TableInstances.AsNoTracking()
            .SingleAsync(t => t.PeriodKeyValue == s.Doc.PeriodKey.Value && t.Id == s.Doc.TableInstanceId).ConfigureAwait(true);
        Assert.Equal(s.TargetTableDefId, instance.TableDefId);

        var sheets = await db.DocumentSheets.AsNoTracking()
            .Where(x => x.DocumentId == s.Doc.DocumentId).Select(x => x.SheetDefId).ToListAsync().ConfigureAwait(true);
        Assert.Equal([s.TargetSheetDefId], sheets);

        // Рядок, якого стара версія не мала, заведено в наявному екземплярі.
        var newRow = await db.TableRows.AsNoTracking()
            .SingleAsync(r => r.PeriodKeyValue == s.Doc.PeriodKey.Value
                              && r.TableInstanceId == s.Doc.TableInstanceId
                              && r.RowKeyValue == "R3_" + s.Tag).ConfigureAwait(true);
        Assert.Equal(s.TargetRowR3, newRow.RowDefId);

        var events = await db.Database
            .SqlQuery<string>($"SELECT ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType = N'DocumentVersionMigrated' AND ChangedByUserId = {s.UserId}")
            .ToListAsync().ConfigureAwait(true);
        var details = JsonDocument.Parse(Assert.Single(events)).RootElement;
        Assert.Equal(s.TargetVersionId, details.GetProperty("toVersionId").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Safe_відмовляє_коли_зникло_б_введене_значення()
    {
        var s = await ArrangeAsync(Target.DropsC2).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true);
        var report = JsonDocument.Parse(await dry.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.False(report.GetProperty("canApply").GetBoolean());
        Assert.Equal(1, report.GetProperty("lostValues").GetInt64());
        Assert.Contains("dataLoss", report.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateDataLoss").ConfigureAwait(true);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Safe_відмовляє_коли_змінюється_тип_колонки_з_даними()
    {
        var s = await ArrangeAsync(Target.C2BecomesText).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);

        var body = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateDataLoss").ConfigureAwait(true);
        Assert.Contains("guardedWithData", body, StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Presentation_відмовляє_на_структурну_різницю_а_на_лише_підписи_переносить()
    {
        var structural = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var labels = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);

        using (var client = await SignedInAsync(app, structural.UserName).ConfigureAwait(true))
        {
            var refused = await PostAsync(client, structural, "Presentation", dryRun: false).ConfigureAwait(true);
            await AssertProblemAsync(refused, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateStructural").ConfigureAwait(true);
        }

        using (var client = await SignedInAsync(app, labels.UserName).ConfigureAwait(true))
        {
            var applied = await PostAsync(client, labels, "Presentation", dryRun: false).ConfigureAwait(true);
            var body = await applied.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(applied.StatusCode == HttpStatusCode.OK, $"{applied.StatusCode}: {body}");

            var items = JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.NotEmpty(items);
            Assert.All(items, i => Assert.Equal("Presentation", i.GetProperty("kind").GetString()));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Поданий_аркуш_дає_409_і_нічого_не_змінює()
    {
        var s = await ArrangeAsync(Target.OnlyLabels, submitted: true).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "ECR-DOC-0409", "err.ECR-DOC-0409.migrateSheetsLocked").ConfigureAwait(true);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чернетка_як_ціль_дає_422()
    {
        var s = await ArrangeAsync(Target.OnlyLabels, publishTarget: false).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "ECR-TMPL-0422", "err.ECR-TMPL-0422.migrateTargetNotPublished").ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_Template_Edit_403()
    {
        var s = await ArrangeAsync(Target.OnlyLabels, permission: "Document.View").ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_цілей_лише_опубліковані_версії_того_самого_шаблону()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await client.GetAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/migrate-version", UriKind.Relative))
            .ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");

        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(s.Doc.TemplateVersionId, root.GetProperty("currentVersionId").GetInt32());
        Assert.Equal(
            [s.TargetVersionId],
            root.GetProperty("targets").EnumerateArray().Select(t => t.GetProperty("id").GetInt32()).ToList());
    }

    /// <summary>Чим цільова версія відрізняється від вихідної.</summary>
    public enum Target
    {
        /// <summary>Та сама структура, інші підписи колонок.</summary>
        OnlyLabels,

        /// <summary>Без C3 (там лише порожня комірка), з новою C4 і новим рядком R3.</summary>
        DropsC3AddsC4AndRow,

        /// <summary>Без C2 — а в ній значення 42.</summary>
        DropsC2,

        /// <summary>C2 стає текстовою — а в ній число 42.</summary>
        C2BecomesText,
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Scenario s, string mode, bool dryRun)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/migrate-version", UriKind.Relative),
            new { targetVersionId = s.TargetVersionId, mode, dryRun });

    private static async Task<string> AssertProblemAsync(
        HttpResponseMessage response, HttpStatusCode status, string code, string messageKey)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}");
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(code, root.GetProperty("errorCode").GetString());
        Assert.Equal(messageKey, root.GetProperty("messageKey").GetString());
        return body;
    }

    /// <summary>Усе, що перенос міг би змінити, одним рядком — для порівняння «до/після».</summary>
    private async Task<string> SnapshotAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var version = await db.Projects.AsNoTracking()
            .Where(p => p.Id == s.Doc.ProjectId).Select(p => p.TemplateVersionId).SingleAsync().ConfigureAwait(false);
        var cells = await db.CellValues.AsNoTracking()
            .Where(c => c.PeriodKeyValue == s.Doc.PeriodKey.Value && s.Doc.RowIds.Contains(c.TableRowId))
            .OrderBy(c => c.TableRowId).ThenBy(c => c.ColumnDefId)
            .Select(c => $"{c.TableRowId}/{c.ColumnDefId}/{c.TableDefId}/{c.ValueString}/{c.ValueNumeric}/{c.IsEmpty}")
            .ToListAsync().ConfigureAwait(false);
        var rows = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == s.Doc.PeriodKey.Value && r.TableInstanceId == s.Doc.TableInstanceId)
            .OrderBy(r => r.Id).Select(r => $"{r.Id}/{r.RowKeyValue}/{r.RowDefId}/{r.IsDeleted}")
            .ToListAsync().ConfigureAwait(false);
        var instance = await db.TableInstances.AsNoTracking()
            .Where(t => t.PeriodKeyValue == s.Doc.PeriodKey.Value && t.Id == s.Doc.TableInstanceId)
            .Select(t => t.TableDefId).SingleAsync().ConfigureAwait(false);
        var sheets = await db.DocumentSheets.AsNoTracking()
            .Where(x => x.DocumentId == s.Doc.DocumentId).Select(x => x.SheetDefId).ToListAsync().ConfigureAwait(false);

        return $"v{version}|t{instance}|s{string.Join(",", sheets)}|{string.Join(";", cells)}|{string.Join(";", rows)}";
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private async Task<Scenario> ArrangeAsync(
        Target target, bool submitted = false, bool publishTarget = true,
        string permission = MigrateDocumentVersionHandler.Permission)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, rowCount: 2).ConfigureAwait(false);
        var tag = doc.SheetCode["SHEET".Length..];
        var now = new DateTime(2026, 1, 16, 9, 0, 0, DateTimeKind.Utc);

        await using var db = builder.CreateContext();

        var document = await db.Documents.Include(d => d.Sheets).SingleAsync(d => d.Id == doc.DocumentId).ConfigureAwait(false);
        document.IncludeSheet(doc.SheetDefId);

        var key = doc.PeriodKey;
        db.CellValues.Add(new CellValue(
            new CellAddress(key, doc.RowIds[0], doc.ColumnDefIds[0]), doc.TableDefId, new CellValueData { ValueString = "a" }));
        db.CellValues.Add(new CellValue(
            new CellAddress(key, doc.RowIds[0], doc.ColumnDefIds[1]), doc.TableDefId, new CellValueData { ValueNumeric = 42m }));
        db.CellValues.Add(new CellValue(
            new CellAddress(key, doc.RowIds[0], doc.ColumnDefIds[2]), doc.TableDefId, new CellValueData { IsEmpty = true }));

        if (submitted)
        {
            var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, key.Value);
            state.Submit(1, now);
            db.ApprovalStates.Add(state);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        // ── Цільова версія: ті самі коди, крім того, що змінює сценарій ──
        var version = new TemplateVersion(doc.TemplateId, $"2.0.0.{tag}", 1, now, doc.TemplateVersionId);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var sheet = new SheetDef(version.Id, EcrCode.Create(doc.SheetCode), Name($"Sheet {tag} v2"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var tableCode = await db.TableDefs.AsNoTracking()
            .Where(t => t.Id == doc.TableDefId).Select(t => t.Code).SingleAsync().ConfigureAwait(false);
        var table = new TableDef(
            sheet.Id, EcrCode.Create(tableCode), Name($"Table {tag} v2"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var columns = new Dictionary<string, ColumnDef>(StringComparer.Ordinal);
        void Column(string code, int ordinal, CellDataType type)
        {
            var column = new ColumnDef(table.Id, EcrCode.Create($"{code}_{tag}"), Name($"{code} relabelled"), ordinal, type);
            columns[code] = column;
            db.ColumnDefs.Add(column);
        }

        Column("C1", 1, CellDataType.String);
        if (target != Target.DropsC2)
        {
            Column("C2", 2, target == Target.C2BecomesText ? CellDataType.String : CellDataType.Decimal);
        }

        if (target != Target.DropsC3AddsC4AndRow)
        {
            Column("C3", 3, CellDataType.Decimal);
        }
        else
        {
            Column("C4", 4, CellDataType.Decimal);
        }

        var rowKeys = target == Target.DropsC3AddsC4AndRow ? new[] { 1, 2, 3 } : [1, 2];
        var rowDefs = rowKeys
            .Select(i => new RowDef(table.Id, RowKey.Create($"R{i}_{tag}"), i, Name($"Row {i}"), RowKind.Item))
            .ToList();
        db.RowDefs.AddRange(rowDefs);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (publishTarget)
        {
            version.Publish(1, now);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        // ── Користувач із правом і грантом Write на проєкт ──
        var userName = $"migr_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"MIGR_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Doc migrate" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, permission));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Write));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(
            doc, tag, userName, user.Id, version.Id, sheet.Id, table.Id,
            columns.ToDictionary(c => c.Key, c => c.Value.Id, StringComparer.Ordinal),
            rowDefs.Count > 2 ? rowDefs[2].Id : null);
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Scenario(
        TestDocument Doc,
        string Tag,
        string UserName,
        int UserId,
        int TargetVersionId,
        int TargetSheetDefId,
        int TargetTableDefId,
        IReadOnlyDictionary<string, int> TargetColumns,
        int? TargetRowR3);
}
