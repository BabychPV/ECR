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

    /// <summary>Мова запиту у видачі рядків (<c>R9</c>): нею підписуються колонки.</summary>
    private const string Language = "en";

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

        var page = await builder.RowsAsync(snapshotId, 0, 10, Language, CancellationToken.None);
        Assert.Equal(["Value", "UnitCode", "DocumentId"], page!.Columns.Select(c => c.Code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Побудова_одразу_записує_формат_суми_current()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = chain.CreateContext();
        var seeded = await SeedResultsAsync(chain, db);
        var version = await PublishedAsync(db, RuledColumnsJson);

        var snapshotId = await new ReportSnapshotBuilder(db, new TestClock(Now)).BuildAsync(
            version.Id, seeded.ProjectId, seeded.PeriodKey, null, CancellationToken.None);

        await using var fresh = chain.CreateContext();
        Assert.Equal("current", await fresh.ReportSnapshots.AsNoTracking()
            .Where(s => s.Id == snapshotId).Select(s => s.HashFormat).SingleAsync());
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

        var first = await builder.RowsAsync(snapshotId, 0, 1, Language, CancellationToken.None);
        var row = Assert.Single(first!.Rows);
        Assert.Equal(1, row.RowNo);
        Assert.Equal("E_CO2", row.Cells["OutputCode"]);

        // Число без хвостових нулів масштабу `decimal(34,16)`.
        Assert.Equal("12.5", ((decimal)row.Cells["Value"]!).ToString(CultureInfo.InvariantCulture));
        Assert.Equal(1, first.NextCursor);

        var second = await builder.RowsAsync(snapshotId, first.NextCursor!.Value, 1, Language, CancellationToken.None);
        Assert.Equal(2, Assert.Single(second!.Rows).RowNo);
        Assert.Null(second.NextCursor);

        Assert.Null(await builder.RowsAsync(long.MaxValue, 0, 1, Language, CancellationToken.None));
    }

    private static readonly ReportColumnCommand[] RuledColumns = [new("OutputCode", "text"), new("Value", "number")];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правило_set_змінює_значення_в_рядках_і_суму_а_перевірка_сходиться()
    {
        // 12.5 → 13 (округлення від нуля); другий рядок (5) умова не зачіпає.
        var (plain, ruled, builder, db) = await BuildPairAsync(
            new("[Value] > 10", new(Set: new("Value", "ROUND([Value], 0)"))));

        await using var scope = db;
        var cells = await CellsAsync(db, ruled);

        Assert.Equal(
            ["1|OutputCode|E_CO2|", "1|Value||13", "2|OutputCode|E_NOX|", "2|Value||5"], cells.Select(Key));
        Assert.Equal(Convert.ToHexString(OldHash(cells)), await HashAsync(db, ruled));
        Assert.NotEqual(await HashAsync(db, plain), await HashAsync(db, ruled));

        var hashes = await builder.VerifyAsync(ruled, CancellationToken.None);
        Assert.Equal(hashes!.Stored, hashes.Actual);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правило_hideRow_прибирає_рядок_зі_зрізу_й_із_суми_а_перевірка_сходиться()
    {
        var (plain, ruled, builder, db) = await BuildPairAsync(new("[OutputCode] = 'E_CO2'", new(HideRow: true)));

        await using var scope = db;
        var cells = await CellsAsync(db, ruled);

        // Прихований рядок номера не займає: лишився один, і він — перший.
        Assert.Equal(["1|OutputCode|E_NOX|", "1|Value||5"], cells.Select(Key));
        Assert.Equal(4, (await CellsAsync(db, plain)).Count);
        Assert.NotEqual(await HashAsync(db, plain), await HashAsync(db, ruled));

        var hashes = await builder.VerifyAsync(ruled, CancellationToken.None);
        Assert.Equal(hashes!.Stored, hashes.Actual);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Помилка_правила_на_рядку_валить_побудову_і_не_лишає_зрізу()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = chain.CreateContext();
        var seeded = await SeedResultsAsync(chain, db);

        // Другий рядок має Value = 5: ділення на нуль.
        var version = await PublishedAsync(db, RuledColumnsJson, ReportDefinitionSpec.RulesJson(
            new("CalculationResults", Rules: [new("1 / ([Value] - 5) > 0", new(HideRow: true))]), RuledColumns));

        var error = await Assert.ThrowsAsync<Ecr.Application.Errors.BusinessRuleException>(
            () => new ReportSnapshotBuilder(db, new TestClock(Now)).BuildAsync(
                version.Id, seeded.ProjectId, seeded.PeriodKey, null, CancellationToken.None));

        Assert.Equal("1", error.Details!["ruleNo"]);
        Assert.False(await db.ReportSnapshots.AsNoTracking().AnyAsync(s => s.ReportVersionId == version.Id));
    }

    private const string RuledColumnsJson = """[{"code":"OutputCode","kind":"text"},{"code":"Value","kind":"number"}]""";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Макет_не_чіпає_рядків_зрізу_і_суми_а_змінює_лише_видачу()
    {
        // ⛔ R8 цілиться рівно в це: макет — спосіб ПОКАЗУ. Якби він доїжджав
        // до `rpt.ReportRow`, та сама версія з групуванням і без нього давала б
        // різні контрольні суми — тобто сумою більше не можна було б довести,
        // що звіт не змінився.
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = chain.CreateContext();
        var seeded = await SeedResultsAsync(chain, db);
        var builder = new ReportSnapshotBuilder(db, new TestClock(Now));

        var plain = await PublishedAsync(db, RuledColumnsJson);
        var grouped = await PublishedAsync(db, RuledColumnsJson, ReportDefinitionSpec.RulesJson(
            new(
                "CalculationResults",
                Layout: new("OutputCode", [new("Value", "sum")], ShowGroupHeader: true)),
            RuledColumns));

        var plainId = await builder.BuildAsync(
            plain.Id, seeded.ProjectId, seeded.PeriodKey, null, CancellationToken.None);
        var groupedId = await builder.BuildAsync(
            grouped.Id, seeded.ProjectId, seeded.PeriodKey, null, CancellationToken.None);

        Assert.Equal(await HashAsync(db, plainId), await HashAsync(db, groupedId));
        Assert.Equal(
            (await CellsAsync(db, plainId)).Select(Key), (await CellsAsync(db, groupedId)).Select(Key));

        var page = await builder.RowsAsync(groupedId, 0, 10, Language, CancellationToken.None);
        var groups = page!.Groups!;

        Assert.Equal(["E_CO2", "E_NOX"], groups.Select(g => g.Value as string));
        Assert.Equal(12.5m, Assert.Single(groups[0].Totals).Value);
        Assert.Equal(17.5m, Assert.Single(page.Totals!).Value);
        Assert.True(page.ShowGroupHeader);
        Assert.Null(page.NextCursor);

        // Зріз без макета віддається як до R8: полів групи й підсумку немає взагалі.
        var flat = await builder.RowsAsync(plainId, 0, 10, Language, CancellationToken.None);
        Assert.Null(flat!.Groups);
        Assert.Null(flat.Totals);
        Assert.False(flat.ShowGroupHeader);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Сторінка_зрізу_з_макетом_іде_за_групою_а_підсумок_не_залежить_від_сторінки()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = chain.CreateContext();
        var seeded = await SeedResultsAsync(chain, db);
        var builder = new ReportSnapshotBuilder(db, new TestClock(Now));

        // Групування за `Value` ставить рядок 2 (5) перед рядком 1 (12.5).
        var version = await PublishedAsync(db, RuledColumnsJson, ReportDefinitionSpec.RulesJson(
            new("CalculationResults", Layout: new("Value", [new("Value", "sum")])), RuledColumns));

        var snapshotId = await builder.BuildAsync(
            version.Id, seeded.ProjectId, seeded.PeriodKey, null, CancellationToken.None);

        var first = await builder.RowsAsync(snapshotId, 0, 1, Language, CancellationToken.None);
        var second = await builder.RowsAsync(snapshotId, first!.NextCursor!.Value, 1, Language, CancellationToken.None);

        Assert.Equal(2, Assert.Single(first.Rows).RowNo);
        Assert.Equal(1, Assert.Single(second!.Rows).RowNo);
        Assert.Null(second.NextCursor);

        // ⚠ Підсумок — по ВСЬОМУ зрізу, тож на обох сторінках він однаковий.
        Assert.Equal(17.5m, Assert.Single(first.Totals!).Value);
        Assert.Equal(17.5m, Assert.Single(second.Totals!).Value);
    }

    /// <summary>Колонки з підписами: три мови, дві мови й жодної.</summary>
    private static readonly ReportColumnCommand[] NamedColumns =
    [
        new("Value", "number", new Dictionary<string, string>
        {
            ["en"] = "Amount", ["ru"] = "Объём", ["kz"] = "Мөлшері",
        }),
        new("UnitCode", "text", new Dictionary<string, string> { ["en"] = "Unit", ["kz"] = "Бірлік" }),
        new("OutputCode", "text"),
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.4")]
    public async Task Колонки_підписуються_мовою_запиту_з_фолбеком_через_en_до_коду()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = chain.CreateContext();
        var seeded = await SeedResultsAsync(chain, db);
        var builder = new ReportSnapshotBuilder(db, new TestClock(Now));

        var named = await PublishedAsync(db, ReportDefinitionSpec.ColumnsJson(NamedColumns));
        var plain = await PublishedAsync(
            db, ReportDefinitionSpec.ColumnsJson([.. NamedColumns.Select(c => c with { NameL10n = null })]));

        var namedId = await builder.BuildAsync(
            named.Id, seeded.ProjectId, seeded.PeriodKey, null, CancellationToken.None);
        var plainId = await builder.BuildAsync(
            plain.Id, seeded.ProjectId, seeded.PeriodKey, null, CancellationToken.None);

        // Мова запиту є в описі — підпис саме нею.
        var ru = await builder.RowsAsync(namedId, 0, 10, "ru", CancellationToken.None);
        Assert.Equal(["Объём", "Unit", "OutputCode"], ru!.Columns.Select(c => c.Name));

        // ⛔ Три ланки фолбеку в одному рядку: `uk` в описі немає ніде, тож
        // `Value` і `UnitCode` приходять англійськими, а `OutputCode`, який
        // назв не має взагалі, — власним КОДОМ.
        var uk = await builder.RowsAsync(namedId, 0, 10, "uk", CancellationToken.None);
        Assert.Equal(["Amount", "Unit", "OutputCode"], uk!.Columns.Select(c => c.Name));

        // Опис БЕЗ назв віддається рівно як до R9: підпис дорівнює коду.
        var flat = await builder.RowsAsync(plainId, 0, 10, "ru", CancellationToken.None);
        Assert.Equal(flat!.Columns.Select(c => c.Code), flat.Columns.Select(c => c.Name));
        Assert.Equal(["Value", "UnitCode", "OutputCode"], flat.Columns.Select(c => c.Name));

        // ⛔ А рядки й сума від назв не залежать узагалі: назва — ПОДАННЯ.
        // Якби вона доїжджала до `rpt.ReportRow`, перейменування колонки
        // читалося б контрольною сумою як підміна звіту.
        Assert.Equal(await HashAsync(db, plainId), await HashAsync(db, namedId));
        Assert.Equal(
            (await CellsAsync(db, plainId)).Select(Key), (await CellsAsync(db, namedId)).Select(Key));
    }

    /// <summary>Два зрізи тих самих даних: без правил (схема 1) і з правилом (схема 2).</summary>
    private async Task<(long Plain, long Ruled, ReportSnapshotBuilder Builder, EcrDbContext Db)> BuildPairAsync(
        ReportRuleCommand rule)
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var db = chain.CreateContext();
        var seeded = await SeedResultsAsync(chain, db);
        var builder = new ReportSnapshotBuilder(db, new TestClock(Now));

        var plain = await PublishedAsync(db, RuledColumnsJson);
        var ruled = await PublishedAsync(db, RuledColumnsJson, ReportDefinitionSpec.RulesJson(
            new("CalculationResults", Rules: [rule]), RuledColumns));

        return (
            await builder.BuildAsync(plain.Id, seeded.ProjectId, seeded.PeriodKey, null, CancellationToken.None),
            await builder.BuildAsync(ruled.Id, seeded.ProjectId, seeded.PeriodKey, null, CancellationToken.None),
            builder,
            db);
    }

    private static async Task<List<ReportRow>> CellsAsync(EcrDbContext db, long snapshotId)
        => await db.ReportRows.AsNoTracking()
            .Where(r => r.SnapshotId == snapshotId)
            .OrderBy(r => r.RowNo).ThenBy(r => r.ColumnCode)
            .ToListAsync();

    private static async Task<string> HashAsync(EcrDbContext db, long snapshotId)
        => Convert.ToHexString((await db.ReportSnapshots.AsNoTracking()
            .Where(s => s.Id == snapshotId).Select(s => s.ContentHash).SingleAsync())!);

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
                        CAST({text} AS decimal(34,16)), {unit.Id})
                """);
        }

        return new Seeded(document.ProjectId, document.PeriodKey, document.DocumentId, unit.Code, results);
    }

    private static async Task<ReportVersion> PublishedAsync(
        EcrDbContext db, string columnsJson, string rulesJson = """{"rowSource":"CalculationResults"}""")
    {
        var def = new ReportDef(
            EcrCode.Create($"RPT{Guid.NewGuid().ToString("N")[..8]}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Layout test" }),
            isRegulatory: true);
        db.ReportDefs.Add(def);
        await db.SaveChangesAsync();

        var version = new ReportVersion(def.Id, "1.0", columnsJson, rulesJson, Now);
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
