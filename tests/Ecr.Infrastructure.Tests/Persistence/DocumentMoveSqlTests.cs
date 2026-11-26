// tests/Ecr.Infrastructure.Tests/Persistence/DocumentMoveSqlTests.cs
using System.Data;
using System.Diagnostics;
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// b14 document-move T1 (D-577): <c>tools/document-move</c> на справжньому SQL Server на синтетичному відтворенні фактів
/// дизайну §0: місячний LANDUI (січень–вересень закриті, жовтень Grace, листопад Open, грудень Scheduled) з документом
/// Land і прив'язаним шаблоном HSE400; річний HSE400_2026 з документом «рік + місячні колонки», де ВСІ місяці лежать в
/// одному екземплярі 202601, і актуальним прогоном з F_ на ньому. Текст скрипта складає та сама бібліотека, що й
/// обгортка (<c>DocumentMove.Lib.ps1</c> через PowerShell) — одне джерело.
/// </summary>
/// <remarks>
/// МУТАЦІЯ (локально, 26.11): у Move-Document.sql не переключати calc.CalculationRun документа — червоні «Apply…»
/// (projectMismatch → 51814) і «Паритет…» (F_ зникають з читання складу).
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "b14-document-move-T1")]
public sealed class DocumentMoveSqlTests(SqlServerFixture sql)
{
    private const int Jan = 202601;
    private static readonly DateTime Now = new(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Apply_річний_документ_переноситься_в_місячний_проєкт_місяці_у_власних_періодах()
    {
        var w = await ArrangeAsync();

        var report = Report(await MoveAsync(w, "Apply"));

        Assert.Equal(1, Int(report, "RunsRekeyed"));
        Assert.Equal(0, Int(report, "Q17Conflicts"));
        Assert.Equal(10, Int(report, "Q17InstancesCreated"));           // 202602..202611 (грудень без даних)
        Assert.Equal(20, Int(report, "Q17CopiesWritten"));              // 2 рядки × M02..M11
        Assert.Equal(16, Int(report, "Q17WrittenToLocked"));            // лютий–вересень закриті: системне перенесення
        Assert.Equal(20, Int(report, "NonOwnerCopiesRemoved"));         // січневі копії M02..M11
        Assert.Equal(0, Int(report, "NonOwnerCopiesLeft"));
        Assert.Equal(0, Int(report, "OwnerMismatch"));
        Assert.Equal(0, Int(report, "ProjectMismatch"));
        Assert.Equal(1, Int(report, "TemplateIdWasNull"));

        await using var db = Db();
        var doc = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == w.DocumentId);
        Assert.Equal(w.TargetProjectId, doc.ProjectId);
        Assert.Equal(w.TemplateId, doc.TemplateId);
        Assert.All(await db.CalculationRuns.AsNoTracking().Where(r => r.DocumentId == w.DocumentId).ToListAsync(),
            r => Assert.Equal(w.TargetProjectId, r.ProjectId));

        // Кожне місячне значення — у своєму періоді, рівне вихідному; інших копій немає.
        var cells = await MonthCellsAsync(w);
        Assert.Equal(22, cells.Count);
        Assert.All(cells, c =>
        {
            Assert.Equal((2026 * 100) + c.Month, c.Period);
            Assert.Equal(Value(c.Row, c.Month), c.Value);
        });

        Assert.Equal(1, await ScalarAsync(
            $"SELECT COUNT(*) FROM aud.SecurityEvent WHERE EventType = N'DocumentMovedToProject' AND CorrelationId = N'{w.Correlation}'"));
        Assert.Equal(40, await ScalarAsync(
            $"SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = {w.DocumentId} AND Origin = N'Migration' AND CorrelationId = N'{w.Correlation}'"));
    }

    [Fact]
    public async Task Preview_звітує_те_саме_що_Apply_і_нічого_не_змінює()
    {
        var w = await ArrangeAsync();
        var before = await StateAsync(w);

        var preview = Report(await MoveAsync(w, "Preview"));

        Assert.Equal(before, await StateAsync(w));
        Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM aud.SecurityEvent WHERE CorrelationId = N'{w.Correlation}'"));

        var apply = Report(await MoveAsync(w, "Apply"));
        foreach (var column in new[]
                 {
                     "RunsRekeyed", "Q17Conflicts", "Q17CopiesWritten", "Q17WrittenToLocked", "Q17InstancesCreated",
                     "Q17RowsCreated", "NonOwnerCopiesRemoved", "NonOwnerCopiesLeft", "OwnerMismatch", "ProjectMismatch",
                     "CellsBefore", "CellsAfter", "ChecksumBefore", "ChecksumAfter",
                 })
        {
            Assert.Equal(preview[column], apply[column]);
        }
    }

