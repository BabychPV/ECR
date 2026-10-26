// tests/Ecr.Infrastructure.Tests/Persistence/Q17MonthColumnConsolidationSqlTests.cs
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Q-17 (D-457), коміт 6: скрипти <c>tools/q17</c> на справжньому SQL Server. Один T-SQL
/// (<c>Preview-MonthColumnConsolidation.sql</c>) обслуговує Preview / Apply / Purge, PowerShell-обгортка
/// (<c>Apply-MonthColumnConsolidation.ps1</c>) — запобіжники. Кожен тест будує власний документ і ганяє скрипт з
/// <c>@DocumentId</c> — бази інших тестів колекції не зачіпаються.
/// </summary>
/// <remarks>
/// Модель: місячний стовпець M3 — власник екземпляр 202603; введений не-місячний «Total» — якір 202601 (найменший
/// період календаря року); похідні (IsCalculated = 1) не переносяться. Мутація-доказ: у SQL прибрати
/// <c>IsCalculated = 0</c> з кандидатів — тест (i) червоніє; прибрати <c>AND cv.IsEmpty = 1</c> з UPDATE — тест (c).
/// </remarks>
[Collection("SqlServer")]
public sealed class Q17MonthColumnConsolidationSqlTests(SqlServerFixture sql)
{
    private const int Jan = 202601;
    private const int Mar = 202603;
    private const int Apr = 202604;
    private const int May = 202605;
    private static readonly string[] AllKeys = ["A", "B", "C"];
    private static int _tag;

    // ─────────────────────────────────────────────── (a) порожній власник, одна копія

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-457")]
    public async Task A_власник_порожній_одна_копія_копіюється_у_власника()
    {
        var w = await ArrangeAsync(instances: new() { [Jan] = ["A"], [Mar] = ["A"], [May] = ["A"] });
        await CellAsync(w, May, "A", w.M3, 7m);

        var preview = Summary(await RunAsync(w, "Preview"));
        Assert.Equal(1, preview.copyPlanned);
        Assert.Equal(0, preview.conflicts);
        Assert.Equal(0, preview.copiesWritten);                    // Preview нічого не пише
        Assert.Null(await ReadCellAsync(w, Mar, "A", w.M3));

        var applied = Summary(await RunAsync(w, "Apply"));
        Assert.Equal(1, applied.copiesWritten);

        var owner = await ReadCellAsync(w, Mar, "A", w.M3);
        Assert.NotNull(owner);
        Assert.Equal(7m, owner!.Numeric);
        Assert.False(owner.IsEmpty);
        Assert.False(owner.IsCalculated);
        Assert.Equal(7m, (await ReadCellAsync(w, May, "A", w.M3))!.Numeric);   // копія лишається (Purge окремо)
    }

