// tests/Ecr.Infrastructure.Tests/Persistence/FormulaErrorStoreTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ C-1 (D-475): <c>calc.FormulaError</c> тримає лише ПОТОЧНИЙ стан комірки — заміна не порушує унікального індексу,
/// читання документа — одна команда без рядків видалених <c>doc.TableRow</c>, прапорець екземпляра — EXISTS у тій самій
/// команді переліку екземплярів.
/// </summary>
/// <remarks>
/// Очікувані мутації (не запускались окремо): прибрати DELETE в <c>ApplyAsync</c> — червоніє тест заміни (UX_FormulaError_Cell);
/// прибрати <c>where !r.IsDeleted</c> у <c>ListAsync</c> — червоніє тест видаленого рядка; прапорець у
/// <c>GetTableInstancesAsync</c> завжди <c>false</c> — червоніє тест прапорця.
/// </remarks>
[Collection("SqlServer")]
public sealed class FormulaErrorStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime T0 = new(2026, 11, 11, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Стан_комірки_замінюється_а_видалення_знімає_його()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202611);
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var store = new FormulaErrorStore(db);
        var c = Row(doc, 0, FormulaError.ErrorKind, "#DIV/0");
        var d = Row(doc, 1, FormulaError.BlockedKind, "#DIV/0") with { SourceRowKey = "R1", SourceTableDefId = doc.TableDefId, SourceColumnDefId = doc.ColumnDefIds[2], SourcePeriodKey = doc.PeriodKey.Value };

        await store.ApplyAsync([c, d], [], CancellationToken.None);
        await db.SaveChangesAsync();

        // Та сама комірка з іншим станом — заміна, а не другий рядок (унікальний індекс не порушено).
        await store.ApplyAsync([c with { ErrorCode = "#REF" }], [], CancellationToken.None);
        await db.SaveChangesAsync();

        var after = await store.ReadInstancesAsync([doc.TableInstanceId], doc.PeriodKey, CancellationToken.None);
        Assert.Equal(2, after.Count);
        Assert.Equal("#REF", after.Single(r => r.TableRowId == c.TableRowId).ErrorCode);

        await store.ApplyAsync([], [d.Address], CancellationToken.None);
        await db.SaveChangesAsync();
        var left = await store.ReadInstancesAsync([doc.TableInstanceId], doc.PeriodKey, CancellationToken.None);
        Assert.Equal(c.TableRowId, Assert.Single(left).TableRowId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читання_документа_одна_команда_без_видалених_рядків()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202612);
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var store = new FormulaErrorStore(db);
            await store.ApplyAsync(
                [Row(doc, 0, FormulaError.ErrorKind, "#DIV/0"), Row(doc, 1, FormulaError.ErrorKind, "#REF")], [], CancellationToken.None);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE doc.TableRow SET IsDeleted = 1 WHERE PeriodKey = {doc.PeriodKey.Value} AND Id = {doc.RowIds[1]};");
        }

        await using var reader = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var readStore = new FormulaErrorStore(reader);
        using var counter = new SqlClientCommandCounter(new CommandTally());
        counter.Tally.Reset();

        var rows = await readStore.ListAsync(doc.DocumentId, doc.PeriodKey.Value, null, CancellationToken.None);

        counter.AssertObserved();
        Assert.Equal(1, counter.Tally.Snapshot().Total);
        var only = Assert.Single(rows);
        Assert.Equal(doc.RowIds[0], only.Row.TableRowId);
        Assert.False(string.IsNullOrEmpty(only.TableCode));
        Assert.False(string.IsNullOrEmpty(only.ColumnCode));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прапорець_екземпляра_лише_з_рядком_стану_і_тією_ж_командою()
    {
        var withError = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202701);
        var clean = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202701);
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            await new FormulaErrorStore(db).ApplyAsync(
                [Row(withError, 0, FormulaError.ErrorKind, "#DIV/0")], [], CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var reader = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var rows = new RowStore(reader, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(T0));
        using var counter = new SqlClientCommandCounter(new CommandTally());
        counter.Tally.Reset();

        var flagged = await rows.GetTableInstancesAsync(withError.DocumentId, withError.PeriodKey, CancellationToken.None);

        counter.AssertObserved();
        Assert.Equal(1, counter.Tally.Snapshot().Total);
        Assert.True(Assert.Single(flagged).HasFormulaErrors);

        var plain = await rows.GetTableInstancesAsync(clean.DocumentId, clean.PeriodKey, CancellationToken.None);
        Assert.False(Assert.Single(plain).HasFormulaErrors);

        var resolved = await rows.ResolveTableInstanceAsync(withError.TableInstanceId, CancellationToken.None);
        Assert.True(resolved.HasFormulaErrors);
    }

    /// <summary>
    /// ✎ B78 (живий прохід W15t, doc 96 Land): динамічна таблиця — у всіх рядків <c>doc.TableRow.Ordinal = 0</c> і
    /// немає опису рядка шаблону; банер показував «рядок 0» на кожній із 19 комірок. Номер — позиція в сітці з 1.
    /// </summary>
    /// <remarks>Червоне до фіксу: <c>RowOrdinal</c> брався з <c>r.Ordinal</c> → 0 і 0 замість 1 і 3.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Номер_рядка_динамічної_таблиці_позиція_в_сітці_а_не_Ordinal()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202702);
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            await new FormulaErrorStore(db).ApplyAsync(
                [Row(doc, 2, FormulaError.ErrorKind, "#N/A"), Row(doc, 0, FormulaError.ErrorKind, "#N/A")], [], CancellationToken.None);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE doc.TableRow SET Ordinal = 0 WHERE PeriodKey = {doc.PeriodKey.Value} AND TableInstanceId = {doc.TableInstanceId};");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE cfg.RowDef SET IsDeleted = 1 WHERE TableDefId = {doc.TableDefId};");
        }

        await using var reader = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var rows = await new FormulaErrorStore(reader).ListAsync(doc.DocumentId, doc.PeriodKey.Value, null, CancellationToken.None);

        Assert.Equal([(doc.RowIds[0], 1), (doc.RowIds[2], 3)], rows.Select(r => (r.Row.TableRowId, r.RowOrdinal)));
    }

    /// <summary>
    /// ✎ B78: порядок той самий, що в зрізі сітки (<c>GetTableSliceHandler</c>): Ordinal опису рядка шаблону за RowKey,
    /// далі <c>TableRow.Id</c> — а не Id сам по собі.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Номер_рядка_за_порядком_опису_рядка_шаблону()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202703);
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            await new FormulaErrorStore(db).ApplyAsync(
                [Row(doc, 0, FormulaError.ErrorKind, "#N/A"), Row(doc, 3, FormulaError.ErrorKind, "#N/A")], [], CancellationToken.None);
            await db.SaveChangesAsync();

            // Шаблон перевертає порядок: R4 перший, R1 останній.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE cfg.RowDef SET Ordinal = 10 - Ordinal WHERE TableDefId = {doc.TableDefId};");
        }

        await using var reader = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var rows = await new FormulaErrorStore(reader).ListAsync(doc.DocumentId, doc.PeriodKey.Value, null, CancellationToken.None);

        Assert.Equal([(doc.RowIds[3], 1), (doc.RowIds[0], 4)], rows.Select(r => (r.Row.TableRowId, r.RowOrdinal)));
    }

    /// <summary>
    /// ✎ B93 (d): опис рядка шаблону шукається за RowKey ТОЧНО (ordinal), як у сітці (<c>GetTableSliceHandler</c>,
    /// <c>StringComparer.Ordinal</c>), а не за collation бази (CI): ключ, що відрізняється регістром, опису не має — рядок
    /// іде в кінець за Id, і номер у деталях збігається з сіткою.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Опис_рядка_за_RowKey_точним_збігом_як_у_сітці()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202705);
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            await new FormulaErrorStore(db).ApplyAsync(
                [Row(doc, 0, FormulaError.ErrorKind, "#N/A"), Row(doc, 3, FormulaError.ErrorKind, "#N/A")], [], CancellationToken.None);
            await db.SaveChangesAsync();

            // Опис шаблону перевертає порядок, але його ключі — в іншому регістрі ("r1" ≠ "R1" для сітки).
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE cfg.RowDef SET Ordinal = 10 - Ordinal, RowKey = LOWER(RowKey) WHERE TableDefId = {doc.TableDefId};");
        }

        await using var reader = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var rows = await new FormulaErrorStore(reader).ListAsync(doc.DocumentId, doc.PeriodKey.Value, null, CancellationToken.None);

        Assert.Equal([(doc.RowIds[0], 1), (doc.RowIds[3], 4)], rows.Select(r => (r.Row.TableRowId, r.RowOrdinal)));
    }

    /// <summary>
    /// ✎ B78: назва запису — лише для колонки, що посилається САМЕ на довідник запису; той самий Id у числовій колонці
    /// назвою не стає. Один запит.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Назви_записів_лише_для_довідникової_колонки_входу()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202704);
        long entryId;
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var registry = new Ecr.Domain.Entities.Configuration.RegistryDef(
                EcrCode.Create($"FUEL_B78_{doc.TableDefId}"), Text("Fuels"), isTemporal: false);
            db.RegistryDefs.Add(registry);
            await db.SaveChangesAsync();
            var entry = new Ecr.Domain.Entities.Dictionaries.RegistryEntry(registry.Id, EcrCode.Create("F37"), Text("Diesel"));
            db.RegistryEntries.Add(entry);
            await db.SaveChangesAsync();
            entryId = entry.Id;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE cfg.ColumnDef SET LookupRegistryDefId = {registry.Id} WHERE Id = {doc.ColumnDefIds[0]};");
        }

        await using var reader = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        using var counter = new SqlClientCommandCounter(new CommandTally());
        counter.Tally.Reset();

        var names = await new FormulaErrorStore(reader).LookupNamesAsync(
            [(doc.ColumnDefIds[0], entryId), (doc.ColumnDefIds[1], entryId)], CancellationToken.None);

        counter.AssertObserved();
        Assert.Equal(1, counter.Tally.Snapshot().Total);
        var only = Assert.Single(names);
        Assert.Equal((doc.ColumnDefIds[0], entryId), only.Key);
        Assert.Equal("Diesel", only.Value.Get("en"));
    }

    private static LocalizedText Text(string en) => new(new Dictionary<string, string> { ["en"] = en });

    private static FormulaErrorRow Row(TestDocument doc, int row, string kind, string code)
        => new(
            doc.DocumentId, doc.PeriodKey.Value, doc.TableInstanceId, doc.TableDefId, null, doc.RowIds[row], $"R{row + 1}",
            doc.ColumnDefIds[2], FormulaDefId: 1, kind, code,
            kind == FormulaError.ErrorKind ? """[{"columnCode":"B","value":"0"}]""" : null,
            null, null, null, null, T0);
}
