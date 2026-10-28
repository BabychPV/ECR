// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewLandShapeTests.cs
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Q-17.perf2 (Аудит-2 на 24147ebd4, Land): <c>SELECT *</c> по в'ю БЕЗ предиката з прапором ON - T7b_00 3,5 с проти 1,6 с OFF,
/// T7_00 15 с проти 5,6 с, холодний 77-157 с; план: 53 Nested Loops, seek IX_CalculationResult_DocRun по одному на
/// екземпляр таблиці (676). Синтетика t3perf (один документ, один екземпляр) цього не показувала. Тут - форма Land:
/// багато документів x 12 місяців = сотні екземплярів ОДНІЄЇ таблиці, розкладка MonthsInColumns, ~200 колонок (96 місячних),
/// розріджені комірки (по 2 на місяць-власник), 20 колонок Calculated з прив'язками, результати актуальних прогонів і історія
/// НЕактуальних. Межі - у читаннях і формі плану (детерміновані), не в часі.
/// </summary>
[Collection("SqlServer")]
public sealed class ReportViewLandShapeTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private static readonly DateTime Now = new(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);
    private const int Docs = 8;
    private const int RowsPerInstance = 6;
    private const int CalcColumns = 20;

    private sealed record Shape(TestDocument Doc, string View, int Instances, int Rows);

    private static readonly Regex Reads = new(@"logical reads (\d+)", RegexOptions.Compiled);
    private static readonly Regex Cpu = new(@"execution times:\s+CPU time = (\d+) ms", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Compile = new(@"parse and compile time:\s+CPU time = (\d+) ms", RegexOptions.Compiled);

    private async Task<Shape> BuildAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(202601, columnCount: 100, rowCount: RowsPerInstance,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);

        await using (var db = sql.CreateContext())
        {
            var ordinal = 9000;
            for (var g = 2; g <= 8; g++)
            {
                for (var m = 1; m <= 12; m++)
                {
                    var column = new ColumnDef(doc.TableDefId, EcrCode.Create($"G{g}M{m}" + Guid.NewGuid().ToString("N")[..6]),
                        new LocalizedText(new Dictionary<string, string> { ["en"] = $"G{g}M{m}" }), ordinal++, CellDataType.Decimal);
                    column.SetMonth(true, (byte)m);
                    db.ColumnDefs.Add(column);
                }
            }

            for (var i = 0; i < CalcColumns; i++)
            {
                db.ColumnDefs.Add(new ColumnDef(doc.TableDefId, EcrCode.Create("CLP" + i + Guid.NewGuid().ToString("N")[..6]),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = "calc" }), 7000 + i, CellDataType.Calculated));
            }

            await db.SaveChangesAsync();
            var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
            version.Publish(publishedByUserId: 1, Now);
            await db.SaveChangesAsync();
        }

        for (var m = 2; m <= 12; m++)
        {
            await builder.AddPeriodAsync(doc, new PeriodKey(202600 + m));
        }

        // Документи x 12 місяців = сотні екземплярів однієї таблиці, рядки з тими самими RowKey.
        List<(string Key, int Ordinal)> rowKeys;
        await using (var db = sql.CreateContext())
        {
            var loaded = await db.TableRows.AsNoTracking()
                .Where(r => r.PeriodKeyValue == 202601 && r.TableInstanceId == doc.TableInstanceId)
                .OrderBy(r => r.Ordinal).Select(r => new { r.RowKeyValue, r.Ordinal }).ToListAsync();
            rowKeys = [.. loaded.Select(x => (x.RowKeyValue, x.Ordinal))];
        }

        var docIds = new List<long> { doc.DocumentId };
        await using (var db = sql.CreateContext())
        {
            var tag = Guid.NewGuid().ToString("N")[..8];
            var extra = Enumerable.Range(1, Docs - 1).Select(i => new Document(doc.ProjectId, $"LS{tag}{i}", 1, Now)).ToList();
            db.Documents.AddRange(extra);
            await db.SaveChangesAsync();
            docIds.AddRange(extra.Select(d => d.Id));
        }

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceCount = Docs * 12 - 1;
        var firstInstanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", instanceCount, CancellationToken.None);
        var firstRowId = await loader.ReserveIdsAsync("doc.TableRowSeq", instanceCount * rowKeys.Count, CancellationToken.None);
        await using (var db = sql.CreateContext())
        {
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            long iid = firstInstanceId;
            long rid = firstRowId;
            for (var d = 0; d < Docs; d++)
            {
                for (var m = 1; m <= 12; m++)
                {
                    if (d == 0 && m == 1)
                    {
                        continue;
                    }

                    var key = new PeriodKey(202600 + m);
                    db.TableInstances.Add(new TableInstance(key, iid, docIds[d], doc.TableDefId, Now));
                    foreach (var (rk, ord) in rowKeys)
                    {
                        db.TableRows.Add(new TableRow(key, rid++, iid, RowKey.Create(rk), ord, Now));
                    }

                    iid++;
                }
            }

            await db.SaveChangesAsync();
        }

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 600;
        // Розріджено: 2 комірки на рядок у колонках СВОГО місяця (власник = екземпляр місяця); 3 введені не-місячні - лише в якорі.
        command.CommandText = """
            ;WITH mc AS (
                SELECT c.Id, c.MonthNumber, ROW_NUMBER() OVER (PARTITION BY c.MonthNumber ORDER BY c.Id) AS rn
                  FROM cfg.ColumnDef AS c WHERE c.TableDefId = @t AND c.IsMonthColumn = 1)
            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric)
            SELECT r.PeriodKey, r.Id, mc.Id, @t, CAST(r.Ordinal % 97 AS decimal(18, 4)) + mc.rn + mc.MonthNumber / 100.0
              FROM doc.TableInstance AS ti
              JOIN doc.TableRow AS r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id
              JOIN mc ON r.PeriodKey = 202600 + mc.MonthNumber AND mc.rn <= 2
             WHERE ti.TableDefId = @t;

            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric)
            SELECT r.PeriodKey, r.Id, c.Id, @t, CAST(r.Ordinal % 89 AS decimal(18, 4)) + c.Ordinal
              FROM doc.TableInstance AS ti
              JOIN doc.TableRow AS r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id
              JOIN cfg.ColumnDef AS c ON c.TableDefId = @t AND c.IsMonthColumn = 0 AND c.DataType = 2 AND c.Ordinal BETWEEN 2 AND 4
             WHERE ti.TableDefId = @t AND r.PeriodKey = 202601;
            """;
        command.Parameters.AddWithValue("@t", doc.TableDefId);
        await command.ExecuteNonQueryAsync();

        await CheckpointAsync();
        await SeedResultsAsync(doc, docIds);
        await CheckpointAsync();

        var view = await ViewNameAsync(doc);
        return new Shape(doc, view, Docs * 12, Docs * 12 * rowKeys.Count);
    }

    /// <summary>Прибирає дані синтетики (спільна тестова база має стелю 192 МБ): звільнені сторінки використають інші тести.</summary>
    private async Task CleanupAsync(Shape s)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 600;
        command.CommandText = """
            DELETE FROM calc.CalculationResult WHERE DocumentId IN (SELECT Id FROM doc.Document WHERE ProjectId = @p);
            DELETE FROM calc.CalculationRun WHERE ProjectId = @p;
            DELETE FROM doc.CellValue WHERE TableDefId = @t;
            DELETE FROM doc.TableRow WHERE TableInstanceId IN (SELECT Id FROM doc.TableInstance WHERE TableDefId = @t);
            DELETE FROM doc.TableInstance WHERE TableDefId = @t;
            CHECKPOINT;
            """;
        command.Parameters.AddWithValue("@p", s.Doc.ProjectId);
        command.Parameters.AddWithValue("@t", s.Doc.TableDefId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task CheckpointAsync()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CHECKPOINT";
        await command.ExecuteNonQueryAsync();
    }

    private async Task SeedResultsAsync(TestDocument doc, List<long> docIds)
    {
        int methodologyId;
        int versionId;
        int unitId;
        await using (var db = sql.CreateContext())
        {
            var methodology = new Methodology(EcrCode.Create("LSH" + Guid.NewGuid().ToString("N")[..8]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "landshape" }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();
            var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
            db.MethodologyVersions.Add(version);
            await db.SaveChangesAsync();
            methodologyId = methodology.Id;
            versionId = version.Id;
            var calc = await db.ColumnDefs.AsNoTracking()
                .Where(c => c.TableDefId == doc.TableDefId && c.DataType == CellDataType.Calculated)
                .OrderBy(c => c.Id).ToListAsync();
            for (var i = 0; i < calc.Count; i++)
            {
                db.CalculationBindings.Add(new CalculationBinding(doc.TableDefId, calc[i].Id, methodologyId, "OUT" + i, "{}"));
            }

            await db.SaveChangesAsync();
            unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

            // Прогін на (документ, місяць): НЕактуальний (історія) і актуальний.
            var history = new List<CalculationRun>();
            var current = new List<CalculationRun>();
            foreach (var d in docIds)
            {
                for (var m = 1; m <= 12; m++)
                {
                    history.Add(new CalculationRun(doc.ProjectId, 202600 + m, triggeredByUserId: 1, Now, d));
                    current.Add(new CalculationRun(doc.ProjectId, 202600 + m, triggeredByUserId: 1, Now, d));
                }
            }

            db.CalculationRuns.AddRange(history);
            await db.SaveChangesAsync();
            foreach (var h in history)
            {
                h.Complete("Completed", Now, profileJson: null, errorMessage: null);
            }

            await db.SaveChangesAsync();
            db.CalculationRuns.AddRange(current);
            await db.SaveChangesAsync();
            foreach (var c in current)
            {
                c.Complete("Completed", Now, profileJson: null, errorMessage: null);
                c.MakeCurrent();
            }

            await db.SaveChangesAsync();
        }

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var fill = connection.CreateCommand();
        fill.CommandTimeout = 900;
        // Результати: рядок x вихід; актуальний прогін і такий самий за обсягом НЕактуальний (Land: 88 % прочитаного - НЕактуальні прогони).
        fill.CommandText = """
            INSERT INTO calc.CalculationResult
                (PeriodKey, Id, CalculationRunId, MethodologyVersionId, DocumentId, SourceRowKey, OutputCode, Value, UnitId, Kind)
            SELECT x.PeriodKey, NEXT VALUE FOR calc.CalculationResultSeq, x.RunId, @ver, x.DocumentId, x.RowKey, x.OutputCode,
                   1.5 + k.n, @unit, 0
              FROM (SELECT ti.PeriodKey, ti.DocumentId, r.RowKey, b.OutputCode,
                           ru.Id AS RunId, ru.Status AS RunStatus
                      FROM doc.TableInstance AS ti
                      JOIN doc.TableRow AS r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id
                      CROSS JOIN cfg.CalculationBinding AS b
                      JOIN calc.CalculationRun AS ru ON ru.ProjectId = @p AND ru.DocumentId = ti.DocumentId AND ru.PeriodKey = ti.PeriodKey
                     WHERE ti.TableDefId = @t AND b.TableDefId = @t AND b.MethodologyId = @m) AS x
              JOIN (VALUES (1, N'Current'), (1, N'Completed')) AS k(n, st)
                ON k.st = x.RunStatus;
            """;
        fill.Parameters.AddWithValue("@ver", versionId);
        fill.Parameters.AddWithValue("@unit", unitId);
        fill.Parameters.AddWithValue("@p", doc.ProjectId);
        fill.Parameters.AddWithValue("@t", doc.TableDefId);
        fill.Parameters.AddWithValue("@m", methodologyId);
        await fill.ExecuteNonQueryAsync();
    }

    private async Task<string> ViewNameAsync(TestDocument doc)
    {
        await using var db = sql.CreateContext();
        var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
        var template = await db.Templates.SingleAsync(t => t.Id == version.TemplateId);
        var table = await db.TableDefs.SingleAsync(t => t.Id == doc.TableDefId);
        return Regex.Replace($"v_{template.Code}_{doc.SheetCode}_{table.Code}_v{version.Version}", "[^A-Za-z0-9_]", "_");
    }

    private async Task GenerateAsync(Shape s, bool routing)
    {
        await using var db = sql.CreateContext();
        var router = routing
            ? new MonthColumnRouter(
                db,
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = "true" })
                    .Build())
            : null;
        await new ReportViewGenerator(db, monthRouter: router).GenerateAsync(s.Doc.TemplateVersionId, CancellationToken.None);
    }

    private sealed record Run(long Reads, long CpuMs, long CompileCpuMs, int Rows);

    private async Task<Run> MeasureAsync(string query)
    {
        long reads = 0, cpu = 0, compile = 0;
        await using var connection = new SqlConnection(sql.ConnectionString);
        connection.InfoMessage += (_, e) =>
        {
            foreach (Match m in Reads.Matches(e.Message)) { reads += long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); }
            foreach (Match m in Cpu.Matches(e.Message)) { cpu += long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); }
            foreach (Match m in Compile.Matches(e.Message)) { compile += long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); }
        };
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 900;
        command.CommandText = $"""
            SET STATISTICS IO ON; SET STATISTICS TIME ON;
            IF OBJECT_ID('tempdb..#x') IS NOT NULL DROP TABLE #x;
            {query.Replace("SELECT *", "SELECT * INTO #x")};
            SET STATISTICS IO OFF; SET STATISTICS TIME OFF;
            SELECT COUNT(*) FROM #x;
            """;
        var count = (int)(await command.ExecuteScalarAsync())!;
        return new Run(reads, cpu, compile, count);
    }

    private async Task<System.Xml.Linq.XDocument> PlanAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 900;
        command.CommandText = "SET STATISTICS XML ON; " + query.Replace("SELECT *", "SELECT * INTO #y");
        await using var reader = await command.ExecuteReaderAsync();
        var last = "";
        do
        {
            while (await reader.ReadAsync())
            {
                if (reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase))
                {
                    last = reader.GetString(0);
                }
            }
        }
        while (await reader.NextResultAsync());
        return System.Xml.Linq.XDocument.Parse(last);
    }

    private sealed record PlanFacts(
        int NestedLoops, int HashMatches, long DocRunSeekExecutions, long InstanceSeekExecutions, long TableRowSeekExecutions,
        int CalculationResultPartitions);

    private static PlanFacts Facts(System.Xml.Linq.XDocument plan)
    {
        System.Xml.Linq.XNamespace sp = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        var ops = plan.Descendants(sp + "RelOp").ToList();
        long Exec(System.Xml.Linq.XElement o) => o.Descendants(sp + "RunTimeCountersPerThread").Sum(c => (long)c.Attribute("ActualExecutions")!);
        long Seeks(string table, string? index = null) => ops
            .Where(o => ((string?)o.Attribute("PhysicalOp") ?? "").Contains("Seek", StringComparison.Ordinal)
                        && o.Elements(sp + "IndexScan").Elements(sp + "Object")
                            .Any(x => (string?)x.Attribute("Table") == table && (index == null || (string?)x.Attribute("Index") == index)))
            .Sum(Exec);
        return new PlanFacts(
            ops.Count(o => (string?)o.Attribute("PhysicalOp") == "Nested Loops"),
            ops.Count(o => (string?)o.Attribute("PhysicalOp") == "Hash Match"),
            Seeks("[CalculationResult]", "[IX_CalculationResult_DocRun]"),
            Seeks("[TableInstance]"),
            Seeks("[TableRow]"),
            ops.Where(o => ((string?)o.Attribute("PhysicalOp") ?? "").Contains("Seek", StringComparison.Ordinal)
                           && o.Elements(sp + "IndexScan").Elements(sp + "Object").Any(x => (string?)x.Attribute("Table") == "[CalculationResult]"))
                .SelectMany(o => o.Descendants(sp + "PartitionsAccessed"))
                .Select(p => (int?)p.Attribute("PartitionCount") ?? 0).DefaultIfEmpty(0).Max());
    }

    private sealed record DocRunSeek(int Operators, long RowsRead, long RowsOut, long Executions, List<string> KeyColumns);

    /// <summary>Факти seek-ів IX_CalculationResult_DocRun: ключові колонки префікса пошуку, прочитані рядки, виконання.</summary>
    private static DocRunSeek DocRunSeekFacts(System.Xml.Linq.XDocument plan)
    {
        System.Xml.Linq.XNamespace sp = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        var seeks = plan.Descendants(sp + "RelOp")
            .Where(o => ((string?)o.Attribute("PhysicalOp") ?? "").Contains("Seek", StringComparison.Ordinal)
                        && o.Elements(sp + "IndexScan").Elements(sp + "Object").Any(x =>
                            (string?)x.Attribute("Table") == "[CalculationResult]"
                            && (string?)x.Attribute("Index") == "[IX_CalculationResult_DocRun]"))
            .ToList();
        long Sum(string attribute) => seeks.Sum(o => o.Descendants(sp + "RunTimeCountersPerThread")
            .Sum(c => (long?)c.Attribute(attribute) ?? 0));
        var keys = seeks
            .SelectMany(o => o.Descendants(sp + "SeekPredicateNew").Descendants(sp + "Prefix").Descendants(sp + "RangeColumns")
                .Descendants(sp + "ColumnReference"))
            .Select(c => ((string?)c.Attribute("Column") ?? "").Trim('[', ']'))
            .Distinct().ToList();
        return new DocRunSeek(seeks.Count, Sum("ActualRowsRead"), Sum("ActualRows"), Sum("ActualExecutions"), keys);
    }

    /// <summary>
    /// Q-17.rvseek (Аудит-2, Land 4,47 млн рядків): читання результатів вʼюхою БЕЗ предиката мусить іти за АКТУАЛЬНИМИ прогонами: seek
    /// IX_CalculationResult_DocRun по ВСІХ трьох ключах (PeriodKey, DocumentId, CalculationRunId) і прочитані рядки ≈ рядкам актуальних
    /// прогонів, а не всій таблиці (синтетика: історія НЕактуальних прогонів = стільки ж, скільки актуальних; до виправлення seek ішов по
    /// (PeriodKey, DocumentId), прогін відсікався НАД seek-ом і читалась уся історія документа за період). З предикатом
    /// _PeriodKey - одна партиція. Детерміновано (план і лічильники, не час): за обох значень прапора.
    /// Мутація: прибрати <c>INNER LOOP JOIN</c> або <c>AND ru.PeriodKey = ti.PeriodKey</c> у наборовій гілці rv - тест червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Вʼюха_без_предиката_читає_результати_актуальних_прогонів_seek_по_трьох_ключах_а_не_всю_історію()
    {
        var s = await BuildAsync();
        try
        {
            foreach (var routing in new[] { false, true })
            {
                await AssertSeekShapeAsync(s, routing);
            }
        }
        finally
        {
            await CleanupAsync(s);
        }
    }

    private async Task AssertSeekShapeAsync(Shape s, bool routing)
    {
        {
            var star = $"SELECT * FROM rpt.[{s.View}]";
            var one = $"SELECT * FROM rpt.[{s.View}] WHERE _DocumentId = {s.Doc.DocumentId} AND _PeriodKey = 202603";
            await GenerateAsync(s, routing);

            long currentRows;
            long allRows;
            await using (var connection = new SqlConnection(sql.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT (SELECT COUNT_BIG(*) FROM calc.CalculationResult AS cr JOIN calc.CalculationRun AS ru ON ru.Id = cr.CalculationRunId
                             WHERE ru.ProjectId = @p AND ru.Status = N'Current'),
                           (SELECT COUNT_BIG(*) FROM calc.CalculationResult AS cr JOIN calc.CalculationRun AS ru ON ru.Id = cr.CalculationRunId
                             WHERE ru.ProjectId = @p)
                    """;
                command.Parameters.AddWithValue("@p", s.Doc.ProjectId);
                await using var reader = await command.ExecuteReaderAsync();
                await reader.ReadAsync();
                currentRows = reader.GetInt64(0);
                allRows = reader.GetInt64(1);
            }

            await PlanAsync(star);   // прогрів кешу плану/сторінок: далі міряємо повторний, "робочий" прогін
            var starSeek = DocRunSeekFacts(await PlanAsync(star));
            var oneFacts = Facts(await PlanAsync(one));
            var report = $"routing={routing} current={currentRows} all={allRows}: star seeks={starSeek.Operators} read={starSeek.RowsRead} "
                         + $"out={starSeek.RowsOut} exec={starSeek.Executions} keys=[{string.Join(',', starSeek.KeyColumns)}] | one partitions={oneFacts.CalculationResultPartitions}";
            output.WriteLine(report);

            Assert.True(allRows >= 1.8 * currentRows, "синтетика мусить містити історію НЕактуальних прогонів, співмірну з актуальними: " + report);
            Assert.True(starSeek.Operators >= 1, "у плані немає seek-а IX_CalculationResult_DocRun: " + report);
            Assert.Contains("PeriodKey", starSeek.KeyColumns);
            Assert.Contains("DocumentId", starSeek.KeyColumns);
            Assert.Contains("CalculationRunId", starSeek.KeyColumns);
            Assert.True(starSeek.RowsRead <= 1.3 * currentRows, report);
            Assert.True(oneFacts.CalculationResultPartitions <= 1, report);
        }
    }

    /// <summary>
    /// SELECT * БЕЗ предиката і з предикатом (документ + період) по в'ю з прапором ON на формі Land. Виміри (24 документи x 12 місяців =
    /// 288 екземплярів, 1 728 рядків): ON без предиката - 34,9 тис. читань (20 на рядок), 11 Nested Loops, seek TableRow по одному на
    /// екземпляр; ДО Q-17.perf2 (ланцюжок власників на рядок; на 480 екземплярах) - 296 тис. читань (103 на рядок), 51 Nested Loops,
    /// 74 880 seek-ів TableRow (26 на рядок) і 2 880 seek-ів екземплярів (по одному на рядок). Межі: читання на рядок, число seek-ів
    /// TableRow (26 на рядок до - не більш як 5 на рядок після), кількість Nested Loops, і відношення до вʼюхи без прапора.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Вʼюха_ON_без_предиката_на_формі_Land_читає_наборово_а_не_ланцюжком_власників_на_рядок()
    {
        var s = await BuildAsync();
        try
        {
            var star = $"SELECT * FROM rpt.[{s.View}]";
            var one = $"SELECT * FROM rpt.[{s.View}] WHERE _DocumentId = {s.Doc.DocumentId} AND _PeriodKey = 202603";

            await GenerateAsync(s, routing: false);
            await MeasureAsync(star);
            var off = await MeasureAsync(star);
            var offOne = await MeasureAsync(one);

            await GenerateAsync(s, routing: true);
            await MeasureAsync(star);
            var on = await MeasureAsync(star);
            var onFacts = Facts(await PlanAsync(star));
            var onOne = await MeasureAsync(one);
            var onOneFacts = Facts(await PlanAsync(one));

            var report = $"instances={s.Instances} rows={s.Rows}: ON {on} {onFacts} | OFF {off} | ONE ON {onOne} {onOneFacts} OFF {offOne}";
            output.WriteLine(report);

            Assert.Equal(s.Rows, on.Rows);
            Assert.Equal(RowsPerInstance, onOne.Rows);
            // Читання на рядок не ростуть з числом екземплярів/місяців: ланцюжок власників на рядок давав ~103.
            Assert.True(on.Reads <= 40L * s.Rows, report);
            Assert.True(on.Reads <= 2 * off.Reads, report);
            // Власники читаються НАБОРОМ: seek-ів TableRow не більш як 5 на рядок (було 26 на рядок; у наборі класу план інколи обирає
        // seek-и замість скану - ~1,3 на рядок), екземплярів - не по одному на рядок.
            Assert.True(onFacts.TableRowSeekExecutions <= 5L * s.Rows, report);
            Assert.True(onFacts.InstanceSeekExecutions <= s.Instances, report);
            Assert.True(onFacts.NestedLoops <= 25, report);
            // rv - seek IX_CalculationResult_DocRun на (екземпляр x актуальний прогін документа; їх до 12), а не на рядок/результат.
            Assert.True(onFacts.DocRunSeekExecutions <= 13L * s.Instances, report);
            // Шлях з предикатом не гірший за вʼюху без прапора і малий у абсолюті: предикат документа проходить у похідну власників.
            Assert.True(onOne.Reads <= Math.Max(2 * offOne.Reads, 4_000), report);
            Assert.True(onOneFacts.TableRowSeekExecutions <= 400, report);
            // Відсічка партицій (calc.CalculationResult - по місяцю): seek результатів з предикатом _PeriodKey торкається ОДНІЄЇ партиції
            // (Land: 3-12 із 25), бо cr.PeriodKey = ti.PeriodKey, а _PeriodKey береться з ti.
            Assert.True(onOneFacts.CalculationResultPartitions <= 1, report);
        }
        finally
        {
            await CleanupAsync(s);
        }
    }
}