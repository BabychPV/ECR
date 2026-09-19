// tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotVerifyTests.cs
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Перевірка зрізу на РЕАЛЬНІЙ базі (BE-17): сума перераховується за тим, що
/// справді лежить у <c>rpt.ReportRow</c>, а не переказується зі збереженої.
/// </summary>
/// <remarks>
/// ⚠ Саме на базі, а не на підробці: вся складність — у колі через
/// <c>decimal(28,10)</c> (масштаб числа після читання інший, ніж при побудові)
/// і в порядку рядків, який первинний ключ дає алфавітним за кодом колонки.
/// Обидва дефекти на підробці невидимі.
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportSnapshotVerifyTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Незмінений_зріз_із_числами_збігається_після_кола_через_базу()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var snapshotId = await SeedAsync(chain);

        await using var db = chain.CreateContext();
        var hashes = await new ReportSnapshotBuilder(db, new TestClock(Now))
            .VerifyAsync(snapshotId, CancellationToken.None);

        Assert.NotNull(hashes);
        Assert.Equal(64, hashes.Stored.Length);
        Assert.Equal(hashes.Stored, hashes.Actual);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Вміст_підмінено_після_створення_суми_розходяться()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var snapshotId = await SeedAsync(chain);

        await using var db = chain.CreateContext();

        // Підміна повз застосунок — рівно те, від чого сума й захищає.
        var touched = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE rpt.ReportRow SET ValueNumeric = 999 WHERE SnapshotId = {snapshotId} AND ColumnCode = 'Value'");
        Assert.Equal(1, touched);

        var builder = new ReportSnapshotBuilder(db, new TestClock(Now));
        var hashes = await builder.VerifyAsync(snapshotId, CancellationToken.None);

        Assert.NotNull(hashes);
        Assert.NotEqual(hashes.Stored, hashes.Actual);

        // Перевірка нічого не лагодить: збережена сума лишилась як була.
        var again = await builder.VerifyAsync(snapshotId, CancellationToken.None);
        Assert.Equal(hashes, again);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зрізу_немає_перевірка_віддає_null()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        await chain.BuildAsync();

        await using var db = chain.CreateContext();
        var builder = new ReportSnapshotBuilder(db, new TestClock(Now));

        Assert.Null(await builder.VerifyAsync(long.MaxValue, CancellationToken.None));
        Assert.Null(await builder.FindProjectIdAsync(long.MaxValue, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Зріз_із_сумою_старого_формату_визнається_незмінним_за_legacy()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var snapshotId = await SeedAsync(chain, LegacyRows, HashBeforeBe17);

        await using var db = chain.CreateContext();

        // Засновок відтворення: з `decimal(28,10)` число повертається з
        // масштабом 10 — так само `Value` приходило з `calc.CalculationResult`
        // у мить побудови. Зламається це — зламається й відтворення.
        var value = await db.ReportRows.AsNoTracking()
            .Where(r => r.SnapshotId == snapshotId && r.ColumnCode == "Value")
            .Select(r => r.ValueNumeric)
            .SingleAsync();
        Assert.Equal("5.0000000000", value!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var hashes = await new ReportSnapshotBuilder(db, new TestClock(Now))
            .VerifyAsync(snapshotId, CancellationToken.None);

        Assert.NotNull(hashes);
        Assert.NotEqual(hashes.Stored, hashes.Actual);
        Assert.Equal(hashes.Stored, hashes.LegacyActual);
    }

    [Theory]
    [InlineData("Value", "999")]
    [InlineData("Value", "5.0000000001")]
    [InlineData("DocumentId", "4217.5")]
    [InlineData("SubstanceEntryId", "89")]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Підмінений_зріз_старого_формату_не_збігається_за_жодним_форматом(
        string column, string tampered)
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var snapshotId = await SeedAsync(chain, LegacyRows, HashBeforeBe17);

        await using var db = chain.CreateContext();

        // ⚠ Число йде РЯДКОМ і перетворюється в SQL: параметр `decimal` EF
        // оголошує як `decimal(18,2)`, і `5.0000000001` доїхало б як `5.00` —
        // «підміна» нічого б не змінила, а тест звинуватив би продукт.
        var touched = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE rpt.ReportRow SET ValueNumeric = CAST({tampered} AS decimal(28,10)) WHERE SnapshotId = {snapshotId} AND ColumnCode = {column}");
        Assert.Equal(1, touched);

        var now = await db.ReportRows.AsNoTracking()
            .Where(r => r.SnapshotId == snapshotId && r.ColumnCode == column)
            .Select(r => r.ValueNumeric)
            .SingleAsync();
        Assert.Equal(decimal.Parse(tampered, System.Globalization.CultureInfo.InvariantCulture), now);

        var hashes = await new ReportSnapshotBuilder(db, new TestClock(Now))
            .VerifyAsync(snapshotId, CancellationToken.None);

        Assert.NotNull(hashes);
        Assert.NotEqual(hashes.Stored, hashes.Actual);
        Assert.NotNull(hashes.LegacyActual);
        Assert.NotEqual(hashes.Stored, hashes.LegacyActual);
    }

    /// <summary>
    /// Рядки з масштабом чисел МИТІ ПОБУДОВИ: ідентифікатори — з <c>long</c>
    /// (масштаб 0), значення — з <c>decimal(28,10)</c> (масштаб 10). Ціле
    /// значення взято навмисно: «5» проти «5.0000000000» — саме та пастка.
    /// </summary>
    private static List<ReportRow> LegacyRows(long snapshotId) =>
    [
        Cell(snapshotId, "DocumentId", null, 4217L),
        Cell(snapshotId, "RowKey", "row-1", null),
        Cell(snapshotId, "OutputCode", "E_CO2", null),
        Cell(snapshotId, "Value", null, 5.0000000000m),
        Cell(snapshotId, "SubstanceEntryId", null, 88L),
    ];

    /// <summary>
    /// Сума ТАК, як її рахував <c>ReportSnapshotBuilder.Hash</c> до BE-17
    /// (дослівна копія з коміту 34e5370). ⛔ Не виражати через продуктовий
    /// код: тоді тест порівнював би відтворення із самим собою.
    /// </summary>
    private static byte[] HashBeforeBe17(IReadOnlyList<ReportRow> rows)
    {
        var text = string.Join(
            '\n',
            rows.Select(r => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{r.RowNo}|{r.ColumnCode}|{r.ValueString}|{r.ValueNumeric}")));

        return System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
    }

    private static Task<long> SeedAsync(TestDocumentBuilder chain)
        => SeedAsync(
            chain,
            id =>
            [

                // ⚠ Масштаб чисел — як при побудові: ідентифікатори цілі
                // (масштаб 0), значення — дріб. Після кола через
                // `decimal(28,10)` усе матиме масштаб 10, і сума мусить це
                // пережити.
                Cell(id, "DocumentId", null, 4217L),
                Cell(id, "RowKey", "row-1", null),
                Cell(id, "OutputCode", "E_CO2", null),
                Cell(id, "Value", null, 12.5m),
                Cell(id, "SubstanceEntryId", null, null),
            ],
            ReportSnapshotBuilder.ComputeHash);

    /// <summary>Зріз з одним рядком результату — у тому ж вигляді, що дає побудова.</summary>
    private static async Task<long> SeedAsync(
        TestDocumentBuilder chain,
        Func<long, List<ReportRow>> cells,
        Func<IReadOnlyList<ReportRow>, byte[]> hash)
    {
        var document = await chain.BuildAsync();

        await using var db = chain.CreateContext();

        var def = new ReportDef(
            EcrCode.Create($"RPT{Guid.NewGuid().ToString("N")[..8]}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Verify test" }),
            isRegulatory: true);

        db.ReportDefs.Add(def);
        await db.SaveChangesAsync(CancellationToken.None);

        var version = new ReportVersion(def.Id, "1.0", "[]", "{}", Now);
        version.Publish();
        db.ReportVersions.Add(version);
        await db.SaveChangesAsync(CancellationToken.None);

        var snapshot = new ReportSnapshot(
            version.Id, document.ProjectId, document.PeriodKey.Value, SnapshotStatus.Draft, Now, null);

        db.ReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync(CancellationToken.None);

        var rows = cells(snapshot.Id);

        db.ReportRows.AddRange(rows);
        snapshot.Complete(1, hash(rows), null, null);
        await db.SaveChangesAsync(CancellationToken.None);

        return snapshot.Id;
    }

    private static ReportRow Cell(long snapshotId, string column, string? text, decimal? number)
    {
        var row = new ReportRow(snapshotId, 1, column);
        row.SetValue(text, number, null);
        return row;
    }
}
