using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// CELL-STYLE L3b: сховище користувацького оформлення комірок (<c>doc.CellStyle</c>) — читання
/// екземпляра, база відхиляє нерозбірливий колір, видалення документа не лишає сиріт.
/// </summary>
[Collection("SqlServer")]
public sealed class CellStyleStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime At = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зріз_екземпляра_читає_лише_оформлення_його_рядків()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 2, rowCount: 2);
        var other = await builder.BuildAsync(columnCount: 1, rowCount: 1);

        await using (var db = builder.CreateContext())
        {
            db.CellStyles.Add(Styled(doc, doc.RowIds[0], doc.ColumnDefIds[0], "#FFFF00"));
            db.CellStyles.Add(Styled(doc, doc.RowIds[1], doc.ColumnDefIds[1], "#99CCFF"));
            db.CellStyles.Add(Styled(other, other.RowIds[0], other.ColumnDefIds[0], "#FF0000"));
            await db.SaveChangesAsync();
        }

        await using var read = builder.CreateContext();
        var styles = await new CellStyleStore(read).ReadInstanceAsync(doc.TableInstanceId, doc.PeriodKey, CancellationToken.None);

        Assert.Equal(2, styles.Count);
        Assert.All(styles, s => Assert.Contains(s.TableRowId, doc.RowIds));
        Assert.Contains(styles, s => s.BackgroundHex == "#FFFF00");
        Assert.Contains(styles, s => s.BackgroundHex == "#99CCFF");

        var batch = await new CellStyleStore(read).ReadInstancesAsync(
            [doc.TableInstanceId, other.TableInstanceId], doc.PeriodKey, CancellationToken.None);
        Assert.Equal(2, batch[doc.TableInstanceId].Count);
        Assert.False(batch.ContainsKey(other.TableInstanceId) && batch[other.TableInstanceId].Count != 1);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task База_відхиляє_колір_не_у_формі_rrggbb_навіть_повз_домен()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 1, rowCount: 1);

        await using var db = builder.CreateContext();
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlAsync(
            $"INSERT INTO doc.CellStyle (PeriodKey, TableRowId, ColumnDefId, BackgroundHex, IsBold, UpdatedAt, UpdatedByUserId) VALUES ({doc.PeriodKey.Value}, {doc.RowIds[0]}, {doc.ColumnDefIds[0]}, 'red', 0, {At}, 1)"));

        Assert.Contains("CK_CellStyle_Background", thrown.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видалення_документа_прибирає_його_оформлення_і_не_чіпає_чуже()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 1, rowCount: 1);
        var other = await builder.BuildAsync(columnCount: 1, rowCount: 1);

        await using (var db = builder.CreateContext())
        {
            db.CellStyles.Add(Styled(doc, doc.RowIds[0], doc.ColumnDefIds[0], "#FFFF00"));
            db.CellStyles.Add(Styled(other, other.RowIds[0], other.ColumnDefIds[0], "#FF0000"));
            await db.SaveChangesAsync();
        }

        await using (var db = builder.CreateContext())
        {
            await new DocumentDeletionStore(db).DeleteAsync(doc.DocumentId, CancellationToken.None);
        }

        await using var check = builder.CreateContext();
        Assert.False(await check.CellStyles.AnyAsync(
            s => s.PeriodKeyValue == doc.PeriodKey.Value && s.TableRowId == doc.RowIds[0]));
        Assert.True(await check.CellStyles.AnyAsync(
            s => s.PeriodKeyValue == other.PeriodKey.Value && s.TableRowId == other.RowIds[0]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Завантаження_для_зміни_приймає_рівно_стелю_у_2000_рядків_на_справжній_СУБД()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 1, rowCount: 1);

        await using (var db = builder.CreateContext())
        {
            db.CellStyles.Add(Styled(doc, doc.RowIds[0], doc.ColumnDefIds[0], "#FFFF00"));
            await db.SaveChangesAsync();
        }

        // 2000 ідентифікаторів: перший — справжній, решта — вигадані (рядків немає).
        var ids = Enumerable.Range(0, 2000).Select(i => i == 0 ? doc.RowIds[0] : 9_000_000_000L + i).ToList();

        await using var read = builder.CreateContext();
        var loaded = await new CellStyleStore(read).LoadForUpdateAsync(doc.PeriodKey, ids, CancellationToken.None);

        var single = Assert.Single(loaded);
        Assert.Equal("#FFFF00", single.BackgroundHex);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Гонка_двох_збережень_нового_оформлення_дає_409_а_не_500_і_один_рядок()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 1, rowCount: 1);

        // Обидва «читали»: оформлення немає — і обидва додають (детерміновано, без таймінгів).
        await using var first = builder.CreateContext();
        await using var second = builder.CreateContext();
        var a = new CellStyleStore(first);
        var b = new CellStyleStore(second);
        Assert.Empty(await a.LoadForUpdateAsync(doc.PeriodKey, [doc.RowIds[0]], CancellationToken.None));
        Assert.Empty(await b.LoadForUpdateAsync(doc.PeriodKey, [doc.RowIds[0]], CancellationToken.None));

        a.Add(Styled(doc, doc.RowIds[0], doc.ColumnDefIds[0], "#FFFF00"));
        b.Add(Styled(doc, doc.RowIds[0], doc.ColumnDefIds[0], "#FF0000"));

        await new UnitOfWork(first).SaveChangesAsync(CancellationToken.None);
        var ex = await Assert.ThrowsAsync<Ecr.Application.Errors.ConcurrencyConflictException>(
            () => new UnitOfWork(second).SaveChangesAsync(CancellationToken.None));

        Assert.Equal("ECR-CELL-0409", ex.ErrorCode);
        Assert.Equal("err.ECR-CELL-0409.concurrentChange", ex.Details!["messageKey"]);

        await using var check = builder.CreateContext();
        var stored = await check.CellStyles.AsNoTracking()
            .Where(s => s.PeriodKeyValue == doc.PeriodKey.Value && s.TableRowId == doc.RowIds[0]).ToListAsync();
        Assert.Equal("#FFFF00", Assert.Single(stored).BackgroundHex);
    }

    private static CellStyle Styled(TestDocument doc, long rowId, int columnId, string background)
    {
        var style = new CellStyle(new CellAddress(doc.PeriodKey, rowId, columnId), userId: 1, At);
        style.Apply(background, null, null, 1, At);
        return style;
    }
}
