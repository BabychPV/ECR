// tests/Ecr.Infrastructure.Tests/Persistence/CalculationIssueStoreTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// AN-69: <c>calc.CalculationIssue</c> тримає лише поточні проблеми - прохід замінює свої рядки, копія аркуша не
/// змішується з оригіналом, стеля 1000 на таблицю, читання - одна SQL-команда.
/// </summary>
/// <remarks>
/// ⚠ Потребують таблиці <c>calc.CalculationIssue</c> - запускаються лише після міграції CI01 (до неї модель EF
/// розходиться зі схемою, фікстура не будує базу). Мутації (після CI01): прибрати <c>ExecuteDeleteAsync</c> у
/// <c>ReplaceAsync</c> - червоніє тест заміни; прибрати <c>Take(MaxPerTable)</c> - тест стелі; прибрати умову
/// <c>SheetCopyId</c> у <c>ListAsync</c> - тест копії.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationIssueStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime T0 = new(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Другий_прохід_замінює_перший_а_виправлені_рядки_зникають()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202611);
        await using var db = f.Builder.CreateContext();
        var run = await PurgeKit.AddRunAsync(db, f, "Running", T0);
        var store = new CalculationIssueStore(db, new TestClock(T0));
        var table = f.Document.TableInstanceId;

        await store.ReplaceAsync(run, [table], [Issue(table, 1), Issue(table, 2), Issue(table, 3)], CancellationToken.None);
        await db.SaveChangesAsync();
        Assert.Equal([1, 2, 3], await RowNumbersAsync(store, f, null));

        await store.ReplaceAsync(run, [table], [Issue(table, 2)], CancellationToken.None);
        await db.SaveChangesAsync();
        Assert.Equal([2], await RowNumbersAsync(store, f, null));

        // Таблиця перераховано, проблем немає - порожньо (а не «лишилося старе»).
        await store.ReplaceAsync(run, [table], [], CancellationToken.None);
        await db.SaveChangesAsync();
        Assert.Empty(await RowNumbersAsync(store, f, null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Копія_аркуша_не_змішується_з_оригіналом_і_заміна_оригіналу_її_не_чіпає()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202612);
        await using var db = f.Builder.CreateContext();
        var run = await PurgeKit.AddRunAsync(db, f, "Running", T0);
        var store = new CalculationIssueStore(db, new TestClock(T0));
        var table = f.Document.TableInstanceId;

        // AN-160: таблиця без активної прив'язки проблем не тримає - для цього тесту вона прив'язана.
        db.CalculationBindings.Add(new Ecr.Domain.Entities.Configuration.CalculationBinding(
            f.Document.TableDefId, f.Document.ColumnDefIds[0], f.MethodologyId, "OUT1", "{}"));
        await db.SaveChangesAsync();

        // Проблема копії записана напряму (екземпляр копії вимагає ланцюга DocumentSheet, тут не потрібного).
        db.CalculationIssues.Add(new CalculationIssue(
            f.Document.DocumentId, f.Period, f.Document.TableDefId, sheetCopyId: 77, "RC", 9, null, run, T0));
        await db.SaveChangesAsync();

        await store.ReplaceAsync(run, [table], [Issue(table, 1)], CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal([1], await RowNumbersAsync(store, f, null));
        Assert.Equal([9], await RowNumbersAsync(store, f, 77));
        Assert.Empty(await RowNumbersAsync(store, f, 78));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Понад_стелю_на_таблицю_зберігається_1000_найменших_номерів_а_решта_рахується()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202701);
        await using var db = f.Builder.CreateContext();
        var run = await PurgeKit.AddRunAsync(db, f, "Running", T0);
        var store = new CalculationIssueStore(db, new TestClock(T0));
        var table = f.Document.TableInstanceId;

        var dropped = await store.ReplaceAsync(
            run, [table], [.. Enumerable.Range(1, 1005).Select(n => Issue(table, n))], CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(5, dropped);
        var stored = await RowNumbersAsync(store, f, null);
        Assert.Equal(ICalculationIssueStore.MaxPerTable, stored.Count);
        Assert.Equal(1, stored[0]);
        Assert.Equal(1000, stored[^1]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Код_методології_у_читанні_це_код_методології_а_не_мітка_версії()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202703);
        await using var db = f.Builder.CreateContext();
        var run = await PurgeKit.AddRunAsync(db, f, "Running", T0);
        var store = new CalculationIssueStore(db, new TestClock(T0));
        var table = f.Document.TableInstanceId;
        var code = await db.Methodologies.AsNoTracking().Where(m => m.Id == f.MethodologyId).Select(m => m.Code).SingleAsync();
        var label = await db.MethodologyVersions.AsNoTracking().Where(v => v.Id == f.VersionId).Select(v => v.Version).SingleAsync();

        await store.ReplaceAsync(run, [table], [Issue(table, 1, f.MethodologyId), Issue(table, 2)], CancellationToken.None);
        await db.SaveChangesAsync();

        var rows = await store.ListAsync(f.Document.DocumentId, f.Period, null, CancellationToken.None);
        Assert.Equal(code, rows.Single(r => r.RowNumber == 1).MethodologyCode);
        Assert.NotEqual(label, rows.Single(r => r.RowNumber == 1).MethodologyCode);
        Assert.Null(rows.Single(r => r.RowNumber == 2).MethodologyCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читання_проблем_це_одна_SQL_команда()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202702);
        await using var db = f.Builder.CreateContext();
        var run = await PurgeKit.AddRunAsync(db, f, "Running", T0);
        var store = new CalculationIssueStore(db, new TestClock(T0));
        var table = f.Document.TableInstanceId;
        await store.ReplaceAsync(run, [table], [Issue(table, 1), Issue(table, 2)], CancellationToken.None);
        await db.SaveChangesAsync();

        await using var reader = f.Builder.CreateContext();
        var readStore = new CalculationIssueStore(reader, new TestClock(T0));
        using var counter = new SqlClientCommandCounter(new CommandTally());
        counter.Tally.Reset();

        var rows = await readStore.ListAsync(f.Document.DocumentId, f.Period, null, CancellationToken.None);

        counter.AssertObserved();
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, counter.Tally.Snapshot().Total);
    }

    private static CalculationIssueInput Issue(long tableInstanceId, int row, int? methodologyId = null)
        => new(tableInstanceId, row, $"R{row}", methodologyId);

    private static async Task<List<int>> RowNumbersAsync(CalculationIssueStore store, PurgeStand f, long? copy)
        => [.. (await store.ListAsync(f.Document.DocumentId, f.Period, copy, CancellationToken.None))
            .Select(r => r.RowNumber)];
}
