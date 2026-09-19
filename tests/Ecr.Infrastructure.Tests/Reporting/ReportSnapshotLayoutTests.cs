// tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotLayoutTests.cs
using System.Globalization;
using Ecr.Application.Reporting;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// D-52a: опис версії КЕРУЄ побудовою зрізу — на реальній базі й зі справжніми
/// результатами розрахунку (порожній зріз не довів би нічого).
/// </summary>
[Collection("SqlServer")]
public sealed class ReportSnapshotLayoutTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.4")]
    public async Task Опис_із_трьох_колонок_в_іншому_порядку_дає_в_рядках_рівно_їх()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = chain.CreateContext();
        var seeded = await SeedResultsAsync(chain, db);

        var version = await PublishedAsync(
            db,
            """[{"code":"Value","kind":"number"},{"code":"UnitCode","kind":"text"},{"code":"DocumentId","kind":"number"}]""");

        var builder = new ReportSnapshotBuilder(db, new TestClock(Now));
        var snapshotId = await builder.BuildAsync(
            version.Id, seeded.ProjectId, seeded.PeriodKey, null, CancellationToken.None);

        var cells = await db.ReportRows.AsNoTracking()
            .Where(r => r.SnapshotId == snapshotId)
            .ToListAsync();

        Assert.Equal(2 * 3, cells.Count);
        Assert.Equal(["DocumentId", "UnitCode", "Value"], cells.Select(c => c.ColumnCode).Distinct().Order());
        Assert.All(cells.Where(c => c.ColumnCode == "UnitCode"), c => Assert.Equal(seeded.UnitCode, c.ValueString));

        // Порядок опису — це й порядок суми: перевірка мусить зійтися.
        var hashes = await builder.VerifyAsync(snapshotId, CancellationToken.None);
        Assert.Equal(hashes!.Stored, hashes.Actual);

        var page = await builder.RowsAsync(snapshotId, 0, 10, CancellationToken.None);
        Assert.Equal(["Value", "UnitCode", "DocumentId"], page!.Columns.Select(c => c.Code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Зріз_за_описом_IEC_із_сіду_має_той_самий_вміст_і_суму_що_й_до_D52a()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = chain.CreateContext();
        var seeded = await SeedResultsAsync(chain, db);

        var iec = await (
            from def in db.ReportDefs
            join version in db.ReportVersions on def.Id equals version.ReportDefId
            where def.Code == "IEC"
            select version.Id).SingleAsync();

        var snapshotId = await new ReportSnapshotBuilder(db, new TestClock(Now)).BuildAsync(
            iec, seeded.ProjectId, seeded.PeriodKey, null, CancellationToken.None);

        var stored = await db.ReportSnapshots.AsNoTracking()
            .Where(s => s.Id == snapshotId).Select(s => s.ContentHash).SingleAsync();

        // ⛔ Очікуване складено ДОСЛІВНОЮ копією старого `AggregateAsync`
        // (п'ять `Cell(...)` у фіксованому порядку), а не новим кодом.
        var expected = new List<ReportRow>();
        var rowNo = 0;

        foreach (var (rowKey, output, value) in seeded.Results)
        {
            rowNo++;
            expected.Add(Cell(snapshotId, rowNo, "DocumentId", null, seeded.DocumentId));
            expected.Add(Cell(snapshotId, rowNo, "RowKey", rowKey, null));
            expected.Add(Cell(snapshotId, rowNo, "OutputCode", output, null));
            expected.Add(Cell(snapshotId, rowNo, "Value", null, value));
            expected.Add(Cell(snapshotId, rowNo, "SubstanceEntryId", null, null));
        }

        Assert.Equal(Convert.ToHexString(OldHash(expected)), Convert.ToHexString(stored!));

        var actual = await db.ReportRows.AsNoTracking()
            .Where(r => r.SnapshotId == snapshotId)
            .Select(r => new { r.RowNo, r.ColumnCode, r.ValueString, r.ValueNumeric })
            .ToListAsync();

        Assert.Equal(
            expected.Select(Key).Order(StringComparer.Ordinal),
            actual.Select(r => $"{r.RowNo}|{r.ColumnCode}|{r.ValueString}|{Canonical(r.ValueNumeric)}")
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Рядки_віддаються_сторінками_за_курсором_RowNo()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = chain.CreateContext();
        var seeded = await SeedResultsAsync(chain, db);

        var version = await PublishedAsync(
            db, """[{"code":"OutputCode","kind":"text"},{"code":"Value","kind":"number"}]""");

        var builder = new ReportSnapshotBuilder(db, new TestClock(Now));
        var snapshotId = await builder.BuildAsync(
            version.Id, seeded.ProjectId, seeded.PeriodKey, null, CancellationToken.None);

        var first = await builder.RowsAsync(snapshotId, 0, 1, CancellationToken.None);
        var row = Assert.Single(first!.Rows);
        Assert.Equal(1, row.RowNo);
        Assert.Equal("E_CO2", row.Cells["OutputCode"]);

        // Число без хвостових нулів масштабу `decimal(28,10)`.
        Assert.Equal("12.5", ((decimal)row.Cells["Value"]!).ToString(CultureInfo.InvariantCulture));
        Assert.Equal(1, first.NextCursor);

        var second = await builder.RowsAsync(snapshotId, first.NextCursor!.Value, 1, CancellationToken.None);
        Assert.Equal(2, Assert.Single(second!.Rows).RowNo);
        Assert.Null(second.NextCursor);

        Assert.Null(await builder.RowsAsync(long.MaxValue, 0, 1, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Кожне_поле_таблиці_джерела_будівник_уміє_прочитати()
        => Assert.Equal(
            ReportSourceColumns.CodesOf(ReportDefinitionSpec.CalculationResults).Order(StringComparer.Ordinal),
            ReportSnapshotBuilder.ReadableColumns.Order(StringComparer.Ordinal));

    private sealed record Seeded(
        int ProjectId, PeriodKey PeriodKey, long DocumentId, string UnitCode,
        IReadOnlyList<(string RowKey, string Output, decimal Value)> Results);

    /// <summary>Чинний прогін із двома результатами для документа ланцюга.</summary>
    private static async Task<Seeded> SeedResultsAsync(TestDocumentBuilder chain, EcrDbContext db)
    {
        var document = await chain.BuildAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];

        var methodology = new Methodology(
            EcrCode.Create($"RPTM_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var methodologyVersion = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.Add(methodologyVersion);

        var run = new CalculationRun(document.ProjectId, document.PeriodKey.Value, triggeredByUserId: null, Now);
        run.Complete("Succeeded", Now, "{}", errorMessage: null);
        run.MakeCurrent();
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();

        var unit = await db.Units.AsNoTracking().OrderBy(u => u.Id).FirstAsync();

        (string RowKey, string Output, decimal Value)[] results = [("row-1", "E_CO2", 12.5m), ("row-2", "E_NOX", 5m)];

        foreach (var (rowKey, output, value) in results)
        {
            // ⚠ Сирим SQL: `Id` береться з послідовності ДО вставки (як у
            // `CalculationResultStore`), EF його сам не видає. Число — рядком:
            // параметр `decimal` EF оголошує як `decimal(18,2)`.
            var text = value.ToString(CultureInfo.InvariantCulture);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO calc.CalculationResult
                    (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
                VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {methodologyVersion.Id},
                        {document.PeriodKey.Value}, {document.DocumentId}, {rowKey}, {output},
                        CAST({text} AS decimal(28,10)), {unit.Id})
                """);
        }

        return new Seeded(document.ProjectId, document.PeriodKey, document.DocumentId, unit.Code, results);
    }

    private static async Task<ReportVersion> PublishedAsync(EcrDbContext db, string columnsJson)
    {
        var def = new ReportDef(
            EcrCode.Create($"RPT{Guid.NewGuid().ToString("N")[..8]}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Layout test" }),
            isRegulatory: true);
        db.ReportDefs.Add(def);
        await db.SaveChangesAsync();

        var version = new ReportVersion(def.Id, "1.0", columnsJson, """{"rowSource":"CalculationResults"}""", Now);
        version.Publish();
        db.ReportVersions.Add(version);
        await db.SaveChangesAsync();

        return version;
    }

    /// <summary>Сума так, як її рахував будівник до D-52a (копія <c>HashOf</c> + <c>Canonical</c>).</summary>
    private static byte[] OldHash(IReadOnlyList<ReportRow> rows)
        => System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('\n', rows.Select(Key))));

    private static string Key(ReportRow r) => $"{r.RowNo}|{r.ColumnCode}|{r.ValueString}|{Canonical(r.ValueNumeric)}";

    private static string Canonical(decimal? value)
        => value is { } number ? number.ToString("0.##########", CultureInfo.InvariantCulture) : string.Empty;

    private static ReportRow Cell(long snapshotId, int rowNo, string column, string? text, decimal? number)
    {
        var row = new ReportRow(snapshotId, rowNo, column);
        row.SetValue(text, number, null);
        return row;
    }
}
