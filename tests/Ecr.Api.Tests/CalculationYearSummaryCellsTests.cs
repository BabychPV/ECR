using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Зведення року: комірки формул в агрегаті (маска місяців) — той самий набір (місяць, рядок, колонка), що й читання без
/// агрегату (пошук на кожен рядок, як сторінка й експорт), на документі, чиї рядки впереміш із рядками ЧУЖОГО документа
/// тієї ж таблиці й того самого періоду, з видаленим рядком, порожньою розрахованою і введеною коміркою. Далі — свіжість:
/// зведення бачить зміну комірки формули, новий і видалений рядок і новий прогін одразу (комірки формул не кешуються).
/// </summary>
/// <remarks>
/// Мутації (локально): прибрати <c>ti.DocumentId = @documentId</c> з агрегату комірок формул — червоніє (рядок чужого
/// документа з ключем R0 дає січневий біт); прибрати <c>row.IsDeleted = 0</c> — червоніє (видалений рядок R6).
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationYearSummaryCellsTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly int[] Year = [202601, 202602];
    private static readonly int[] JanuaryWithFormula = [0, 2, 4, 12, 14, 16, 18];
    private static readonly string?[] OldRunRows = ["R12"];
    private static readonly string?[] NewRunRows = ["R14"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "CALC-RESULTS")]
    public async Task Комірки_формул_зведення_дорівнюють_пошуку_на_рядок_і_свіжі_після_змін()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var a = await builder.BuildAsync(rowCount: 0).ConfigureAwait(true);
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var feb = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(true);

        // Чужий документ — у тому самому проєкті й ТІЙ САМІЙ таблиці шаблону (як сусідні документи живої бази): його
        // рядки з тими ж ключами злилися б у групи зведення A, якби діапазон острова не відсікався документом.
        var foreignInstance = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(true);
        long foreignDocument;
        await using (var db = Context())
        {
            var document = new Document(a.ProjectId, $"DOCX{feb}", 1, Now);
            db.Documents.Add(document);
            await db.SaveChangesAsync().ConfigureAwait(true);
            foreignDocument = document.Id;
        }

        await builder.AddPeriodAsync(a, PeriodKey.Create(2026, 2)).ConfigureAwait(true);

        // Id рядків: 0..19 — документ A і чужий упереміш (парні — A, непарні — чужий); 20..399 — лише чужі; 400..403 — A
        // (далекий блок); 404 — чужий. Лютий A — окремий блок.
        var first = await loader.ReserveIdsAsync("doc.TableRowSeq", 420, CancellationToken.None).ConfigureAwait(true);
        var formula = a.ColumnDefIds[2];
        var sqlText = new List<string>();
        sqlText.Add($"""
            INSERT doc.TableInstance (PeriodKey, Id, DocumentId, TableDefId, SheetCopyId, CreatedAt, ModifiedAt)
            VALUES (202602, {feb}, {a.DocumentId}, {a.TableDefId}, NULL, '2026-01-15', '2026-01-15'),
                   (202601, {foreignInstance}, {foreignDocument}, {a.TableDefId}, NULL, '2026-01-15', '2026-01-15');

            """);
        void Row(int period, long id, long instance, string key, int ordinal, bool deleted = false)
            => sqlText.Add($"""
                INSERT doc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, ModifiedAt, IsDeleted)
                VALUES ({period}, {id}, {instance}, N'{key}', NULL, {ordinal}, '2026-01-15', {(deleted ? 1 : 0)});

                """);
        void Cell(int period, long row, int column, int table, decimal? value, bool calculated = true)
            => sqlText.Add($"""
                INSERT doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
                VALUES ({period}, {row}, {column}, {table}, {(value is { } v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL")},
                        {(calculated ? 1 : 0)}, {(value is null ? 1 : 0)});

                """);

        for (var i = 0; i < 20; i++)
        {
            var id = first + i;
            if (i % 2 == 0)
            {
                // A: R0, R2 … R18; R6 видалений (комірка формули лишилась), R8 — порожня розрахована, R10 — введене число.
                Row(202601, id, a.TableInstanceId, $"R{i}", i, deleted: i == 6);
                Cell(202601, id, formula, a.TableDefId, i == 8 ? null : i + 0.5m, calculated: i != 10);
                Cell(202601, id, a.ColumnDefIds[1], a.TableDefId, 1m, calculated: false);
            }
            else
            {
                // Чужий «R0» (той самий ключ, що в A): після очищення формули R0 у січні A лишає R0 лише в лютому, і
                // січневий біт R0 зведення A міг би дати тільки рядок чужого документа.
                Row(202601, id, foreignInstance, i == 1 ? "R0" : $"R{i}", i);
                Cell(202601, id, formula, a.TableDefId, 7m);
            }
        }

        for (var i = 20; i < 400; i += 19)
        {
            Row(202601, first + i, foreignInstance, $"F{i}", i);
            Cell(202601, first + i, formula, a.TableDefId, 7m);
        }

        for (var i = 400; i < 404; i++)
        {
            Row(202601, first + i, a.TableInstanceId, $"L{i}", i);
            Cell(202601, first + i, formula, a.TableDefId, 3m);
        }

        Row(202601, first + 404, foreignInstance, "F404", 404);
        Cell(202601, first + 404, formula, a.TableDefId, 7m);

        // Лютий A: R0 і R2 з формулою, R4 — без.
        for (var i = 0; i < 3; i++)
        {
            Row(202602, first + 410 + i, feb, $"R{i * 2}", i);
            if (i < 2)
            {
                Cell(202602, first + 410 + i, formula, a.TableDefId, 2m);
            }
        }

        await using (var db = Context())
        {
            await db.Database.ExecuteSqlRawAsync(string.Concat(sqlText)).ConfigureAwait(true);
        }

        // R0, R2, R4, R12, R14, R16, R18 (січень) + L400..L403 + лютий R0, R2.
        var expected = JanuaryWithFormula.Select(i => (202601, $"R{i}"))
            .Concat(Enumerable.Range(400, 4).Select(i => (202601, $"L{i}")))
            .Concat([(202602, "R0"), (202602, "R2")])
            .ToHashSet();
        await AssertSummaryAsync(a, feb, formula, expected).ConfigureAwait(true);

        // Свіжість 1: комірка формули стала порожньою (R0, січень) і порожня отримала число (R8).
        await ExecAsync($"""
            UPDATE doc.CellValue SET ValueNumeric = NULL, IsEmpty = 1 WHERE PeriodKey = 202601 AND TableRowId = {first} AND ColumnDefId = {formula};
            UPDATE doc.CellValue SET ValueNumeric = 4, IsEmpty = 0 WHERE PeriodKey = 202601 AND TableRowId = {first + 8} AND ColumnDefId = {formula};
            """).ConfigureAwait(true);
        expected.Remove((202601, "R0"));
        expected.Add((202601, "R8"));
        await AssertSummaryAsync(a, feb, formula, expected).ConfigureAwait(true);

        // Свіжість 2: рядок видалено (R2, січень) і додано новий далеко від островів (R4 лютого — формула з'явилась у
        // новому рядку з ключем N1).
        var added = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None).ConfigureAwait(true);
        await ExecAsync($"""
            UPDATE doc.TableRow SET IsDeleted = 1 WHERE PeriodKey = 202601 AND Id = {first + 2};
            INSERT doc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, ModifiedAt)
            VALUES (202602, {added}, {feb}, N'N1', NULL, 9, '2026-01-15');
            INSERT doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
            VALUES (202602, {added}, {formula}, {a.TableDefId}, 5, 1, 0);
            """).ConfigureAwait(true);
        expected.Remove((202601, "R2"));
        expected.Add((202602, "N1"));
        await AssertSummaryAsync(a, feb, formula, expected).ConfigureAwait(true);

        // Свіжість 3: новий прогін. Проєктний прогін січня дає число рядку R12; новий актуальний (старий витіснено) — R14.
        await using (var db = Context())
        {
            var methodology = new Methodology(
                EcrCode.Create(("CYS" + Guid.NewGuid().ToString("N"))[..12].ToUpperInvariant()), new LocalizedText(new Dictionary<string, string> { ["en"] = "M" }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync().ConfigureAwait(true);
            var created = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
            db.MethodologyVersions.Add(created);
            await db.SaveChangesAsync().ConfigureAwait(true);
            var version = created.Id;

            var unit = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync().ConfigureAwait(true);
            var old = NewRun(a.ProjectId);
            db.CalculationRuns.Add(old);
            await db.SaveChangesAsync().ConfigureAwait(true);
            await InsertResultAsync(db, old.Id, version, a, "R12", unit).ConfigureAwait(true);
            Assert.Equal(OldRunRows, await ResultRowsAsync(a, feb).ConfigureAwait(true));

            old.Supersede();
            await db.SaveChangesAsync().ConfigureAwait(true);
            var next = NewRun(a.ProjectId);
            db.CalculationRuns.Add(next);
            await db.SaveChangesAsync().ConfigureAwait(true);
            await InsertResultAsync(db, next.Id, version, a, "R14", unit).ConfigureAwait(true);
            Assert.Equal(NewRunRows, await ResultRowsAsync(a, feb).ConfigureAwait(true));
        }
    }

    /// <summary>Зведення (агрегат) = оракул без агрегату = очікуване: (місяць, ключ рядка) комірок колонки формули.</summary>
    private async Task AssertSummaryAsync(TestDocument a, long feb, int formula, HashSet<(int, string)> expected)
    {
        var aggregate = await ReadAsync(a, feb, aggregate: true).ConfigureAwait(true);
        var raw = await ReadAsync(a, feb, aggregate: false).ConfigureAwait(true);
        var fromAggregate = aggregate.FormulaCellMonths
            .SelectMany(c => Enumerable.Range(1, 12).Where(m => (c.Months & (1 << (m - 1))) != 0).Select(m => (PeriodKey: 202600 + m, c.RowKey, c.ColumnDefId)))
            .ToList();
        var fromRaw = raw.FormulaCells.Select(c => (c.PeriodKey, c.RowKey, c.ColumnDefId)).ToList();

        Assert.Equal(fromRaw.Count, fromAggregate.Count);
        Assert.Equal(fromRaw.ToHashSet(), fromAggregate.ToHashSet());
        Assert.All(fromAggregate, c => Assert.Equal(formula, c.ColumnDefId));
        Assert.Equal(expected.OrderBy(x => x), fromAggregate.Select(c => (c.PeriodKey, c.RowKey)).OrderBy(x => x));
    }

    private async Task<CalculationYearFacts> ReadAsync(TestDocument a, long feb, bool aggregate)
    {
        await using var db = Context();
        var store = new CalculationResultStore(db, new TestClock(Now));
        return await store.ReadYearAsync(
            new CalculationYearReadRequest(a.DocumentId, Year, [a.TableInstanceId, feb], [], IncludeRows: false, SliceResults: false, Aggregate: aggregate),
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<List<string?>> ResultRowsAsync(TestDocument a, long feb)
    {
        // Зведення: числа з екземпляром — маски (ResultMonths), без екземпляра — Results.
        var facts = await ReadAsync(a, feb, aggregate: true).ConfigureAwait(false);
        return [.. facts.ResultMonths.Select(r => r.SourceRowKey).Concat(facts.Results.Select(r => r.SourceRowKey))];
    }

    private static CalculationRun NewRun(int projectId)
    {
        var run = new CalculationRun(projectId, 202601, triggeredByUserId: null, Now);
        run.Complete("Succeeded", Now.AddMinutes(1), "{}", errorMessage: null);
        run.MakeCurrent();
        return run;
    }

    private static Task<int> InsertResultAsync(EcrDbContext db, long runId, int version, TestDocument a, string rowKey, int unit)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO calc.CalculationResult
                (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId, TableInstanceId)
            VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {runId}, {version}, 202601, {a.DocumentId},
                    {rowKey}, N'E_CYS', CAST(1 AS decimal(34,16)), {unit}, {a.TableInstanceId})
            """);

    private async Task ExecAsync(string text)
    {
        await using var db = Context();
        await db.Database.ExecuteSqlRawAsync(text).ConfigureAwait(false);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
