// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.Recalculation.cs
using System.Globalization;
using System.Net;
using System.Text.Json;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// LP-41: перенос на версію, що ДОДАЄ формулу, ставить ОДИН coalesced перерахунок проєкту (системна ціль,
/// <c>PeriodKey = null</c>); без змін формул і в сухому прогоні — нічого; закритий період не перераховується.
/// </summary>
/// <remarks>
/// ⚠ Справжнім HTTP, справжнім SQL Server і справжньою чергою (<c>itg.JobProgress</c>). Мутаційний доказ: у
/// <c>MigrateDocumentVersionHandler.ApplyAsync</c> прибрати виклик <c>MigrationRecalculation.EnqueueAsync</c> —
/// червоніють «додає формулу» і «значення формули»; зробити умову безумовною — червоніє «без змін формул».
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Перенос_що_додає_формулу_ставить_один_перерахунок_проєкту_і_значення_формули_рахується()
    {
        var s = await ArrangeAsync(Target.AddsFormulaColumn).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("applied").GetBoolean());

        // Рівно одна задача на проєкт: системна (автора немає), не задача документа.
        // Постановка асинхронна щодо запису рядка стану (черга пише його на старті задачі) - чекаємо появи.
        var queued = 0;
        for (var i = 0; i < 240 && queued == 0; i++)
        {
            queued = await ProjectRecalcJobsAsync(s, systemOnly: false).ConfigureAwait(true);
            if (queued == 0)
            {
                await Task.Delay(250).ConfigureAwait(true);
            }
        }

        Assert.True(queued == 1, $"задач перерахунку проєкту: {queued}\n{await DumpJobsAsync().ConfigureAwait(true)}{app.ErrorsText}");
        Assert.Equal(1, await ProjectRecalcJobsAsync(s, systemOnly: true).ConfigureAwait(true));

        // Після виконання задач (проєкт → документ) нова колонка має значення, а не порожнечу.
        var c4 = s.TargetColumns["C4"];
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var deadline = DateTime.UtcNow.AddSeconds(90);
        decimal? value = null;
        while (DateTime.UtcNow < deadline && value is null)
        {
            value = await db.CellValues.AsNoTracking()
                .Where(c => c.PeriodKeyValue == s.Doc.PeriodKey.Value && c.ColumnDefId == c4 && c.ValueNumeric != null)
                .Select(c => c.ValueNumeric)
                .FirstOrDefaultAsync().ConfigureAwait(true);
            if (value is null)
            {
                await Task.Delay(250).ConfigureAwait(true);
            }
        }

        Assert.True(value == 42m, $"значення формули після перерахунку: {value}; {app.ErrorsText}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Перенос_без_змін_формул_перерахунку_не_ставить()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");

        // Рішення плану (детерміновано): подія переносу каже, що таблиць зі змінами формул немає.
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var details = await db.Database
                .SqlQuery<string>($"SELECT ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType = N'DocumentVersionMigrated' AND ChangedByUserId = {s.UserId}")
                .SingleAsync().ConfigureAwait(true);
            Assert.Equal(0, JsonDocument.Parse(details).RootElement.GetProperty("formulaTables").GetInt32());
        }

        await Task.Delay(2000).ConfigureAwait(true);
        Assert.Equal(0, await ProjectRecalcJobsAsync(s, systemOnly: false).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Сухий_прогін_що_додає_формулу_перерахунку_не_ставить()
    {
        var s = await ArrangeAsync(Target.AddsFormulaColumn).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("dryRun").GetBoolean());

        await Task.Delay(2000).ConfigureAwait(true);
        Assert.Equal(0, await ProjectRecalcJobsAsync(s, systemOnly: false).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Закритий_період_після_переносу_не_перераховується_нова_колонка_лишається_порожньою()
    {
        var s = await ArrangeAsync(Target.AddsFormulaColumn).ConfigureAwait(true);

        var now = new DateTime(2026, 1, 16, 9, 0, 0, DateTimeKind.Utc);
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var project = await db.Projects.Include(p => p.Periods)
                .FirstAsync(p => p.Id == s.Doc.ProjectId).ConfigureAwait(true);
            project.Activate(now);
            foreach (var period in project.Periods)
            {
                period.AdvanceTo(PeriodState.Closed, now);
            }

            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");

        // Перерахунок поставлено (колонка нова), але сама задача відсіює закритий період зі скоупу запису.
        Assert.Equal(1, await ProjectRecalcJobsAsync(s, systemOnly: true).ConfigureAwait(true));
        await WaitRecalcSettledAsync(s).ConfigureAwait(true);

        await using var read = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var c4 = s.TargetColumns["C4"];
        var written = await read.CellValues.AsNoTracking()
            .CountAsync(c => c.PeriodKeyValue == s.Doc.PeriodKey.Value && c.ColumnDefId == c4 && c.ValueNumeric != null)
            .ConfigureAwait(true);
        Assert.Equal(0, written);
    }

    private async Task<string> DumpJobsAsync()
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var rows = await db.Database.SqlQuery<string>($"SELECT TOP 20 CONCAT(JobId, N' | ', JobCode, N' | ', TargetKey, N' | ', State, N' | ', CreatedByUserId) AS Value FROM itg.JobProgress ORDER BY CreatedAt DESC").ToListAsync().ConfigureAwait(false);
        var audit = await db.Database.SqlQuery<string>($"SELECT TOP 3 ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType = N'DocumentVersionMigrated' ORDER BY Id DESC").ToListAsync().ConfigureAwait(false);
        var defs = await db.Database.SqlQuery<string>($"SELECT TOP 4 CONCAT(N'F:', TableDefId, N'/', ColumnDefId, N'/', Expression, N'/', IsDeleted) AS Value FROM cfg.FormulaDef ORDER BY Id DESC").ToListAsync().ConfigureAwait(false);
        var cols = await db.Database.SqlQuery<string>($"SELECT TOP 5 CONCAT(N'C:', Id, N'/', TableDefId, N'/', Code, N'/', DataType, N'/', IsDeleted) AS Value FROM cfg.ColumnDef ORDER BY Id DESC").ToListAsync().ConfigureAwait(false);
        return string.Join('\n', rows) + '\n' + string.Join('\n', audit) + '\n' + string.Join('\n', defs) + '\n' + string.Join('\n', cols) + '\n';
    }

    /// <summary>Скільки задач перерахунку проєкту-після-переносу в черзі (за ціллю злиття).</summary>
    private async Task<int> ProjectRecalcJobsAsync(Scenario s, bool systemOnly)
    {
        var target = MigrationRecalculation.TargetOf(s.Doc.ProjectId);
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        return systemOnly
            ? await db.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM itg.JobProgress WHERE JobCode LIKE N'%IRecalculationJob' AND JobId LIKE {"%~" + target + "~%"} AND CreatedByUserId IS NULL")
                .SingleAsync().ConfigureAwait(false)
            : await db.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM itg.JobProgress WHERE JobCode LIKE N'%IRecalculationJob' AND JobId LIKE {"%~" + target + "~%"}")
                .SingleAsync().ConfigureAwait(false);
    }

    /// <summary>Чекає, поки задача проєкту й документні задачі (розклад) завершаться; до 90 с.</summary>
    private async Task WaitRecalcSettledAsync(Scenario s)
    {
        var docTarget = string.Create(CultureInfo.InvariantCulture, $"%~doc{s.Doc.DocumentId}-%~%");
        var projectTarget = "%~" + MigrationRecalculation.TargetOf(s.Doc.ProjectId) + "~%";
        var deadline = DateTime.UtcNow.AddSeconds(90);
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        while (DateTime.UtcNow < deadline)
        {
            var open = await db.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM itg.JobProgress WHERE JobCode LIKE N'%IRecalculationJob' AND (JobId LIKE {projectTarget} OR JobId LIKE {docTarget}) AND State IN (N'Queued', N'Running')")
                .SingleAsync().ConfigureAwait(false);
            var children = await db.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM itg.JobProgress WHERE JobCode LIKE N'%IRecalculationJob' AND JobId LIKE {docTarget}")
                .SingleAsync().ConfigureAwait(false);
            if (open == 0 && children > 0)
            {
                return;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }
    }
}
