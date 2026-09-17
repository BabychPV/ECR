using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>RowStore.TouchRowsAsync</c> зобов'язаний нести <c>PeriodKey</c> —
/// ключ партиції <c>doc.TableRow</c>.
/// </summary>
/// <remarks>
/// ⛔ Дефект: фільтр був тільки за <c>Id</c>. Кластерний ключ таблиці —
/// <c>(PeriodKey, Id)</c>, таблиця лежить на <c>ps_ByPeriodKey</c>, і жодного
/// індексу з <c>Id</c> попереду немає: <c>07-partition-tables.sql</c> вирівнює
/// кожен індекс цих таблиць по схемі партиціонування й падає
/// (<c>THROW 50031</c>), якщо хоч один лишився поза нею. Тобто «дотик» рядка
/// всередині транзакції запису, під блокуваннями, на найгарячішому шляху
/// системи йшов по ВСІХ партиціях.
///
/// ⚠ ЩО САМЕ ДОВОДИТЬ ЦЕЙ ТЕСТ, а що ні. Він НЕ міряє план і не доводить
/// швидкодію: тестова база не партиційована (скрипти <c>01</c>/<c>02</c>/<c>07</c>
/// у неї не подаються), тож звідси не видно ні <c>Seek</c>, ні логічних
/// читань. Він доводить рівно те, що предикат на <c>PeriodKey</c> реально
/// стоїть у запиті й реально звужує його: два рядки з ОДНАКОВИМ <c>Id</c> у
/// різних періодах (складений первинний ключ це дозволяє) — і «дотик» з одним
/// періодом має зачепити рівно один із них. Приберіть <c>PeriodKey</c> з
/// <c>WHERE</c> — і другий рядок теж отримає новий <c>ModifiedAt</c>, тобто
/// тест упаде. Зв'язок із швидкодією тут непрямий, але однозначний: предикат,
/// який відсікає чужу партицію в даних, — це той самий предикат, який відсікає
/// її в плані.
/// </remarks>
[Collection("SqlServer")]
public sealed class RowStorePartitionScopeTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дотик_рядків_не_виходить_за_межі_свого_періоду()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        var first = await builder.BuildAsync(
            periodKey: 202601, rowMode: TableRowMode.Dynamic, rowCount: 1, ct: CancellationToken.None);
        var second = await builder.BuildAsync(
            periodKey: 202602, rowMode: TableRowMode.Dynamic, rowCount: 1, ct: CancellationToken.None);

        // ⚠ Той САМИЙ Id у сусідньому періоді. У бойових даних Id видає спільна
        // SEQUENCE, тож такий збіг — рідкість; але первинний ключ складений,
        // база його дозволяє, і саме цей рядок відрізняє «запит засікся по
        // партиції» від «запит пройшов по всіх».
        var sharedId = first.RowIds[0];
        var createdAt = new DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc);

        await using (var seed = builder.CreateContext())
        {
            seed.TableRows.Add(new TableRow(
                second.PeriodKey, sharedId, second.TableInstanceId,
                RowKey.Create("TWIN"), ordinal: 99, createdAt));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var touchedAt = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

        await using (var db = builder.CreateContext())
        {
            var store = new RowStore(
                db,
                new BulkCellLoader(sql.ConnectionString, 1000),
                new FixedClock(touchedAt));

            await store.TouchRowsAsync([sharedId], first.PeriodKey, touchedAt, CancellationToken.None);
        }

        await using var check = builder.CreateContext();

        var own = await check.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == first.PeriodKey.Value && r.Id == sharedId)
            .Select(r => r.ModifiedAt)
            .SingleAsync(CancellationToken.None);

        var foreign = await check.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == second.PeriodKey.Value && r.Id == sharedId)
            .Select(r => r.ModifiedAt)
            .SingleAsync(CancellationToken.None);

        Assert.Equal(touchedAt, own);
        Assert.Equal(createdAt, foreign);
    }

    private sealed class FixedClock(DateTime utcNow) : Domain.Abstractions.IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
