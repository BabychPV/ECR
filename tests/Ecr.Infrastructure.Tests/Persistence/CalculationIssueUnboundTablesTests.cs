// tests/Ecr.Infrastructure.Tests/Persistence/CalculationIssueUnboundTablesTests.cs
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// AN-160: таблиця без жодної активної прив'язки методики не тримає поточних проблем розрахунку - їх прибирає і зняття
/// останньої прив'язки (<c>DeleteForUnboundTablesAsync</c> за таблицями), і самолікування на проході
/// (<c>ReplaceAsync</c>) для таблиць, що вже лишилися без прив'язок.
/// </summary>
/// <remarks>
/// Мутації: зробити <c>DeleteForUnboundTablesAsync</c> порожнім - червоніють тести 1 і 2; прибрати з нього умову
/// <c>IsActive</c> - червоніє тест 3.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationIssueUnboundTablesTests(SqlServerFixture sql)
{
    private static readonly DateTime T0 = new(2026, 11, 5, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Таблиця, якої немає в стенді: без прив'язок, але з проблемами - чужа "інша" таблиця.</summary>
    private const int OtherTable = 987654;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зняття_останньої_прив_язки_прибирає_проблеми_цієї_таблиці_а_чужі_лишає()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202801);
        await using var db = f.Builder.CreateContext();
        var run = await PurgeKit.AddRunAsync(db, f, "Running", T0);
        var binding = await BindAsync(db, f);
        AddIssue(db, f, f.Document.TableDefId, run);
        AddIssue(db, f, OtherTable, run);
        await db.SaveChangesAsync();
        var store = new CalculationIssueStore(db, new TestClock(T0));

        binding.Update("{}", isActive: false);
        await db.SaveChangesAsync();
        var removed = await store.DeleteForUnboundTablesAsync(null, null, [f.Document.TableDefId], CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.Equal(0, await CountAsync(db, f.Document.TableDefId));
        Assert.Equal(1, await CountAsync(db, OtherTable));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Таблиця_що_вже_без_прив_язок_втрачає_залишені_проблеми_на_наступному_проході()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202802);
        await using var db = f.Builder.CreateContext();
        var run = await PurgeKit.AddRunAsync(db, f, "Running", T0);
        AddIssue(db, f, OtherTable, run);
        AddIssue(db, f, OtherTable, run, row: 2);
        await db.SaveChangesAsync();
        var store = new CalculationIssueStore(db, new TestClock(T0));

        // Прохід, що цієї таблиці не відвідує (перераховано лише таблицю стенда без проблем).
        await store.ReplaceAsync(run, [f.Document.TableInstanceId], [], CancellationToken.None);

        Assert.Equal(0, await CountAsync(db, OtherTable));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Таблиця_з_активною_прив_язкою_зберігає_проблеми_і_при_очищенні_і_на_проході()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202803);
        await using var db = f.Builder.CreateContext();
        var run = await PurgeKit.AddRunAsync(db, f, "Running", T0);
        await BindAsync(db, f);
        AddIssue(db, f, f.Document.TableDefId, run);
        await db.SaveChangesAsync();
        var store = new CalculationIssueStore(db, new TestClock(T0));

        var removed = await store.DeleteForUnboundTablesAsync(null, null, [f.Document.TableDefId], CancellationToken.None);
        Assert.Equal(0, removed);
        Assert.Equal(1, await CountAsync(db, f.Document.TableDefId));

        // Прохід по іншій таблиці стенда не зачіпає проблем прив'язаної таблиці (самолікування чіпає лише таблиці без прив'язок).
        await store.ReplaceAsync(run, [], [], CancellationToken.None);
        Assert.Equal(1, await CountAsync(db, f.Document.TableDefId));
    }

    private static async Task<CalculationBinding> BindAsync(EcrDbContext db, PurgeStand f)
    {
        var binding = new CalculationBinding(
            f.Document.TableDefId, f.Document.ColumnDefIds[0], f.MethodologyId, "OUT1", "{}");
        db.CalculationBindings.Add(binding);
        await db.SaveChangesAsync();
        return binding;
    }

    private static void AddIssue(EcrDbContext db, PurgeStand f, int tableDefId, long run, int row = 1)
        => db.CalculationIssues.Add(new CalculationIssue(
            f.Document.DocumentId, f.Period, tableDefId, sheetCopyId: null, $"R{row}", row, null, run, T0));

    private static Task<int> CountAsync(EcrDbContext db, int tableDefId)
        => db.CalculationIssues.AsNoTracking().CountAsync(i => i.TableDefId == tableDefId);
}
