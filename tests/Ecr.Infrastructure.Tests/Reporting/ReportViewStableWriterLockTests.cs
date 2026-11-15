// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewStableWriterLockTests.cs
using System.Diagnostics;
using System.Text.RegularExpressions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// B126 (D-NEW): записувачі стабільних вʼюх ШАБЛОНУ (<c>rpt.usp_GenerateStableViews</c>) серіалізуються транзакційним
/// <c>sp_getapplock</c> <c>ecr.rpt.stableviews.template.&lt;TemplateId&gt;</c>, а стан шаблону (опубліковані версії) читається ПІД
/// замком — останній записувач пише свіжий стан. Прогін «усі шаблони» не висить на чужому замку: шаблон пропускається
/// (<c>Busy</c>), відбиток не пишеться. Власна база: порядок шаблонів у прогоні «усі» детермінований, а спільна база колекції
/// містить навмисно «зіткнені» шаблони (повний прогін там кидає 50409).
/// </summary>
/// <remarks>
/// Сценарій CI B123 (run 43639822988): прогін «усі шаблони» (старт застосунку) зняв знімок версій до публікації v1, а вʼюху
/// шаблону записав ПІСЛЯ генерації шаблону — вʼюха без рядів v1. Тут затримка відтворюється детерміновано: Sch-M на вʼюсі
/// ІНШОГО шаблону (раніший у циклі) зупиняє прогін «усі» між знімком і записом.
/// Мутації (локально, B126): (1) прибрати <c>sp_getapplock</c> (rc = 0) — червоніє тест таймауту замка; (2) читати <c>@gt</c>
/// ДО замка (перенести блок замка після <c>INSERT INTO @gt</c>) — червоніє тест «записувач, що чекав на замок»; без фіксу
/// (05-rpt-views.sql з origin/main до B126) — червоніє тест прогону «усі шаблони».
/// </remarks>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "RPT-STABLE")]
public sealed partial class ReportViewStableWriterLockTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2099, 1, 2, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerFixture _sql = SqlServerFixture.WithOwnDatabase("_b126");

    public Task InitializeAsync() => _sql.InitializeAsync();

    public Task DisposeAsync() => _sql.DisposeAsync();

    [Fact]
    public async Task Прогін_усі_шаблони_пише_вʼюху_шаблону_зі_стану_під_замком_а_не_зі_знімка_на_початку()
    {
        var blocker = await BlockerTemplateAsync();
        var s = await ArrangeAsync();

        await using var hold = await OpenAsync();
        await using var holdTx = (SqlTransaction)await hold.BeginTransactionAsync();
        await ExecAsync(hold, holdTx, "EXEC sys.sp_refreshview @v", ("@v", "rpt." + blocker));   // Sch-M до коміту

        // «Усі шаблони» (як задача старту): доходить до вʼюхи blocker і стає - знімок версій уже знято, v2 ще чернетка.
        var (all, allSpid) = await StartAsync("EXEC rpt.usp_GenerateStableViews NULL, 0, 0");
        await WaitBlockedAsync(hold, holdTx, allSpid);

        await PublishAsync(s.V2);
        await using (var db = _sql.CreateContext())
        {
            await new ReportViewGenerator(db).GenerateAsync(s.V2, CancellationToken.None);   // генерація шаблону після публікації
        }

        Assert.Equal([(209901, "1"), (209902, "2")], await SeriesAsync(s));   // свіжа генерація - обидві версії

        await holdTx.CommitAsync();
        await all;

        // Прогін «усі» записав вʼюху шаблону ПІЗНІШЕ - і мусив записати свіжий стан, а не знімок з початку.
        Assert.Equal([(209901, "1"), (209902, "2")], await SeriesAsync(s));
    }

    [Fact]
    public async Task Записувач_що_чекав_на_замок_шаблону_читає_опубліковані_версії_після_замка()
    {
        var s = await ArrangeAsync();

        await using var hold = await OpenAsync();
        await using var holdTx = (SqlTransaction)await hold.BeginTransactionAsync();
        await ExecAsync(hold, holdTx,
            "DECLARE @rc int; EXEC @rc = sys.sp_getapplock @Resource = @r, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0; "
            + "IF @rc < 0 THROW 50000, N'lock not granted', 1;",
            ("@r", LockResource(s.TemplateId)));

        // Записувач шаблону (з версією v1) стає на замку: його стан шаблону мусить читатися ПІСЛЯ замка.
        var (writer, writerSpid) = await StartAsync($"EXEC rpt.usp_GenerateStableViews {s.V1}, 0, 0, 60000");
        await WaitBlockedAsync(hold, holdTx, writerSpid);

        // Поки він чекає: публікація v2 і генерація під тим самим замком (сеанс, що тримає замок, - як інший записувач).
        await PublishAsync(s.V2);
        await ExecAsync(hold, holdTx, $"EXEC rpt.usp_GenerateStableViews {s.V2}, 0, 0");
        await holdTx.CommitAsync();
        await writer;

        // Останнім писав той, хто чекав, - зі стану після замка (v2 опублікована), а не з прочитаного до нього.
        Assert.Equal([(209901, "1"), (209902, "2")], await SeriesAsync(s));
    }

    [Fact]
    public async Task Зайнятий_замок_шаблону_дає_50423_а_прогін_усі_шаблони_не_висить_і_пропускає_шаблон()
    {
        var blocker = await BlockerTemplateAsync();
        var s = await ArrangeAsync();
        await PublishAsync(s.V2);

        await using var hold = await OpenAsync();
        var holdTx = (SqlTransaction)await hold.BeginTransactionAsync();
        await ExecAsync(hold, holdTx,
            "DECLARE @rc int; EXEC @rc = sys.sp_getapplock @Resource = @r, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0; "
            + "IF @rc < 0 THROW 50000, N'lock not granted', 1;",
            ("@r", LockResource(s.TemplateId)));

        // Виклик шаблону: не чекає без кінця - названа відмова 50423 після @LockTimeoutMs.
        var clock = Stopwatch.StartNew();
        var refused = await Assert.ThrowsAsync<SqlException>(() => RunAsync($"EXEC rpt.usp_GenerateStableViews {s.V2}, 0, 0, 500"));
        Assert.Equal(50423, refused.Number);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"виклик шаблону чекав {clock.Elapsed}");

        // «Усі шаблони»: зайнятий шаблон пропущено (Busy), решту згенеровано, прогін завершився.
        clock.Restart();
        var rows = await RunAsync("EXEC rpt.usp_GenerateStableViews NULL, 0, 1, 500");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"прогін «усі» чекав {clock.Elapsed}");
        var busy = Assert.Single(rows, r => r.Action == "Busy");
        Assert.StartsWith(Sanitise($"v_{s.TemplateCode}_"), busy.View, StringComparison.Ordinal);
        Assert.Contains("being regenerated by another session", busy.Warning ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(rows, r => r.View == blocker && r.Action != "Busy");
        Assert.Equal([(209901, "1")], await SeriesAsync(s));   // вʼюху зайнятого шаблону ніхто не переписав

        await holdTx.CommitAsync();
        await holdTx.DisposeAsync();

        // Відбиток не записано (шаблон пропущено): наступний старт генерує, а не пропускає.
        var next = await RunAsync("EXEC rpt.usp_GenerateStableViews NULL, 0, 1");
        Assert.DoesNotContain(next, r => r.Action == "Skipped" || r.Action == "Busy");
        Assert.Equal([(209901, "1"), (209902, "2")], await SeriesAsync(s));
    }

    // ── помічники ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Scenario(int TemplateId, string TemplateCode, int V1, int V2, int ProjectId, string Stable);

    private static string LockResource(int templateId) => $"ecr.rpt.stableviews.template.{templateId}";

    /// <summary>Шаблон, раніший за сценарний у прогоні «усі» (менший Id), зі згенерованою стабільною вʼюхою.</summary>
    private async Task<string> BlockerTemplateAsync()
    {
        var (templateId, code) = await NewTemplateAsync("BLK");
        var (version, _) = await AddVersionAsync(templateId, "1", publish: true);
        await RunAsync($"EXEC rpt.usp_GenerateStableViews {version}, 0, 0");
        return Sanitise($"v_{code}_S_T");
    }

    /// <summary>Шаблон: v1 опублікована (209901 на її таблиці), v2 - чернетка (209902 на її таблиці), стабільна вʼюха - лише v1.</summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var (templateId, code) = await NewTemplateAsync("B126");
        var (v1, t1) = await AddVersionAsync(templateId, "1", publish: true);
        var (v2, t2) = await AddVersionAsync(templateId, "2", publish: false);
        var (projectId, documentId) = await NewProjectAsync(v1, 209901, 209902);
        await SeedAsync(documentId, 209901, t1);
        await SeedAsync(documentId, 209902, t2);
        var s = new Scenario(templateId, code, v1, v2, projectId, Sanitise($"v_{code}_S_T"));
        await RunAsync($"EXEC rpt.usp_GenerateStableViews {v1}, 0, 0");
        Assert.Equal([(209901, "1")], await SeriesAsync(s));
        return s;
    }

    private async Task<(int TemplateId, string Code)> NewTemplateAsync(string prefix)
    {
        await using var db = _sql.CreateContext();
        var code = prefix + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var t = new Template(EcrCode.Create(code), Name(code), 1, Now);
        db.Templates.Add(t);
        await db.SaveChangesAsync();
        return (t.Id, code);
    }

    private async Task<(int VersionId, (int TableId, int ColumnId) Table)> AddVersionAsync(int templateId, string versionText, bool publish)
    {
        await using var db = _sql.CreateContext();
        var version = new TemplateVersion(templateId, versionText, 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();
        var sheet = new SheetDef(version.Id, EcrCode.Create("S"), Name("S"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();
        var table = new TableDef(sheet.Id, EcrCode.Create("T"), Name("T"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();
        var column = new ColumnDef(table.Id, EcrCode.Create("A"), Name("A"), 1, CellDataType.String);
        db.ColumnDefs.Add(column);
        if (publish)
        {
            version.Publish(1, Now);
        }

        await db.SaveChangesAsync();
        return (version.Id, (table.Id, column.Id));
    }

    private async Task PublishAsync(int versionId)
    {
        await using var db = _sql.CreateContext();
        var version = await db.TemplateVersions.SingleAsync(v => v.Id == versionId);
        version.Publish(1, Now);
        await db.SaveChangesAsync();
    }

    private async Task<(int ProjectId, long DocumentId)> NewProjectAsync(int versionId, params int[] periods)
    {
        await using var db = _sql.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync();
        var project = new Ecr.Domain.Entities.Documents.Project(
            EcrCode.Create("PRJ" + tag), Name(tag), new DateOnly(2099, 1, 1), new DateOnly(2099, 12, 31),
            versionId, PeriodKind.Monthly, policyId, "Asia/Atyrau");
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        foreach (var period in periods)
        {
            var key = new PeriodKey(period);
            db.Periods.Add(new Period(project.Id, key, (byte)key.Sequence, new DateOnly(key.Year, key.Sequence, 1),
                new DateOnly(key.Year, key.Sequence, DateTime.DaysInMonth(key.Year, key.Sequence))));
        }

        var document = new Document(project.Id, "DOC" + tag, 1, Now);
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        return (project.Id, document.Id);
    }

    private async Task SeedAsync(long documentId, int periodKey, (int TableId, int ColumnId) table)
    {
        var loader = new BulkCellLoader(_sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);
        var key = new PeriodKey(periodKey);
        await using (var db = _sql.CreateContext())
        {
            db.TableInstances.Add(new TableInstance(key, instanceId, documentId, table.TableId, Now));
            db.TableRows.Add(new TableRow(key, rowId, instanceId, RowKey.Create("R1"), 1, Now));
            await db.SaveChangesAsync();
        }

        await using var connection = await OpenAsync();
        await ExecAsync(connection, null,
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString) VALUES (@p, @r, @c, @t, @v)",
            ("@p", periodKey), ("@r", rowId), ("@c", table.ColumnId), ("@t", table.TableId), ("@v", "a" + periodKey));
    }

    private async Task<List<(int Period, string Version)>> SeriesAsync(Scenario s)
    {
        var list = new List<(int, string)>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // ім'я вʼюхи - з коду шаблону тесту
        command.CommandText = $"SELECT _PeriodKey, _TemplateVersion FROM rpt.[{s.Stable}] WHERE _ProjectId = @p ORDER BY _PeriodKey";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("@p", s.ProjectId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add((reader.GetInt32(0), reader.GetString(1)));
        }

        return list;
    }

    /// <summary>Виклик процедури на окремому зʼєднанні у фоні; повертає задачу і SPID (для очікування блокування).</summary>
    private async Task<(Task Run, int Spid)> StartAsync(string text)
    {
        var connection = await OpenAsync();
        await using (var spid = connection.CreateCommand())
        {
            spid.CommandText = "SELECT CAST(@@SPID AS int)";
            var id = (int)(await spid.ExecuteScalarAsync())!;
            var run = Task.Run(async () =>
            {
                await using (connection)
                {
                    await DrainAsync(connection, text);
                }
            });
            return (run, id);
        }
    }

    /// <summary>Чекає, доки сеанс <paramref name="spid"/> стане за сеансом, що тримає <paramref name="hold"/>.</summary>
    private static async Task WaitBlockedAsync(SqlConnection hold, SqlTransaction tx, int spid)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromMinutes(2))
        {
            await using var probe = hold.CreateCommand();
            probe.Transaction = tx;
            probe.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id = @s AND blocking_session_id = @@SPID";
            probe.Parameters.AddWithValue("@s", spid);
            if ((int)(await probe.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"сеанс {spid} так і не став за замком сеансу-утримувача");
    }

    private async Task<List<(string View, string Action, string? Warning)>> RunAsync(string text)
    {
        await using var connection = await OpenAsync();
        return await DrainAsync(connection, text);
    }

    private static async Task<List<(string View, string Action, string? Warning)>> DrainAsync(SqlConnection connection, string text)
    {
        var rows = new List<(string, string, string?)>();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = 600;
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            while (await reader.ReadAsync())
            {
                if (reader.FieldCount == 3)
                {
                    rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
                }
            }
        }
        while (await reader.NextResultAsync());

        return rows;
    }

    private static async Task ExecAsync(SqlConnection connection, SqlTransaction? tx, string text, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = text;
        command.CommandTimeout = 600;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.NextResultAsync())
        {
        }
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(_sql.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static string Sanitise(string name) => NotIdentifier().Replace(name, "_");

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex NotIdentifier();
}