    // ─────────────────────────────────────────────── (b) кілька копій — вибір за ChangedAt

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-457")]
    public async Task B_кілька_копій_обирається_найновіше_введене_за_аудитом()
    {
        var w = await ArrangeAsync(instances: new() { [Jan] = ["A"], [Mar] = ["A"], [May] = ["A"] });
        await CellAsync(w, Jan, "A", w.M3, 5m);
        await CellAsync(w, May, "A", w.M3, 9m);
        await AuditAsync(w, Jan, "A", w.M3, DateTime.UtcNow.AddHours(-48), "5");
        await AuditAsync(w, May, "A", w.M3, DateTime.UtcNow.AddHours(-24), "9");
        // перерахунок пізніше не рахується як введення
        await AuditAsync(w, Jan, "A", w.M3, DateTime.UtcNow.AddHours(-1), "5", origin: "Recalculation");

        var ds = await RunAsync(w, "Apply");
        var sum = Summary(ds);
        Assert.Equal(1, sum.copyPlanned);
        Assert.Equal(1, sum.conflicts);                              // 5 проти 9 — розбіжні непорожні
        Assert.Equal(9m, (await ReadCellAsync(w, Mar, "A", w.M3))!.Numeric);

        var conflict = Assert.Single(ds.Tables[2].Rows.Cast<DataRow>());
        Assert.Equal("ByAuditChangedAt", conflict["Reason"]);
        Assert.Contains("p202605=9", (string)conflict["Chosen"], StringComparison.Ordinal);
        Assert.Contains("202601=5", (string)conflict["ValuesByPeriod"], StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-457")]
    public async Task B2_без_аудиту_обирається_найближчий_до_власника_період()
    {
        var w = await ArrangeAsync(instances: new() { [Jan] = ["A"], [Mar] = ["A"], [Apr] = ["A"] });
        await CellAsync(w, Jan, "A", w.M3, 1m);      // відстань 2
        await CellAsync(w, Apr, "A", w.M3, 4m);      // відстань 1

        var ds = await RunAsync(w, "Apply");

        Assert.Equal(4m, (await ReadCellAsync(w, Mar, "A", w.M3))!.Numeric);
        Assert.Equal("ByNearestPeriod", Assert.Single(ds.Tables[2].Rows.Cast<DataRow>())["Reason"]);
    }

    // ─────────────────────────────────────────────── (c) непорожній власник не змінюється

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-457")]
    public async Task C_власник_непорожній_лишається_конфлікт_у_звіті()
    {
        var w = await ArrangeAsync(instances: new() { [Jan] = ["A"], [Mar] = ["A"], [May] = ["A"] });
        await CellAsync(w, Mar, "A", w.M3, 99m);
        await CellAsync(w, May, "A", w.M3, 7m);

        var ds = await RunAsync(w, "Apply");
        var sum = Summary(ds);

        Assert.Equal(0, sum.copyPlanned);
        Assert.Equal(0, sum.copiesWritten);
        Assert.Equal(1, sum.conflicts);
        Assert.Equal(99m, (await ReadCellAsync(w, Mar, "A", w.M3))!.Numeric);
        Assert.Equal("OwnerKept", Assert.Single(ds.Tables[2].Rows.Cast<DataRow>())["Reason"]);
    }

    // ─────────────────────────────────────────────── (d) заблокований власник — лише звіт

    [Theory]
    [InlineData("submitted")]
    [InlineData("closed")]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-457")]
    public async Task D_заблокований_власник_не_пишеться_без_дозволу(string lockKind)
    {
        var w = await ArrangeAsync(instances: new() { [Jan] = ["A"], [Mar] = ["A"], [May] = ["A"] });
        await CellAsync(w, May, "A", w.M3, 7m);
        await LockAsync(w, Mar, lockKind);

        var ds = await RunAsync(w, "Apply");
        var sum = Summary(ds);

        Assert.Equal(1, sum.lockedTargets);
        Assert.Equal(0, sum.copyPlanned);
        Assert.Equal(0, sum.copiesWritten);
        Assert.Null(await ReadCellAsync(w, Mar, "A", w.M3));
        var locked = Assert.Single(ds.Tables[4].Rows.Cast<DataRow>());
        Assert.Equal("lockedTargetNeedsDecision", locked["Reason"]);

        // адмінська дія: -IncludeLockedTargets → пише, окремий лічильник
        var forced = Summary(await RunAsync(w, "Apply", writeLocked: true));
        Assert.Equal(1, forced.copiesWritten);
        Assert.Equal(1, forced.writtenToLocked);
        Assert.Equal(7m, (await ReadCellAsync(w, Mar, "A", w.M3))!.Numeric);
    }

    // ─────────────────────────────────────────────── (e) ідемпотентність, (f) аудит Migration, (g) рядки

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-457")]
    public async Task E_повторний_запуск_нічого_не_змінює()
    {
        var w = await ArrangeAsync(instances: new() { [Jan] = ["A", "B"], [May] = ["A", "C"] });
        await CellAsync(w, May, "A", w.M3, 7m);
        await CellAsync(w, May, "C", w.Total, 3m);

        var first = Summary(await RunAsync(w, "Apply"));
        Assert.True(first.copiesWritten >= 2);
        var snapshot = await SnapshotAsync(w);

        var second = Summary(await RunAsync(w, "Apply"));

        Assert.Equal(0, second.copyPlanned);
        Assert.Equal(0, second.copiesWritten);
        Assert.Equal(0, second.rowsCreated);
        Assert.Equal(0, second.instancesCreated);
        Assert.Equal(0, second.mirrorRowsToCreate + second.anchorRowsToCreate + second.ownerInstancesToCreate);
        Assert.Equal(snapshot, await SnapshotAsync(w));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-457")]
    public async Task F_аудит_Migration_із_CorrelationId_на_фізичній_адресі_власника()
    {
        var w = await ArrangeAsync(instances: new() { [Jan] = ["A"], [Mar] = ["A"], [May] = ["A"] });
        await CellAsync(w, May, "A", w.M3, 12.5m);
        var runId = "q17-test-" + Guid.NewGuid().ToString("N")[..8];

        await RunAsync(w, "Apply", runId: runId);

        await using var cn = new SqlConnection(sql.ConnectionString);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT PeriodKey, TableRowId, RowKey, ColumnDefId, OldValue, NewValue, Origin, CorrelationId, ChangedByUserId " +
                          "FROM aud.CellChange WHERE DocumentId = @d AND CorrelationId = @c";
        cmd.Parameters.AddWithValue("@d", w.DocId);
        cmd.Parameters.AddWithValue("@c", runId);
        await using var rd = await cmd.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync());
        Assert.Equal(Mar, rd.GetInt32(0));                                       // PeriodKey власника, не періоду-копії
        Assert.Equal(w.RowIds[(Mar, "A")], rd.GetInt64(1));
        Assert.Equal("A", rd.GetString(2));
        Assert.Equal(w.M3, rd.GetInt32(3));
        Assert.True(rd.IsDBNull(4));
        Assert.Equal("12.5", rd.GetString(5));
        Assert.Equal("Migration", rd.GetString(6));
        Assert.Equal(runId, rd.GetString(7));
        Assert.False(await rd.ReadAsync());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-457")]
    public async Task G_створюються_екземпляр_власника_дзеркальні_рядки_і_рядки_якоря()
    {
        // 202601: A,B (якір), 202605: A,C; екземпляра 202603 немає, а значення M3 лежить у 202605.
        var w = await ArrangeAsync(instances: new() { [Jan] = ["A", "B"], [May] = ["A", "C"] });
        await CellAsync(w, May, "C", w.M3, 4m);

        var preview = Summary(await RunAsync(w, "Preview"));
        Assert.Equal(1, preview.ownerInstancesToCreate);
        Assert.Equal(1, preview.anchorRowsToCreate);          // C — в якір 202601
        Assert.Equal(1 + 3, preview.mirrorRowsToCreate);      // B — у 202605; новий 202603 одержує A,B,C
        Assert.Equal(0, await RowCountAsync(w, Mar));

        var applied = Summary(await RunAsync(w, "Apply"));
        Assert.Equal(1, applied.instancesCreated);
        Assert.Equal(5, applied.rowsCreated);

        Assert.Equal(AllKeys, await RowKeysAsync(w, Jan));
        Assert.Equal(AllKeys, await RowKeysAsync(w, Mar));
        Assert.Equal(AllKeys, await RowKeysAsync(w, May));
        Assert.Equal(4m, await OwnerValueAsync(w, Mar, "C", w.M3));
    }

    // ─────────────────────────────────────────────── (i) похідні, (j) без Period-рядка, не-місячні → якір

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-457")]
    public async Task I_похідні_IsCalculated_не_переносяться()
    {
        var w = await ArrangeAsync(instances: new() { [Jan] = ["A"], [Mar] = ["A"], [May] = ["A"] });
        await CellAsync(w, May, "A", w.M3, 5m, calculated: true);
        await CellAsync(w, May, "A", w.Calc, 8m, calculated: true);

        var sum = Summary(await RunAsync(w, "Apply"));

        Assert.Equal(0, sum.copyPlanned);
        Assert.Equal(0, sum.copyCells);
        Assert.Null(await ReadCellAsync(w, Mar, "A", w.M3));
        Assert.Null(await ReadCellAsync(w, Jan, "A", w.Calc));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-457")]
    public async Task J_період_власника_без_Period_рядка_лишає_локальну_копію()
    {
        var w = await ArrangeAsync(
            instances: new() { [Jan] = ["A"], [May] = ["A"] },
            calendar: [Jan, 202602, Apr, May]);                       // 202603 у календарі немає
        await CellAsync(w, May, "A", w.M3, 7m);

        var ds = await RunAsync(w, "Apply");
        var sum = Summary(ds);

        Assert.Equal(1, sum.noPeriodRow);
        Assert.Equal(0, sum.copiesWritten);
        Assert.Equal(0, sum.instancesCreated);
        Assert.Equal(7m, (await ReadCellAsync(w, May, "A", w.M3))!.Numeric);
        var row = Assert.Single(ds.Tables[3].Rows.Cast<DataRow>());
        Assert.Equal(Mar, Convert.ToInt32(row["ExpectedOwnerPeriod"], CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-457")]
    public async Task K_введений_не_місячний_стовпець_іде_в_якір_найменший_період_календаря()
    {
        // календар року починається з 202602 → якір 202602 (екземпляра ще немає — створюється)
        var w = await ArrangeAsync(
            instances: new() { [Mar] = ["A"], [May] = ["A"] },
            calendar: [.. Enumerable.Range(2, 11).Select(m => 202600 + m)]);
        await CellAsync(w, Mar, "A", w.Total, 5m);
        await CellAsync(w, May, "A", w.Total, 6m);
        await AuditAsync(w, Mar, "A", w.Total, DateTime.UtcNow.AddHours(-30), "5");
        await AuditAsync(w, May, "A", w.Total, DateTime.UtcNow.AddHours(-3), "6");

        var preview = Summary(await RunAsync(w, "Preview"));
        Assert.Equal(1, preview.ownerInstancesToCreate);

        var sum = Summary(await RunAsync(w, "Apply"));

        Assert.Equal(1, sum.copyPlanned);
        Assert.Equal(1, sum.conflicts);
        Assert.Equal(1, sum.instancesCreated);
        Assert.Equal(6m, (await ReadCellAsync(w, 202602, "A", w.Total))!.Numeric);

        // -Reanchor: календар доповнено 202601 (раніше за якір) → єдине джерело переїжджає в 202601
        await InsertPeriodAsync(w, Jan);
        await AddInstanceAsync(w, Jan, ["A"]);
        var re = Summary(await RunAsync(w, "Apply", scope: "NonMonth"));
        Assert.Equal(1, re.copiesWritten);
        Assert.Equal(6m, (await ReadCellAsync(w, Jan, "A", w.Total))!.Numeric);

        // повторний Reanchor — 0 змін
        var again = Summary(await RunAsync(w, "Apply", scope: "NonMonth"));
        Assert.Equal(0, again.copiesWritten + again.rowsCreated + again.instancesCreated);
    }

    // ─────────────────────────────────────────────── Purge

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-457")]
    public async Task P_Purge_видаляє_рівні_копії_лишає_відмінні_та_заблоковані()
    {
        var w = await ArrangeAsync(instances: new() { [Jan] = ["A"], [Mar] = ["A"], [Apr] = ["A"], [May] = ["A"] });
        await CellAsync(w, Mar, "A", w.M3, 10m);                         // власник
        await CellAsync(w, Jan, "A", w.M3, 10m);                         // рівна копія → видалити
        await CellAsync(w, Apr, "A", w.M3, 11m);                         // відмінна → лишити
        await CellAsync(w, May, "A", w.M3, 10m);                         // рівна, але період поданий → лишити
        await LockAsync(w, May, "submitted");

        var sum = Summary(await RunAsync(w, "Purge"));

        Assert.Equal(1, sum.copiesPurged);
        Assert.Equal(1, sum.purgeSkippedLocked);
        Assert.Equal(1, sum.purgeKeptDifferent);
        Assert.Null(await ReadCellAsync(w, Jan, "A", w.M3));
        Assert.Equal(11m, (await ReadCellAsync(w, Apr, "A", w.M3))!.Numeric);
        Assert.Equal(10m, (await ReadCellAsync(w, May, "A", w.M3))!.Numeric);
        Assert.Equal(10m, (await ReadCellAsync(w, Mar, "A", w.M3))!.Numeric);
        Assert.Equal(1, await MigrationAuditCountAsync(w));              // OldValue зберігається в аудиті
    }

    // ─────────────────────────────────────────────── (h) запобіжники

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-457")]
    public async Task H_SQL_відмовляє_у_записі_без_токена_обгортки()
    {
        var w = await ArrangeAsync(instances: new() { [Jan] = ["A"], [Mar] = ["A"], [May] = ["A"] });
        await CellAsync(w, May, "A", w.M3, 7m);

        var ex = await Assert.ThrowsAsync<SqlException>(() => RunAsync(w, "Apply", token: null));
        Assert.Contains("Apply-MonthColumnConsolidation.ps1", ex.Message, StringComparison.Ordinal);
        Assert.Null(await ReadCellAsync(w, Mar, "A", w.M3));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-457")]
    public async Task H_PowerShell_відмовляє_без_ConfirmBackup_JobsPaused_і_на_EcrLandUi_без_AllowLive()
    {
        var script = ScriptPath("Apply-MonthColumnConsolidation.ps1");
        var server = ServerName();

        // немає -ConfirmBackup (JobsPaused є): названа відмова ДО будь-якого запису
        var noBackup = await RunPowerShellAsync(script, "-Server", server, "-Database", sql.DatabaseName, "-Apply", "-JobsPaused");
        Assert.NotEqual(0, noBackup.ExitCode);
        Assert.Contains("-ConfirmBackup", noBackup.Output, StringComparison.Ordinal);

        // немає -JobsPaused
        var noJobs = await RunPowerShellAsync(script, "-Server", server, "-Database", sql.DatabaseName, "-Apply", "-ConfirmBackup", "E:\\none.bak");
        Assert.NotEqual(0, noJobs.ExitCode);
        Assert.Contains("-JobsPaused", noJobs.Output, StringComparison.Ordinal);

        // файл бекапу не існує
        var missing = await RunPowerShellAsync(script, "-Server", server, "-Database", sql.DatabaseName, "-Apply", "-JobsPaused",
            "-ConfirmBackup", Path.Combine(Path.GetTempPath(), "q17-no-such-file.bak"));
        Assert.NotEqual(0, missing.ExitCode);
        Assert.Contains("q17-no-such-file.bak", missing.Output, StringComparison.Ordinal);

        // живий EcrLandUi без -AllowLive — відмова ДО підключення до сервера (сервер навмисно неіснуючий)
        var live = await RunPowerShellAsync(script, "-Server", "no-such-server-q17", "-Database", "EcrLandUi", "-Apply", "-JobsPaused",
            "-ConfirmBackup", "E:\\none.bak");
        Assert.NotEqual(0, live.ExitCode);
        Assert.Contains("-AllowLive", live.Output, StringComparison.Ordinal);

        // PurgeCopies вимагає окремого підтвердження
        var purge = await RunPowerShellAsync(script, "-Server", "no-such-server-q17", "-Database", sql.DatabaseName, "-PurgeCopies", "-JobsPaused",
            "-ConfirmBackup", "E:\\none.bak");
        Assert.NotEqual(0, purge.ExitCode);
        Assert.Contains("-ConfirmPurge", purge.Output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-457")]
    public async Task H_PowerShell_Apply_із_справжнім_бекапом_до_запис_після_нуль_змін()
    {
        var w = await ArrangeAsync(instances: new() { [Jan] = ["A"], [Mar] = ["A"], [May] = ["A"] });
        await CellAsync(w, May, "A", w.M3, 7m);

        var dir = Path.Combine(DataRoot(), "q17-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var bak = Path.Combine(dir, "backup.bak");
        var json = Path.Combine(dir, "summary.json");
        try
        {
            await using (var cn = new SqlConnection(sql.ConnectionString))
            {
                await cn.OpenAsync();
                await using var cmd = cn.CreateCommand();
                cmd.CommandTimeout = 600;
                cmd.CommandText = $"BACKUP DATABASE [{sql.DatabaseName}] TO DISK = N'{bak}' WITH COPY_ONLY, INIT, COMPRESSION";
                await cmd.ExecuteNonQueryAsync();
            }

            var run = await RunPowerShellAsync(
                ScriptPath("Apply-MonthColumnConsolidation.ps1"), "-Server", ServerName(), "-Database", sql.DatabaseName,
                "-DocumentId", w.DocId.ToString(CultureInfo.InvariantCulture), "-Apply", "-JobsPaused", "-ConfirmBackup", bak,
                "-UserId", "1", "-OutDir", dir, "-SummaryJson", json);

            Assert.True(run.ExitCode == 0, run.Output);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(json));
            var root = doc.RootElement;
            Assert.Equal(1, root.GetProperty("before").GetProperty("copyPlanned").GetInt32());
            Assert.Equal(1, root.GetProperty("run").GetProperty("copiesWritten").GetInt32());
            Assert.Equal(0, root.GetProperty("after").GetProperty("copyPlanned").GetInt32());
            Assert.Equal(7m, (await ReadCellAsync(w, Mar, "A", w.M3))!.Numeric);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // бекап ще тримає служба SQL Server — не критично, каталог у E:\EcrData\q17-tests
            }
        }
    }

    // ═════════════════════════════════════════════ інфраструктура тесту

    private sealed record Summ(
        int copyPlanned, int conflicts, int noPeriodRow, int lockedTargets, int copyCells, int ownerInstancesToCreate,
        int mirrorRowsToCreate, int anchorRowsToCreate, int copiesWritten, int writtenToLocked, int rowsCreated, int instancesCreated,
        int copiesPurged, int purgeSkippedLocked, int purgeKeptDifferent);

    private sealed record CellRead(decimal? Numeric, bool IsEmpty, bool IsCalculated);

    private sealed record World(
        long DocId, int ProjectId, int SheetDefId, int TableDefId, int M3, int M5, int Total, int Calc,
        Dictionary<(int Period, string Key), long> RowIds, Dictionary<int, long> InstanceIds);

    private static Summ Summary(DataSet ds)
    {
        var r = ds.Tables[0].Rows[0];
        int V(string n) => Convert.ToInt32(r[n], CultureInfo.InvariantCulture);
        return new Summ(
            V("copyPlanned"), V("conflicts"), V("noPeriodRow"), V("lockedTargets"), V("copyCells"), V("ownerInstancesToCreate"),
            V("mirrorRowsToCreate"), V("anchorRowsToCreate"), V("copiesWritten"), V("writtenToLocked"), V("rowsCreated"),
            V("instancesCreated"), V("copiesPurged"), V("purgeSkippedLocked"), V("purgeKeptDifferent"));
    }

    private async Task<World> ArrangeAsync(Dictionary<int, string[]> instances, int[]? calendar = null)
    {
        var tag = Interlocked.Increment(ref _tag).ToString(CultureInfo.InvariantCulture);
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        calendar ??= [.. Enumerable.Range(1, 12).Select(m => 202600 + m)];

        await using var db = sql.CreateContext();
        var template = new Template(EcrCode.Create($"Q17T{tag}"), Name($"Q17 {tag}"), 1, now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();
        var version = new TemplateVersion(template.Id, $"1.0.0.{tag}", 1, now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();
        var sheet = new SheetDef(version.Id, EcrCode.Create($"Q17S{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();
        var table = new TableDef(sheet.Id, EcrCode.Create($"Q17B{tag}"), Name("Table"), 1, TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var label = new ColumnDef(table.Id, EcrCode.Create($"LBL{tag}"), Name("Label"), 1, CellDataType.String);
        var m3 = new ColumnDef(table.Id, EcrCode.Create($"M3_{tag}"), Name("Mar"), 2, CellDataType.Decimal);
        m3.SetMonth(true, 3);
        var m5 = new ColumnDef(table.Id, EcrCode.Create($"M5_{tag}"), Name("May"), 3, CellDataType.Decimal);
        m5.SetMonth(true, 5);
        var total = new ColumnDef(table.Id, EcrCode.Create($"TOT{tag}"), Name("Total"), 4, CellDataType.Decimal);
        var calc = new ColumnDef(table.Id, EcrCode.Create($"CLC{tag}"), Name("Calc"), 5, CellDataType.Decimal);
        foreach (var c in new[] { label, m3, m5, total, calc })
        {
            db.ColumnDefs.Add(c);
        }

        await db.SaveChangesAsync();

        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync();
        var project = new Project(
            EcrCode.Create($"Q17P{tag}"), Name($"Project {tag}"), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            version.Id, PeriodKind.Monthly, policyId, "Asia/Atyrau");
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        foreach (var pk in calendar)
        {
            await AddPeriodAsync(db, project.Id, pk);
        }

        var document = new Document(project.Id, $"Q17D{tag}", 1, now);
        db.Documents.Add(document);
        await db.SaveChangesAsync();

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var rowIds = new Dictionary<(int, string), long>();
        var instanceIds = new Dictionary<int, long>();
        foreach (var (period, keys) in instances)
        {
            var key = new PeriodKey(period);
            var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
            var firstRow = await loader.ReserveIdsAsync("doc.TableRowSeq", keys.Length, CancellationToken.None);
            db.TableInstances.Add(new TableInstance(key, instanceId, document.Id, table.Id, now));
            instanceIds[period] = instanceId;
            for (var i = 0; i < keys.Length; i++)
            {
                db.TableRows.Add(new TableRow(key, firstRow + i, instanceId, RowKey.Create(keys[i]), i + 1, now));
                rowIds[(period, keys[i])] = firstRow + i;
            }
        }

        await db.SaveChangesAsync();
        return new World(document.Id, project.Id, sheet.Id, table.Id, m3.Id, m5.Id, total.Id, calc.Id, rowIds, instanceIds);
    }

    private static async Task AddPeriodAsync(EcrDbContext db, int projectId, int periodKey)
    {
        var key = new PeriodKey(periodKey);
        db.Periods.Add(new Period(
            projectId, key, (byte)key.Sequence, new DateOnly(key.Year, key.Sequence, 1),
            new DateOnly(key.Year, key.Sequence, DateTime.DaysInMonth(key.Year, key.Sequence))));
        await db.SaveChangesAsync();
    }

    private async Task InsertPeriodAsync(World w, int periodKey)
    {
        await using var db = sql.CreateContext();
        await AddPeriodAsync(db, w.ProjectId, periodKey);
    }

    private async Task AddInstanceAsync(World w, int period, string[] keys)
    {
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var key = new PeriodKey(period);
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var firstRow = await loader.ReserveIdsAsync("doc.TableRowSeq", keys.Length, CancellationToken.None);
        await using var db = sql.CreateContext();
        db.TableInstances.Add(new TableInstance(key, instanceId, w.DocId, w.TableDefId, now));
        w.InstanceIds[period] = instanceId;
        for (var i = 0; i < keys.Length; i++)
        {
            db.TableRows.Add(new TableRow(key, firstRow + i, instanceId, RowKey.Create(keys[i]), i + 1, now));
            w.RowIds[(period, keys[i])] = firstRow + i;
        }

        await db.SaveChangesAsync();
    }

    private async Task LockAsync(World w, int period, string kind)
    {
        await using var db = sql.CreateContext();
        if (kind == "submitted")
        {
            var state = new ApprovalState(w.DocId, w.SheetDefId, period);
            state.Submit(userId: 5, DateTime.UtcNow);
            db.ApprovalStates.Add(state);
        }
        else
        {
            var p = await db.Periods.SingleAsync(x => x.ProjectId == w.ProjectId && x.PeriodKeyValue == period);
            p.AdvanceTo(PeriodState.Open, DateTime.UtcNow);
            p.AdvanceTo(PeriodState.Closed, DateTime.UtcNow);
        }

        await db.SaveChangesAsync();
    }

    private async Task CellAsync(World w, int period, string rowKey, int columnId, decimal value, bool calculated = false)
    {
        await ExecAsync(
            "INSERT doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) " +
            "VALUES (@p, @r, @c, @t, @v, @calc, 0)",
            ("@p", period), ("@r", w.RowIds[(period, rowKey)]), ("@c", columnId), ("@t", w.TableDefId), ("@v", value), ("@calc", calculated));
    }

    private async Task AuditAsync(World w, int period, string rowKey, int columnId, DateTime at, string newValue, string origin = "UserEdit")
    {
        await ExecAsync(
            "INSERT aud.CellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit, CorrelationId) " +
            "VALUES (@at, @p, @d, @r, @k, @c, NULL, @nv, 1, @o, 0, NULL)",
            ("@at", at), ("@p", period), ("@d", w.DocId), ("@r", w.RowIds[(period, rowKey)]), ("@k", rowKey), ("@c", columnId), ("@nv", newValue), ("@o", origin));
    }

    private async Task ExecAsync(string text, params (string Name, object Value)[] args)
    {
        await using var cn = new SqlConnection(sql.ConnectionString);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = text;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<CellRead?> ReadCellAsync(World w, int period, string rowKey, int columnId)
    {
        // рядок власника міг бути створений скриптом — адресуємо за RowKey, а не за відомим Id
        await using var cn = new SqlConnection(sql.ConnectionString);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText =
            "SELECT cv.ValueNumeric, cv.IsEmpty, cv.IsCalculated FROM doc.CellValue cv " +
            "JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId " +
            "JOIN doc.TableInstance ti ON ti.PeriodKey = r.PeriodKey AND ti.Id = r.TableInstanceId " +
            "WHERE cv.PeriodKey = @p AND ti.DocumentId = @d AND ti.TableDefId = @t AND r.RowKey = @k AND cv.ColumnDefId = @c";
        cmd.Parameters.AddWithValue("@p", period);
        cmd.Parameters.AddWithValue("@d", w.DocId);
        cmd.Parameters.AddWithValue("@t", w.TableDefId);
        cmd.Parameters.AddWithValue("@k", rowKey);
        cmd.Parameters.AddWithValue("@c", columnId);
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync())
        {
            return null;
        }

        return new CellRead(rd.IsDBNull(0) ? null : rd.GetDecimal(0), rd.GetBoolean(1), rd.GetBoolean(2));
    }

    private async Task<decimal?> OwnerValueAsync(World w, int period, string rowKey, int columnId)
        => (await ReadCellAsync(w, period, rowKey, columnId))?.Numeric;

    private async Task<int> RowCountAsync(World w, int period) => (await RowKeysAsync(w, period)).Length;

    private async Task<string[]> RowKeysAsync(World w, int period)
    {
        await using var cn = new SqlConnection(sql.ConnectionString);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText =
            "SELECT r.RowKey FROM doc.TableRow r JOIN doc.TableInstance ti ON ti.PeriodKey = r.PeriodKey AND ti.Id = r.TableInstanceId " +
            "WHERE r.PeriodKey = @p AND ti.DocumentId = @d AND ti.TableDefId = @t AND r.IsDeleted = 0 ORDER BY r.RowKey";
        cmd.Parameters.AddWithValue("@p", period);
        cmd.Parameters.AddWithValue("@d", w.DocId);
        cmd.Parameters.AddWithValue("@t", w.TableDefId);
        var keys = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            keys.Add(rd.GetString(0));
        }

        return [.. keys];
    }

    private async Task<int> MigrationAuditCountAsync(World w)
    {
        await using var cn = new SqlConnection(sql.ConnectionString);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = @d AND Origin = N'Migration' AND NewValue IS NULL AND OldValue IS NOT NULL";
        cmd.Parameters.AddWithValue("@d", w.DocId);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Знімок стану документа (екземпляри, рядки, комірки, аудит) для перевірки «0 змін».</summary>
    private async Task<string> SnapshotAsync(World w)
    {
        await using var cn = new SqlConnection(sql.ConnectionString);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText =
            "SELECT (SELECT COUNT(*) FROM doc.TableInstance WHERE DocumentId = @d), " +
            "(SELECT COUNT(*) FROM doc.TableRow r JOIN doc.TableInstance ti ON ti.PeriodKey = r.PeriodKey AND ti.Id = r.TableInstanceId WHERE ti.DocumentId = @d), " +
            "(SELECT COUNT(*) FROM doc.CellValue cv JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId " +
            "  JOIN doc.TableInstance ti ON ti.PeriodKey = r.PeriodKey AND ti.Id = r.TableInstanceId WHERE ti.DocumentId = @d), " +
            "(SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = @d), " +
            "(SELECT ISNULL(SUM(CAST(CHECKSUM(cv.PeriodKey, cv.TableRowId, cv.ColumnDefId, cv.ValueNumeric, cv.IsEmpty) AS bigint)), 0) FROM doc.CellValue cv " +
            "  JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId " +
            "  JOIN doc.TableInstance ti ON ti.PeriodKey = r.PeriodKey AND ti.Id = r.TableInstanceId WHERE ti.DocumentId = @d)";
        cmd.Parameters.AddWithValue("@d", w.DocId);
        await using var rd = await cmd.ExecuteReaderAsync();
        await rd.ReadAsync();
        return string.Join('|', Enumerable.Range(0, rd.FieldCount).Select(i => Convert.ToString(rd.GetValue(i), CultureInfo.InvariantCulture)));
    }

    /// <summary>Виконує <c>Preview-MonthColumnConsolidation.sql</c> з підставленим блоком параметрів (як PowerShell-обгортка).</summary>
    private async Task<DataSet> RunAsync(
        World w, string mode, bool writeLocked = false, string? runId = null, string scope = "All", int? year = 2026,
        string? token = "Q17-APPLY-v1")
    {
        var text = await File.ReadAllTextAsync(ScriptPath("Preview-MonthColumnConsolidation.sql"));
        var begin = text.IndexOf("-- BEGIN PARAMS", StringComparison.Ordinal);
        var end = text.IndexOf("-- END PARAMS", StringComparison.Ordinal) + "-- END PARAMS".Length;
        var write = mode != "Preview";
        var header = string.Create(CultureInfo.InvariantCulture, $"""
            DECLARE @Mode        varchar(10)  = '{mode}';
            DECLARE @DocumentId  bigint       = {w.DocId};
            DECLARE @Year        int          = {(year.HasValue ? year.Value.ToString(CultureInfo.InvariantCulture) : "NULL")};
            DECLARE @ColumnScope varchar(10)  = '{scope}';
            DECLARE @RunId       nvarchar(64) = {(write ? $"N'{runId ?? "q17-test-run"}'" : "NULL")};
            DECLARE @UserId      int          = {(write ? "1" : "NULL")};
            DECLARE @WriteLocked bit          = {(writeLocked ? 1 : 0)};
            DECLARE @AllowLive   bit          = 0;
            DECLARE @ApplyToken  varchar(40)  = {(token is null || !write ? "NULL" : $"'{token}'")};
            """);

        await using var cn = new SqlConnection(sql.ConnectionString);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = text[..begin] + header + text[end..];
        cmd.CommandTimeout = 300;
        var ds = new DataSet();
        using var adapter = new SqlDataAdapter(cmd);
        adapter.Fill(ds);
        return ds;
    }

    private static string ScriptPath(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ecr.sln")))
        {
            dir = dir.Parent;
        }

        var root = dir?.FullName ?? throw new InvalidOperationException("Не знайдено Ecr.sln від каталогу збірки вгору.");
        return Path.Combine(root, "tools", "q17", file);
    }

    private static string DataRoot()
        => Environment.GetEnvironmentVariable("ECR_TEST_DATA_PATH") ?? (Directory.Exists("E:\\") ? "E:\\EcrData" : Path.GetTempPath());

    private string ServerName()
        => new SqlConnectionStringBuilder(sql.ConnectionString).DataSource;

    private static async Task<(int ExitCode, string Output)> RunPowerShellAsync(string script, params string[] args)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
