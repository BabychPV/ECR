// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.FromMonth.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// RPT-STABLE Part B: перенос версії «з місяця X» поряд із «увесь проєкт». Періоди &lt; X лишаються на СТАРИХ ідентифікаторах
/// (разом із затвердженим), періоди ≥ X переходять; шкала версій проєкту веде версію за період; нові періоди — на нову.
/// </summary>
/// <remarks>
/// ⚠ Справжнім HTTP на справжньому SQL Server. Мутаційні докази: (1) у <c>DocumentVersionMigrationStore.Prelude</c> прибрати
/// <c>AND PeriodKey &gt;= @fromPeriod</c> - червоніють «періоди до X не зачеплено» (ідентифікатори й значення); (2) у
/// <c>ReadScopeAsync</c> прибрати <c>a.PeriodKey &gt;= fromPeriodKey</c> - червоніє «затверджене до X не блокує»; (3) у
/// <c>ProjectVersionTimeline.VersionOf</c> <c>&lt;=</c> → <c>&lt;</c> - червоніє «шкала».
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    private static Task<HttpResponseMessage> PostRangeAsync(
        HttpClient client, Scenario s, bool dryRun, string range, int? fromPeriodKey, string mode = "Safe", object? renames = null)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/migrate-version", UriKind.Relative),
            new { targetVersionId = s.TargetVersionId, mode, dryRun, range, fromPeriodKey, tableRenames = renames });

    /// <summary>Три місяці одного документа: 202601 (затверджений) вже є, додає 202602 і 202603 з тими самими значеннями.</summary>
    private async Task<List<TestPeriodInstance>> AddMonthsAsync(Scenario s, params int[] periods)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var result = new List<TestPeriodInstance>();
        await using var db = builder.CreateContext();
        foreach (var period in periods)
        {
            var instance = await builder.AddPeriodInstanceAsync(s.Doc, new PeriodKey(period)).ConfigureAwait(false);
            db.CellValues.Add(new CellValue(
                new CellAddress(instance.PeriodKey, instance.RowIds[0], s.Doc.ColumnDefIds[0]), s.Doc.TableDefId,
                new CellValueData { ValueString = "m" + period }));
            db.CellValues.Add(new CellValue(
                new CellAddress(instance.PeriodKey, instance.RowIds[0], s.Doc.ColumnDefIds[1]), s.Doc.TableDefId,
                new CellValueData { ValueNumeric = period % 1000 }));
            result.Add(instance);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return result;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "RPT-STABLE")]
    public async Task З_місяця_періоди_до_X_лишаються_на_старих_id_разом_із_затвердженим_а_після_X_переходять_і_шкала_веде_версію()
    {
        // TableRenamed зачіпає подане (перейменування), але не губить значень: «увесь проєкт» відмовить, «з лютого» ні.
        var s = await ArrangeAsync(Target.TableRenamed, submitted: true, approved: true).ConfigureAwait(true);   // 202601: Approved
        var months = await AddMonthsAsync(s, 202602, 202603).ConfigureAwait(true);

        // Стан періоду ДО X фіксується байт-у-байт (ідентифікатори й значення).
        var jan = await PeriodStateAsync(s, 202601).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        // «Увесь проєкт» тут відмовляє: затверджений січневий аркуш зачіпається (DropsC2).
        var whole = await PostRangeAsync(client, s, dryRun: true, "WholeProject", null, renames: Renames(s)).ConfigureAwait(true);
        var wholeReport = JsonDocument.Parse(await whole.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Contains("sheetsLocked", wholeReport.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));

        // «З лютого»: січневе не рахується - дозволено.
        var dry = await PostRangeAsync(client, s, dryRun: true, "FromMonth", 202602, renames: Renames(s)).ConfigureAwait(true);
        var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody}");
        var dryReport = JsonDocument.Parse(dryBody).RootElement;
        Assert.True(dryReport.GetProperty("canApply").GetBoolean(), dryBody);
        Assert.Equal(202602, dryReport.GetProperty("effectiveFromPeriodKey").GetInt32());
        Assert.Equal(0, dryReport.GetProperty("lockedSheetsAffected").GetInt32());

        var response = await PostRangeAsync(client, s, dryRun: false, "FromMonth", 202602, renames: Renames(s)).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("applied").GetBoolean());

        // Січень: усе як було (ідентифікатори, значення, статус Approved на СТАРОМУ аркуші).
        Assert.Equal(jan, await PeriodStateAsync(s, 202601).ConfigureAwait(true));
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var janState = await db.ApprovalStates.AsNoTracking()
            .SingleAsync(a => a.DocumentId == s.Doc.DocumentId && a.PeriodKey == 202601).ConfigureAwait(true);
        Assert.Equal(s.Doc.SheetDefId, janState.SheetDefId);
        Assert.Equal(DocumentStatus.Approved, janState.Status);

        // Лютий і березень - на цільовій версії: екземпляр, колонки.
        foreach (var m in months)
        {
            var instance = await db.TableInstances.AsNoTracking()
                .SingleAsync(t => t.PeriodKeyValue == m.PeriodKey.Value && t.Id == m.TableInstanceId).ConfigureAwait(true);
            Assert.Equal(s.TargetTableDefId, instance.TableDefId);
            var cells = await db.CellValues.AsNoTracking()
                .Where(c => c.PeriodKeyValue == m.PeriodKey.Value && m.RowIds.Contains(c.TableRowId) && !c.IsEmpty)
                .ToListAsync().ConfigureAwait(true);
            Assert.All(cells, c => Assert.Equal(s.TargetTableDefId, c.TableDefId));
            Assert.Equal("m" + m.PeriodKey.Value, cells.Single(c => c.ColumnDefId == s.TargetColumns["C1"]).ValueString);
        }

        // Склад документа - на цільовій версії; чинна версія проєкту - цільова; шкала: (202601, джерело), (202602, ціль).
        Assert.Equal([s.TargetSheetDefId], await db.DocumentSheets.AsNoTracking()
            .Where(x => x.DocumentId == s.Doc.DocumentId).Select(x => x.SheetDefId).ToListAsync().ConfigureAwait(true));
        var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == s.Doc.ProjectId).ConfigureAwait(true);
        Assert.Equal(s.TargetVersionId, project.TemplateVersionId);
        var timeline = await db.ProjectTemplateVersions.AsNoTracking()
            .Where(t => t.ProjectId == s.Doc.ProjectId).OrderBy(t => t.FromPeriodKey)
            .Select(t => new { t.FromPeriodKey, t.TemplateVersionId }).ToListAsync().ConfigureAwait(true);
        Assert.Equal([(202601, s.Doc.TemplateVersionId), (202602, s.TargetVersionId)], timeline.Select(t => (t.FromPeriodKey, t.TemplateVersionId)));

        // Версія за період - SQL-функція: січень старий, лютий і далі (і НОВІ періоди) - цільова.
        var versions = await db.Projects.AsNoTracking().Where(p => p.Id == s.Doc.ProjectId)
            .Select(p => new
            {
                Jan = EcrDbContext.VersionOf(p.Id, 202601), Feb = EcrDbContext.VersionOf(p.Id, 202602),
                Dec = EcrDbContext.VersionOf(p.Id, 202612),
            }).SingleAsync().ConfigureAwait(true);
        Assert.Equal(s.Doc.TemplateVersionId, versions.Jan);
        Assert.Equal(s.TargetVersionId, versions.Feb);
        Assert.Equal(s.TargetVersionId, versions.Dec);

        // Друга ціль для «увесь проєкт» тепер недоступна: шкала має два сегменти.
        var again = await PostRangeAsync(client, s, dryRun: false, "WholeProject", null).ConfigureAwait(true);
        Assert.NotEqual(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "RPT-STABLE")]
    public async Task Увесь_проєкт_лишає_шкалу_з_одним_рядком_цільової_версії_з_першого_періоду()
    {
        var s = await ArrangeAsync(Target.AddsC4Only).ConfigureAwait(true);
        await AddMonthsAsync(s, 202602).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var response = await PostRangeAsync(client, s, dryRun: false, "WholeProject", null).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var timeline = await db.ProjectTemplateVersions.AsNoTracking()
            .Where(t => t.ProjectId == s.Doc.ProjectId).Select(t => new { t.FromPeriodKey, t.TemplateVersionId }).ToListAsync().ConfigureAwait(true);
        var only = Assert.Single(timeline);
        Assert.Equal((202601, s.TargetVersionId), (only.FromPeriodKey, only.TemplateVersionId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "RPT-STABLE")]
    public async Task З_середини_року_для_таблиці_місяців_у_колонках_увесь_рік_іде_з_січня_і_діалог_попереджає()
    {
        var s = await ArrangeAsync(Target.AddsC4Only, periodKey: 202601).ConfigureAwait(true);
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        await builder.AddPeriodAsync(s.Doc, new PeriodKey(202512)).ConfigureAwait(true);   // перший період - до січня
        await AddMonthsAsync(s, 202602, 202603).ConfigureAwait(true);

        // Джерело має таблицю «місяці в колонках» (Q-17-сімейство).
        await using (var db0 = builder.CreateContext())
        {
            var table = await db0.TableDefs.SingleAsync(t => t.Id == s.Doc.TableDefId).ConfigureAwait(true);
            table.SetLayout(TableLayoutKind.MonthsInColumns);
            await db0.SaveChangesAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = await PostRangeAsync(client, s, dryRun: true, "FromMonth", 202603).ConfigureAwait(true);
        var dryBody = await dry.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(dry.StatusCode == HttpStatusCode.OK, $"{dry.StatusCode}: {dryBody}");
        var report = JsonDocument.Parse(dryBody).RootElement;
        Assert.Equal(202603, report.GetProperty("fromPeriodKey").GetInt32());
        Assert.Equal(202601, report.GetProperty("effectiveFromPeriodKey").GetInt32());
        var warning = Assert.Single(report.GetProperty("warnings").EnumerateArray());
        Assert.Equal("effectiveFromShiftedToJanuary", warning.GetProperty("code").GetString());
        Assert.Equal("documents.migrateWarnEffectiveFromJanuary", warning.GetProperty("messageKey").GetString());

        // Без таблиці місяців у колонках X лишається X (контроль: ті самі періоди, інша розкладка).
        await using (var db1 = builder.CreateContext())
        {
            var table = await db1.TableDefs.SingleAsync(t => t.Id == s.Doc.TableDefId).ConfigureAwait(true);
            table.SetLayout(TableLayoutKind.PerPeriodInstance);
            await db1.SaveChangesAsync().ConfigureAwait(true);
        }

        // ⚠ Свіжий застосунок: знімок метаданих версії кешований в першому (зміна розкладки ревізії не піднімає).
        using var app2 = new EcrApiFactory(sql);
        using var client2 = await SignedInAsync(app2, s.UserName).ConfigureAwait(true);
        var plain = await PostRangeAsync(client2, s, dryRun: true, "FromMonth", 202603).ConfigureAwait(true);
        var plainReport = JsonDocument.Parse(await plain.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal(202603, plainReport.GetProperty("effectiveFromPeriodKey").GetInt32());
        Assert.Equal(JsonValueKind.Null, plainReport.GetProperty("warnings").ValueKind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "RPT-STABLE")]
    public async Task Некоректні_межі_та_чернетка_дають_422_з_ключами()
    {
        var s = await ArrangeAsync(Target.AddsC4Only).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var missing = await PostRangeAsync(client, s, dryRun: true, "FromMonth", null).ConfigureAwait(true);
        await AssertProblemAsync(missing, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateFromMonthRequired")
            .ConfigureAwait(true);

        var foreign = await PostRangeAsync(client, s, dryRun: true, "FromMonth", 209912).ConfigureAwait(true);
        await AssertProblemAsync(foreign, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateFromMonthInvalid")
            .ConfigureAwait(true);
    }

    private async Task<string> PeriodStateAsync(Scenario s, int periodKey)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var instances = await db.TableInstances.AsNoTracking()
            .Where(t => t.DocumentId == s.Doc.DocumentId && t.PeriodKeyValue == periodKey)
            .OrderBy(t => t.Id).Select(t => $"{t.Id}/{t.TableDefId}").ToListAsync().ConfigureAwait(false);
        var cells = await (
                from c in db.CellValues.AsNoTracking()
                join r in db.TableRows.AsNoTracking() on new { c.PeriodKeyValue, Id = c.TableRowId } equals new { r.PeriodKeyValue, r.Id }
                join t in db.TableInstances.AsNoTracking() on new { r.PeriodKeyValue, Id = r.TableInstanceId } equals new { t.PeriodKeyValue, t.Id }
                where t.DocumentId == s.Doc.DocumentId && c.PeriodKeyValue == periodKey
                orderby c.TableRowId, c.ColumnDefId
                select $"{c.TableRowId}/{c.ColumnDefId}/{c.TableDefId}/{c.ValueString}/{c.ValueNumeric}/{c.IsEmpty}")
            .ToListAsync().ConfigureAwait(false);
        return string.Join(";", instances) + "|" + string.Join(";", cells);
    }
}