    [Theory]
    [InlineData("sameProject")]
    [InlineData("keyClash")]
    [InlineData("singleDocument")]
    [InlineData("versionMismatch")]
    [InlineData("periodMissing")]
    [InlineData("workflowKindMismatch")]
    [InlineData("archived")]
    [InlineData("jobsRunning")]
    [InlineData("projectRuns")]
    public async Task Відмова_нічого_не_змінює(string blocker)
    {
        var w = await ArrangeAsync();
        await BreakAsync(w, blocker);
        var before = await StateAsync(w);
        try
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => MoveAsync(w, "Apply", target: blocker == "sameProject" ? w.SourceCode : null));

            Assert.Equal(51810, error.Number);
            Assert.Contains(blocker, error.Message, StringComparison.Ordinal);
            Assert.Equal(before, await StateAsync(w));
        }
        finally
        {
            await ExecAsync("DELETE FROM itg.JobProgress WHERE JobId LIKE N'b14move-%';");
            await ExecAsync($"DELETE FROM calc.CalculationRun WHERE ProjectId = {w.SourceProjectId} AND DocumentId IS NULL;");
        }
    }

    [Fact]
    public async Task Конфлікт_Q17_відмовляє_і_нічого_не_змінює()
    {
        // Окремий екземпляр 202603 документа з ІНШИМ значенням M3, ніж січнева копія: Q-17 — конфлікт, перенесення — ні.
        var w = await ArrangeAsync();
        await ExtraInstanceAsync(w, 202603, w.MonthColumns[2], 99m);
        var before = await StateAsync(w);

        var error = await Assert.ThrowsAsync<SqlException>(() => MoveAsync(w, "Apply"));

        Assert.Equal(51811, error.Number);
        Assert.Equal(before, await StateAsync(w));
    }

    [Fact]
    public async Task Apply_без_причини_і_скрипт_без_обгортки_відмовляють()
    {
        var w = await ArrangeAsync();

        Assert.Equal(51806, (await Assert.ThrowsAsync<SqlException>(() => MoveAsync(w, "Apply", reason: ""))).Number);

        var raw = await File.ReadAllTextAsync(ToolPath("Move-Document.sql"));
        Assert.Equal(51803, (await Assert.ThrowsAsync<SqlException>(() => RunAsync(raw))).Number);
    }

    [Fact]
    public async Task Паритет_склад_HSE400_для_Land_і_результати_Land_до_й_після_перенесення_однакові()
    {
        var w = await ArrangeAsync();
        var compositionBefore = await CompositionAsync(w);
        var landBefore = await LandChecksumAsync(w);
        Assert.Contains(compositionBefore, r => r.Value == FValue(5));   // F_ з прогону видно

        await MoveAsync(w, "Apply");

        Assert.Equal(compositionBefore, await CompositionAsync(w));
        Assert.Equal(landBefore, await LandChecksumAsync(w));

        // Контроль необхідності переключення прогонів: прогін, лишений на старому проєкті, ховає F_ від читання.
        await ExecAsync($"UPDATE calc.CalculationRun SET ProjectId = {w.SourceProjectId} WHERE DocumentId = {w.DocumentId};");
        Assert.DoesNotContain(await CompositionAsync(w), r => r.Value == FValue(5));
        await ExecAsync($"UPDATE calc.CalculationRun SET ProjectId = {w.TargetProjectId} WHERE DocumentId = {w.DocumentId};");
    }

    [Fact]
    public async Task Reverse_після_перерахунку_й_reopen_повертає_документ_як_у_pre_image()
    {
        var w = await ArrangeAsync();
        var preImage = $"EcrMovePre_{w.Correlation}";
        try
        {
            var manifest = await RunAsync(await ComposeAsync("PreImage", w));
            Assert.Equal(w.DocumentId, Convert.ToInt64(manifest.Tables[0].Rows[0]["DocumentId"], CultureInfo.InvariantCulture));
            var before = await StateAsync(w);

            await MoveAsync(w, "Apply");

            // W-5 після перенесення: прогін 202603 з результатом, document reopen 202601, D-560 прибрав старий прогін.
            await PostMoveRecalcAsync(w);
            Assert.NotEqual(before, await StateAsync(w));

            var preview = await RunAsync(await ComposeAsync("ReversePreview", w));
            Assert.Equal("Preview", preview.Tables[^1].Rows[0]["ReverseReport"]);
            Assert.NotEqual(before, await StateAsync(w));

            var reverse = (await RunAsync(await ComposeAsync("Reverse", w))).Tables[^1].Rows[0];
            Assert.Equal(1, Convert.ToInt32(reverse["DeletedRuns"], CultureInfo.InvariantCulture));
            Assert.Equal(1, Convert.ToInt32(reverse["RestoredRuns"], CultureInfo.InvariantCulture));
            Assert.Equal(1, Convert.ToInt32(reverse["DeletedReopens"], CultureInfo.InvariantCulture));
            Assert.Equal(before, await StateAsync(w));
        }
        finally
        {
            await ExecAsync($"IF DB_ID(N'{preImage}') IS NOT NULL BEGIN ALTER DATABASE [{preImage}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{preImage}]; END", master: true);
        }
    }

    [Fact]
    public async Task Документ_екземпляр_на_період_переноситься_без_зміни_комірок()
    {
        // HSE401 93: таблиця «екземпляр на період», усі дані в 202601 — переноситься лише ключ проєкту і прогони.
        var w = await ArrangeAsync(layout: TableLayoutKind.PerPeriodInstance);
        var before = await CellChecksumAsync(w);

        var report = Report(await MoveAsync(w, "Apply"));

        Assert.Equal(0, Int(report, "Q17CopiesWritten"));
        Assert.Equal(0, Int(report, "NonOwnerCopiesRemoved"));
        Assert.Equal(report["ChecksumBefore"], report["ChecksumAfter"]);
        Assert.Equal(before, await CellChecksumAsync(w));
    }

    // ─────────────────────────────── світ ───────────────────────────────

    private sealed record World(
        TestDocumentBuilder Builder, long DocumentId, int TemplateId, int SourceProjectId, string SourceCode, int TargetProjectId,
        string TargetCode, long Instance, IReadOnlyList<long> Rows, IReadOnlyList<int> MonthColumns, int TableDefId,
        long RunId, long LandDocumentId, string Correlation, string FRowKey, string TemplateCode, int SheetDefId, int LandVersionId);

    private static decimal Value(int row, int month) => (row * 100) + month + 0.25m;

    private static decimal FValue(int month) => 1000 + month;

    private async Task<World> ArrangeAsync(TableLayoutKind layout = TableLayoutKind.MonthsInColumns)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(Jan, columnCount: 1, rowCount: 2, layoutKind: layout, monthColumns: 12);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await using var db = Db();
        var source = await db.Projects.SingleAsync(p => p.Id == doc.ProjectId);
        var templateCode = await db.Templates.Where(t => t.Id == doc.TemplateId).Select(t => t.Code).SingleAsync();
        await ExecAsync($"""
            UPDATE doc.Project SET PeriodKind = {(int)PeriodKind.Yearly} WHERE Id = {doc.ProjectId};
            UPDATE cfg.TemplateVersion SET Layout = {(int)TemplateLayout.YearWithMonthColumns} WHERE Id = {doc.TemplateVersionId};
            UPDATE cfg.Template SET SingleDocumentPerProject = 1 WHERE Id = {doc.TemplateId};
            UPDATE itg.JobProgress SET State = N'Failed' WHERE State IN (N'Queued', N'Running');
            """);

        // F_-рядок у шаблоні (розрахунковий рядок HSE400; значень у комірках немає — лише результати прогону).
        var fKey = $"F_R1_{doc.TableDefId}";
        db.RowDefs.Add(new RowDef(doc.TableDefId, RowKey.Create(fKey), 10, Name(fKey), RowKind.Item));

        // Місячний LANDUI: основний шаблон — Land, HSE400 прив'язано (doc.ProjectTemplate), календар 2026 зі станами §0.
        var land = new Template(EcrCode.Create($"LND{tag}"), Name("Land"), 1, Now);
        db.Templates.Add(land);
        await db.SaveChangesAsync();
        var landVersion = new TemplateVersion(land.Id, $"1.0.0.{tag}", 1, Now);
        db.TemplateVersions.Add(landVersion);
        await db.SaveChangesAsync();
        var target = new Project(
            EcrCode.Create($"LANDUI{tag}"), Name("LANDUI"), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            landVersion.Id, PeriodKind.Monthly, source.PeriodPolicyId, "Asia/Atyrau");
        db.Projects.Add(target);
        await db.SaveChangesAsync();
        for (var m = 1; m <= 12; m++)
        {
            var period = new Period(target.Id, PeriodKey.Create(2026, m), (byte)m, new DateOnly(2026, m, 1), new DateOnly(2026, m, DateTime.DaysInMonth(2026, m)));
            if (m <= 11)
            {
                period.AdvanceTo(PeriodState.Open, Now);
            }

            if (m <= 10)
            {
                period.AdvanceTo(PeriodState.Grace, Now);
            }

            if (m <= 9)
            {
                period.AdvanceTo(PeriodState.Closed, Now);
            }

            db.Periods.Add(period);
        }

        var landDoc = new Document(target.Id, $"LAND{tag}", 1, Now);
        db.Documents.Add(landDoc);
        await db.SaveChangesAsync();
        await ExecAsync($"INSERT doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId) VALUES ({target.Id}, {doc.TemplateId}, {doc.TemplateVersionId});");

        // Усі місяці M01..M11 — в одному екземплярі 202601 (як doc 98 на live).
        var values = new List<string>();
        for (var r = 0; r < doc.RowIds.Count; r++)
        {
            for (var m = 1; m <= 11; m++)
            {
                values.Add(string.Create(CultureInfo.InvariantCulture,
                    $"({Jan}, {doc.RowIds[r]}, {doc.MonthColumnDefIds[m - 1]}, {doc.TableDefId}, {Value(r + 1, m)}, 0, 0)"));
            }
        }

        await ExecAsync("INSERT doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES "
                        + string.Join(", ", values) + ";");

        // Актуальні прогони: HSE400 (F_ на січневому екземплярі, всі місяці) і Land (510 результатів на live — тут 3).
        var methodology = new Methodology(EcrCode.Create($"MV{tag}"), Name("HSE400"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();
        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        var run = new CalculationRun(doc.ProjectId, Jan, null, Now, doc.DocumentId);
        run.Complete("Succeeded", Now, "{}", errorMessage: null);
        run.MakeCurrent();
        var landRun = new CalculationRun(target.Id, 202611, null, Now, landDoc.Id);
        landRun.Complete("Succeeded", Now, "{}", errorMessage: null);
        landRun.MakeCurrent();
        db.CalculationRuns.AddRange(run, landRun);
        await db.SaveChangesAsync();

        // Коди виходів F_ = коди місячних колонок (як у HSE400: результат місяця m — у колонці M{m}).
        var monthCodes = await db.ColumnDefs.AsNoTracking().Where(c => doc.MonthColumnDefIds.Contains(c.Id))
            .ToDictionaryAsync(c => (int)c.MonthNumber!, c => c.Code);
        var unit = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        var results = new List<string>();
        for (var m = 1; m <= 11; m++)
        {
            results.Add(string.Create(CultureInfo.InvariantCulture,
                $"(NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {version.Id}, {Jan}, {doc.DocumentId}, N'{fKey}', N'{monthCodes[m]}', {FValue(m)}, {unit}, {doc.TableInstanceId})"));
        }

        for (var i = 1; i <= 3; i++)
        {
            results.Add(string.Create(CultureInfo.InvariantCulture,
                $"(NEXT VALUE FOR calc.CalculationResultSeq, {landRun.Id}, {version.Id}, 202611, {landDoc.Id}, N'LR{i}', N'E{i}', {i * 7.5m}, {unit}, NULL)"));
        }

        await ExecAsync("INSERT calc.CalculationResult (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId, TableInstanceId) VALUES "
                        + string.Join(", ", results) + ";");

        return new World(
            builder, doc.DocumentId, doc.TemplateId, doc.ProjectId, source.Code, target.Id, target.Code, doc.TableInstanceId, doc.RowIds,
            doc.MonthColumnDefIds, doc.TableDefId, run.Id, landDoc.Id, $"b14t{tag}", fKey, templateCode, doc.SheetDefId, landVersion.Id);
    }

    private async Task BreakAsync(World w, string blocker)
    {
        switch (blocker)
        {
            case "sameProject":
                break;
            case "keyClash":
            case "singleDocument":
            {
                // keyClash — той самий ключ під основним шаблоном цілі (Land); singleDocument — інший ключ, шаблон HSE400.
                await using var db = Db();
                var key = await db.Documents.Where(d => d.Id == w.DocumentId).Select(d => d.BusinessKey).SingleAsync();
                db.Documents.Add(blocker == "keyClash"
                    ? new Document(w.TargetProjectId, key, 1, Now)
                    : new Document(w.TargetProjectId, $"OTHER-{w.Correlation}", 1, Now, w.TemplateId));
                await db.SaveChangesAsync();
                break;
            }

            case "versionMismatch":
            {
                // Ціль на ІНШІЙ версії шаблону HSE400, ніж джерело (спершу migrate-version, потім перенесення).
                await using var db = Db();
                var other = new TemplateVersion(w.TemplateId, $"9.9.9.{w.Correlation[^4..]}", 1, Now);
                db.TemplateVersions.Add(other);
                await db.SaveChangesAsync();
                await ExecAsync($"UPDATE doc.ProjectTemplate SET CurrentTemplateVersionId = {other.Id} WHERE ProjectId = {w.TargetProjectId} AND TemplateId = {w.TemplateId};");
                break;
            }
            case "periodMissing":
                await ExecAsync($"DELETE FROM doc.Period WHERE ProjectId = {w.TargetProjectId} AND PeriodKey = 202605;");
                break;
            case "workflowKindMismatch":
            {
                await using var db = Db();
                db.ApprovalStates.Add(new Ecr.Domain.Entities.Workflow.ApprovalState(w.DocumentId, w.SheetDefId, Jan));
                await db.SaveChangesAsync();
                break;
            }

            case "archived":
                await ExecAsync($"UPDATE doc.Project SET IsArchiving = 1 WHERE Id = {w.SourceProjectId};");
                break;
            case "jobsRunning":
            {
                await using var db = Db();
                db.JobProgresses.Add(new Ecr.Domain.Entities.Integration.JobProgress($"b14move-{w.Correlation}", "Recalc", Now));
                await db.SaveChangesAsync();
                break;
            }

            case "projectRuns":
            {
                await using var db = Db();
                db.CalculationRuns.Add(new CalculationRun(w.SourceProjectId, Jan, null, Now));
                await db.SaveChangesAsync();
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(blocker), blocker, null);
        }
    }

    private async Task ExtraInstanceAsync(World w, int period, int column, decimal value)
    {
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instance = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var row = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);
        await ExecAsync($"""
            INSERT doc.TableInstance (PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt) VALUES ({period}, {instance}, {w.DocumentId}, {w.TableDefId}, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT doc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, IsOrphaned, ModifiedAt)
            SELECT {period}, {row}, {instance}, RowKey, RowDefId, Ordinal, 0, 0, SYSUTCDATETIME() FROM doc.TableRow WHERE PeriodKey = {Jan} AND Id = {w.Rows[0]};
            INSERT doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
            VALUES ({period}, {row}, {column}, {w.TableDefId}, {value.ToString(CultureInfo.InvariantCulture)}, 0, 0);
            """);
    }

    /// <summary>W-5 на копії: новий актуальний прогін 202603, D-560 прибрав старий, document reopen січня.</summary>
    private async Task PostMoveRecalcAsync(World w)
    {
        long newRun;
        await using (var db = Db())
        {
            var run = new CalculationRun(w.TargetProjectId, 202603, null, Now, w.DocumentId);
            run.Complete("Succeeded", Now, "{}", errorMessage: null);
            run.MakeCurrent();
            db.CalculationRuns.Add(run);
            await db.SaveChangesAsync();
            newRun = run.Id;
        }

        await ExecAsync($"""
            DECLARE @run bigint = {newRun};
            INSERT calc.CalculationResult (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
            SELECT NEXT VALUE FOR calc.CalculationResultSeq, @run, MethodologyVersionId, 202603, DocumentId, SourceRowKey, OutputCode, Value, UnitId
            FROM calc.CalculationResult WHERE CalculationRunId = {w.RunId} AND OutputCode LIKE N'%3[_]%';
            DELETE FROM calc.CalculationResult WHERE CalculationRunId = {w.RunId};
            DELETE FROM calc.CalculationRun WHERE Id = {w.RunId};
            INSERT doc.DocumentPeriodReopen (DocumentId, PeriodKey, OpenUntil, Reason, OpenedByUserId, OpenedAt)
            VALUES ({w.DocumentId}, {Jan}, DATEADD(day, 1, SYSUTCDATETIME()), N'b14 move: recalc HSE400 by month', 1, SYSUTCDATETIME());
            """);
    }

    // ─────────────────────────────── склад і контрольні суми ───────────────────────────────

    private async Task<List<(int Year, int Month, string Stream, string Indicator, decimal Value)>> CompositionAsync(World w)
    {
        await ExecAsync($"UPDATE cfg.TableDef SET Code = N'FG_Internal' WHERE Id = {w.TableDefId};");
        try
        {
            await using var db = Db();
            var loader = new Hse400CompositionSnapshotLoader(
                Substitute.For<IRegistrySnapshotLoader>(), db, new CalculationResultStore(db, new TestClock(Now)), enabled: true,
                registryCode: "T1_UNUSED", templateCode: w.TemplateCode, cache: null);
            var (rows, _) = await loader.ReadDocumentsAsync(approvedOnly: false, CancellationToken.None);
            return [.. rows.Select(r => (r.Year, r.Month, r.Stream, r.Indicator, r.Value)).OrderBy(r => r.Month).ThenBy(r => r.Indicator, StringComparer.Ordinal)];
        }
        finally
        {
            await ExecAsync($"UPDATE cfg.TableDef SET Code = N'TBL{w.TableDefId}' WHERE Id = {w.TableDefId};");
        }
    }

    private Task<long> LandChecksumAsync(World w)
        => ScalarAsync($"SELECT ISNULL(SUM(CAST(CHECKSUM(r.PeriodKey, r.SourceRowKey, r.OutputCode, r.Value) AS bigint)), 0) + 1000000 * COUNT(*) FROM calc.CalculationResult r JOIN calc.CalculationRun ru ON ru.Id = r.CalculationRunId WHERE ru.DocumentId = {w.LandDocumentId} AND ru.Status = N'Current'");

    private Task<long> CellChecksumAsync(World w)
        => ScalarAsync($"""
            SELECT ISNULL(SUM(CAST(CHECKSUM(cv.PeriodKey, cv.TableRowId, cv.ColumnDefId, cv.ValueNumeric, cv.IsEmpty) AS bigint)), 0)
            FROM doc.CellValue cv JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
            JOIN doc.TableInstance i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId WHERE i.DocumentId = {w.DocumentId}
            """);

    /// <summary>Відбиток стану документа: проєкт, шаблон, екземпляри, рядки, комірки, прогони, результати, reopen.</summary>
    private async Task<string> StateAsync(World w)
    {
        var parts = new List<long>
        {
            await ScalarAsync($"SELECT ProjectId FROM doc.Document WHERE Id = {w.DocumentId}"),
            await ScalarAsync($"SELECT ISNULL(TemplateId, -1) FROM doc.Document WHERE Id = {w.DocumentId}"),
            await ScalarAsync($"SELECT COUNT(*) FROM doc.TableInstance WHERE DocumentId = {w.DocumentId}"),
            await ScalarAsync($"SELECT COUNT(*) FROM doc.TableRow r JOIN doc.TableInstance i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId WHERE i.DocumentId = {w.DocumentId}"),
            await CellChecksumAsync(w),
            await ScalarAsync($"SELECT ISNULL(SUM(Id + ProjectId), 0) FROM calc.CalculationRun WHERE DocumentId = {w.DocumentId}"),
            await ScalarAsync($"SELECT COUNT(*) FROM calc.CalculationResult WHERE DocumentId = {w.DocumentId}"),
            await ScalarAsync($"SELECT COUNT(*) FROM doc.DocumentPeriodReopen WHERE DocumentId = {w.DocumentId}"),
        };
        return string.Join('|', parts);
    }

    private async Task<List<(int Period, int Row, int Month, decimal Value)>> MonthCellsAsync(World w)
    {
        var ds = await RunAsync($"""
            SELECT cv.PeriodKey, r.Ordinal, c.MonthNumber, cv.ValueNumeric
            FROM doc.CellValue cv JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
            JOIN doc.TableInstance i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
            JOIN cfg.ColumnDef c ON c.Id = cv.ColumnDefId AND c.IsMonthColumn = 1
            WHERE i.DocumentId = {w.DocumentId} AND cv.IsEmpty = 0 AND r.IsDeleted = 0
            """);
        return [.. ds.Tables[0].Rows.Cast<DataRow>().Select(r => (
            Convert.ToInt32(r[0], CultureInfo.InvariantCulture), Convert.ToInt32(r[1], CultureInfo.InvariantCulture),
            Convert.ToInt32(r[2], CultureInfo.InvariantCulture), Convert.ToDecimal(r[3], CultureInfo.InvariantCulture)))];
    }

    // ─────────────────────────────── запуск ───────────────────────────────

    private async Task<DataSet> MoveAsync(World w, string mode, string? target = null, string reason = "b14 test: one project (human 25.11)")
        => await RunAsync(await ComposeAsync(mode, w, target, reason));

    /// <summary>Текст скрипта — з <c>DocumentMove.Lib.ps1</c> (те саме складання, що й у <c>Move-Document.ps1</c>).</summary>
    private static async Task<string> ComposeAsync(string mode, World w, string? target = null, string reason = "b14 test: one project (human 25.11)")
    {
        var dir = Directory.CreateTempSubdirectory("ecr-move-");
        try
        {
            var output = Path.Combine(dir.FullName, "script.sql");
            var call = mode switch
            {
                "PreImage" => $"New-PreImageScript ([IO.File]::ReadAllText('{ToolPath("Export-PreImage.sql")}')) '{w.Correlation}' @({w.DocumentId}) $null",
                "Reverse" or "ReversePreview" => $"New-ReverseScript -ReverseSql ([IO.File]::ReadAllText('{ToolPath("Reverse-Move.sql")}')) -Mode {(mode == "Reverse" ? "Apply" : "Preview")} -DocumentId {w.DocumentId} -CorrelationId '{w.Correlation}' -UserId 1",
                _ => $"New-MoveScript -MoveSql ([IO.File]::ReadAllText('{ToolPath("Move-Document.sql")}')) -Q17Sql ([IO.File]::ReadAllText('{Q17Path()}')) -Mode {mode} -DocumentId {w.DocumentId} -TargetProjectCode '{target ?? w.TargetCode}' -CorrelationId '{w.Correlation}' -Reason '{reason}' -UserId 1",
            };
            var script = Path.Combine(dir.FullName, "compose.ps1");
            await File.WriteAllTextAsync(script, $". '{ToolPath("DocumentMove.Lib.ps1")}'\n[IO.File]::WriteAllText('{output}', ({call}))\n");
            var (code, log) = await PowerShellAsync(script);
            Assert.True(code == 0, log);
            return await File.ReadAllTextAsync(output);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private async Task<DataSet> RunAsync(string text)
    {
        await using var cn = new SqlConnection(sql.ConnectionString);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = text;
        cmd.CommandTimeout = 300;
        var ds = new DataSet();
        using var adapter = new SqlDataAdapter(cmd);
        adapter.Fill(ds);
        return ds;
    }

    private static DataRow Report(DataSet ds)
        => ds.Tables.Cast<DataTable>().Single(t => t.Columns.Contains("MoveReport")).Rows[0];

    private static int Int(DataRow row, string column) => Convert.ToInt32(row[column], CultureInfo.InvariantCulture);

    private async Task<long> ScalarAsync(string text)
    {
        await using var cn = new SqlConnection(sql.ConnectionString);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = text;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task ExecAsync(string text, bool master = false)
    {
        var cs = master ? new SqlConnectionStringBuilder(sql.ConnectionString) { InitialCatalog = "master" }.ConnectionString : sql.ConnectionString;
        await using var cn = new SqlConnection(cs);
        await cn.OpenAsync();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = text;
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync();
    }

    private EcrDbContext Db() => sql.CreateContext();

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ecr.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Не знайдено Ecr.sln від каталогу збірки вгору.");
    }

    private static string ToolPath(string file) => Path.Combine(Root(), "tools", "document-move", file);

    private static string Q17Path() => Path.Combine(Root(), "tools", "q17", "Preview-MonthColumnConsolidation.sql");

    private static async Task<(int ExitCode, string Output)> PowerShellAsync(string script)
    {
        var psi = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh")
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

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
