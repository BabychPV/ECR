using Ecr.Application.Errors;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Q-245: гонитва створення рядка тим самим <c>RowKey</c> має давати чистий
/// <c>409 ECR-ROW-0409</c>, не сирий <c>500</c>.
/// </summary>
/// <remarks>
/// ⛔ Той самий клас дефекту, що вже виправлений як Q-241 (TOCTOU без
/// атомарності): <c>CreateRowHandler</c>/<c>PatchCellsHandler.EnforceRowCreationRules</c>
/// перевіряють дублікат проти знімка, прочитаного на початку обробки
/// запиту — а не проти стану бази в момент запису. <c>UQ_TableRow_Key</c>
/// реально не пускає дублікат у ДАНІ, але без перехоплення в
/// <c>RowStore</c> другий із двох одночасних запитів на той самий ключ
/// падав необробленим <c>DbUpdateException</c> до самого
/// <c>ExceptionHandlingMiddleware</c> (там немає гілки ні на нього, ні на
/// <c>SqlException</c>) — і отримував голий <c>500</c>.
///
/// Тест не відтворює гонитву РЕАЛЬНИМ паралелізмом (два потоки, що
/// стартують одночасно, — джерело флакі-тестів): дефект живе не у вікні
/// перегонів, а в обробці помилки БАЗИ, тож досить детерміновано
/// відтворити сам конфлікт — вставити рядок із ключем один раз (успіх), а
/// тоді ще раз ТИМ САМИМ ключем (той самий шлях, яким пройшов би другий
/// із двох одночасних запитів, що програв гонитву за унікальним індексом).
/// </remarks>
[Collection("SqlServer")]
public sealed class RowStoreRaceTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дублікат_ключа_на_запису_дає_ECR_ROW_0409_а_не_сирий_виняток_бази()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(rowMode: TableRowMode.Dynamic, rowCount: 1, ct: CancellationToken.None);

        var clock = new FixedClock(new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc));
        var bulk = new BulkCellLoader(sql.ConnectionString, 1000);
        var rowKey = RowKey.Create("RACE-1");

        // Перший запис — така, як пройшов би переможець гонитви: успіх.
        await using (var db1 = CreateContext())
        {
            var winner = new RowStore(db1, bulk, clock);
            await winner.CreateRowAsync(doc.TableInstanceId, doc.PeriodKey, rowKey, ordinal: 1, CancellationToken.None);
        }

        // Другий запис — той самий ключ, ОКРЕМИЙ DbContext (як окремий HTTP-
        // запит): саме цей шлях перевіряв TOCTOU-перевірку в обробнику до
        // того, як дійти сюди, і саме тут SQL Server відмовляє за
        // UQ_TableRow_Key.
        await using var db2 = CreateContext();
        var loser = new RowStore(db2, bulk, clock);

        var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
            () => loser.CreateRowAsync(doc.TableInstanceId, doc.PeriodKey, rowKey, ordinal: 2, CancellationToken.None));

        Assert.Equal("ECR-ROW-0409", thrown.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дублікат_ключа_в_пакетному_записі_дає_ECR_ROW_0409()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(rowMode: TableRowMode.Dynamic, rowCount: 1, ct: CancellationToken.None);

        var clock = new FixedClock(new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc));
        var bulk = new BulkCellLoader(sql.ConnectionString, 1000);
        var rowKey = RowKey.Create("RACE-BATCH-1");

        await using (var db1 = CreateContext())
        {
            var winner = new RowStore(db1, bulk, clock);
            await winner.CreateRowAsync(doc.TableInstanceId, doc.PeriodKey, rowKey, ordinal: 1, CancellationToken.None);
        }

        await using var db2 = CreateContext();
        var loser = new RowStore(db2, bulk, clock);

        var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
            () => loser.CreateRowsAsync(
                doc.TableInstanceId, doc.PeriodKey, [rowKey, RowKey.Create("RACE-BATCH-2")], ordinal: 2, CancellationToken.None));

        Assert.Equal("ECR-ROW-0409", thrown.ErrorCode);
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private sealed class FixedClock(DateTime utcNow) : Domain.Abstractions.IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
