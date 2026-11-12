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

    private static FormulaErrorRow Row(TestDocument doc, int row, string kind, string code)
        => new(
            doc.DocumentId, doc.PeriodKey.Value, doc.TableInstanceId, doc.TableDefId, null, doc.RowIds[row], $"R{row + 1}",
            doc.ColumnDefIds[2], FormulaDefId: 1, kind, code,
            kind == FormulaError.ErrorKind ? """[{"columnCode":"B","value":"0"}]""" : null,
            null, null, null, null, T0);
}
