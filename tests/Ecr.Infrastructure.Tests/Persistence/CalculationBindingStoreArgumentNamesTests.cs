// tests/Ecr.Infrastructure.Tests/Persistence/CalculationBindingStoreArgumentNamesTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ D-571 (репетиція W15z на копії live): <see cref="CalculationBindingStore.ListColumnCodesAsync"/> — імена, з яких
/// <c>CalculationInputBuilder</c> будує аргументи рядка, а звірка <c>ECR-CALC-0438</c> їх перевіряє. Річний шаблон Land 1.0.19.0
/// не має колонки <c>Density20</c> — є місячні <c>Density20_Mnn</c> і псевдонім місяця <c>cfg.MonthInputAlias</c>; без псевдонімів
/// звірка відмовляла публікацію КОЖНОЇ нової версії методології на річному шаблоні (21 версія на копії live). І стеля 5 000 рядків
/// обрізала колонки дев'яти річних таблиць (590 колонок кожна) — частина справжніх колонок теж «зникала».
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати об'єднання з псевдонімами — червоніє перший тест; повернути <c>Take(MaxBindings)</c> (5 000) —
/// червоніє другий.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationBindingStoreArgumentNamesTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-571")]
    public async Task Псевдонім_місяця_річного_шаблону_є_іменем_аргументу_таблиці()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 2, ct: ct);
        await using (var db = builder.CreateContext())
        {
            var column = await db.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == doc.TableDefId).Select(c => c.Id).FirstAsync(ct);
            db.MonthInputAliases.Add(new MonthInputAlias(doc.TableDefId, "Density20", 1, column));
            await db.SaveChangesAsync(ct);
        }

        await using var read = builder.CreateContext();
        var names = await new CalculationBindingStore(read).ListColumnCodesAsync([doc.TableDefId], ct);

        Assert.Contains("Density20", names[doc.TableDefId]);
        Assert.Equal(3, names[doc.TableDefId].Count); // 2 колонки + 1 псевдонім (місяці одного псевдоніма — одне ім'я)
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-571")]
    public async Task Колонки_багатьох_річних_таблиць_не_обрізаються_стелею()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 5_010, ct: ct);

        await using var read = builder.CreateContext();
        var names = await new CalculationBindingStore(read).ListColumnCodesAsync([doc.TableDefId], ct);

        Assert.Equal(5_010, names[doc.TableDefId].Count);
    }
}
