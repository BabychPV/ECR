using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>TableFillStore</c> — лічильники заповненості таблиць документа (`BE-10`).
/// </summary>
/// <remarks>
/// ⚠ Три різні твердження, і жодне з них не доводиться іншими:
/// <list type="number">
/// <item>запит засікається по <c>PeriodKey</c>, а не йде по всіх партиціях
/// (урок <c>WR-05</c>);</item>
/// <item>кількість звернень до БД не залежить від кількості таблиць —
/// інакше документ із 91 таблицею коштував би 91 походу;</item>
/// <item>«свідомо порожньо» рахується як відповідь людини, а обчислена
/// комірка — ні.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class TableFillStoreTests(SqlServerFixture sql)
{
    /// <summary>
    /// Лічильник не бачить комірок сусіднього періоду.
    /// </summary>
    /// <remarks>
    /// ⚠ ЩО САМЕ ДОВОДИТЬ ЦЕЙ ТЕСТ — те саме, що й
    /// <c>RowStorePartitionScopeTests</c> поруч, і тією самою ціною. Тестова
    /// база не партиційована (скрипти <c>01</c>/<c>02</c>/<c>07</c> у неї не
    /// подаються), тож звідси не видно ні <c>Seek</c>, ні логічних читань. Він
    /// доводить, що предикат на <c>PeriodKey</c> реально стоїть у запиті й
    /// реально звужує його: комірка з ТИМ САМИМ <c>TableRowId</c> у сусідньому
    /// періоді (складений первинний ключ це дозволяє) не має потрапити в
    /// підрахунок. Приберіть <c>c.PeriodKeyValue == key</c> — і вона
    /// потрапить, тобто тест упаде. Зв'язок із швидкодією непрямий, але
    /// однозначний: предикат, який відсікає чужу партицію в даних, — це той
    /// самий предикат, який відсікає її в плані.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-10")]
    public async Task Підрахунок_не_виходить_за_межі_свого_періоду()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        var first = await builder
            .BuildAsync(periodKey: 202601, columnCount: 1, rowCount: 1, ct: CancellationToken.None)
            .ConfigureAwait(true);
        var second = await builder
            .BuildAsync(periodKey: 202602, columnCount: 1, rowCount: 1, ct: CancellationToken.None)
            .ConfigureAwait(true);

        // ⚠ Той САМИЙ Id рядка в сусідньому періоді. У бойових даних Id видає
        // спільна SEQUENCE, тож такий збіг — рідкість; але первинний ключ
        // складений, база його дозволяє, і саме цей рядок відрізняє «запит
        // засікся по партиції» від «запит пройшов по всіх».
        var sharedId = first.RowIds[0];
        var now = new DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc);

        await using (var seed = builder.CreateContext())
        {
            seed.TableRows.Add(new TableRow(
                second.PeriodKey, sharedId, second.TableInstanceId,
                RowKey.Create("TWIN"), ordinal: 99, now));

            // Наша комірка — одна, у СВОЄМУ періоді.
            seed.CellValues.Add(new CellValue(
                new CellAddress(first.PeriodKey, sharedId, first.ColumnDefIds[0]),
                first.TableDefId,
                new CellValueData { ValueString = "своє" }));

            await seed.SaveChangesAsync(CancellationToken.None).ConfigureAwait(true);

            // Комірка-двійник: той самий рядок, сусідній період, чужа таблиця.
            seed.CellValues.Add(new CellValue(
                new CellAddress(second.PeriodKey, sharedId, second.ColumnDefIds[0]),
                second.TableDefId,
                new CellValueData { ValueString = "чуже" }));

            await seed.SaveChangesAsync(CancellationToken.None).ConfigureAwait(true);
        }

        await using var db = builder.CreateContext();
        var store = new TableFillStore(db);

        var counts = await store
            .GetFillCountsAsync(first.DocumentId, first.PeriodKey, [], CancellationToken.None)
            .ConfigureAwait(true);

        var table = Assert.Single(counts);

        Assert.Equal(first.TableDefId, table.TableDefId);
        Assert.Equal(1, table.RowCount);

        // Один, а не два: комірка сусіднього періоду до цього документа не
        // належить, хоч і лежить на рядку з тим самим Id.
        Assert.Equal(1, table.FilledCells);
    }

    /// <summary>
    /// Звернень до БД рівно два — скільки б таблиць не було в документі.
    /// </summary>
    /// <remarks>
    /// ⛔ Це храповик, а не вимір. Документ у макеті має 91 таблицю, бюджет
    /// відповіді — 150 мс, і запит на таблицю не вкладається в нього ще до
    /// першого рядка (той самий урок, що вже записаний над
    /// <c>ICellStore.ReadSlicesAsync</c>, <c>Q-165</c>). Число мусить лишатися
    /// сталим при зростанні кількості таблиць — саме це тут і перевіряється:
    /// таблиць у документі П'ЯТЬ, а звернень два. З підрахунком по таблиці
    /// їх було б десять, і тест упав би.
    ///
    /// ⚠ Лічильник бачить лише команди EF — і цього тут ДОСИТЬ, бо
    /// <c>TableFillStore</c> сирих команд не випускає взагалі (на відміну від
    /// <c>NormalizedCellStore</c>, через який застереження в
    /// <c>DbCommandCounter</c> і написане).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-10")]
    public async Task Звернень_до_бази_не_більшає_від_кількості_таблиць()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder
            .BuildAsync(columnCount: 2, rowCount: 1, ct: CancellationToken.None)
            .ConfigureAwait(true);

        var extra = await AddTablesAsync(builder, sql.ConnectionString, document, count: 4)
            .ConfigureAwait(true);

        var counter = new DbCommandCounter();

        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .AddInterceptors(counter)
            .Options);

        var store = new TableFillStore(db);

        counter.Tally.Reset();

        var counts = await store
            .GetFillCountsAsync(document.DocumentId, document.PeriodKey, [], CancellationToken.None)
            .ConfigureAwait(true);

        var seen = counter.Tally.Snapshot();

        // Передумова самого тесту названа явно: якби таблиця була одна,
        // «звернень два» нічого не доводило б.
        Assert.Equal(5, counts.Count);
        Assert.Equal(
            extra.Concat([document.TableDefId]).Order().ToList(),
            counts.Select(c => c.TableDefId).Order().ToList());

        Assert.True(seen.Total == 2, seen.Format());
    }

    /// <summary>
    /// Свідома порожнеча — відповідь; обчислена комірка — ні.
    /// </summary>
    /// <remarks>
    /// ⛔ Обидві половини в одному тесті навмисно. Якби рахувалися всі
    /// комірки підряд, вийшло б три; якби <c>IsEmpty</c> теж відкидався —
    /// одна. Правильна відповідь між ними, і жодна з двох підстановок сталої
    /// її не дає.
    ///
    /// ⚠ Чому <c>IsEmpty = 1</c> рахується заповненим: це третій стан
    /// <c>R-B4</c> — «людина подивилась і сказала: тут нічого». Не рахувати
    /// його означало б, що таблиця з однією свідомо порожньою коміркою
    /// НІКОЛИ не дійде до 100 %, і закрити її буде нічим.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-10")]
    public async Task Явна_порожнеча_рахується_а_обчислена_комірка_ні()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder
            .BuildAsync(columnCount: 3, rowCount: 1, ct: CancellationToken.None)
            .ConfigureAwait(true);

        var rowId = document.RowIds[0];

        await using (var seed = builder.CreateContext())
        {
            seed.CellValues.Add(new CellValue(
                new CellAddress(document.PeriodKey, rowId, document.ColumnDefIds[0]),
                document.TableDefId,
                new CellValueData { ValueString = "введено" }));

            seed.CellValues.Add(new CellValue(
                new CellAddress(document.PeriodKey, rowId, document.ColumnDefIds[1]),
                document.TableDefId,
                new CellValueData { IsEmpty = true }));

            seed.CellValues.Add(new CellValue(
                new CellAddress(document.PeriodKey, rowId, document.ColumnDefIds[2]),
                document.TableDefId,
                new CellValueData { ValueNumeric = 7m, IsCalculated = true }));

            await seed.SaveChangesAsync(CancellationToken.None).ConfigureAwait(true);
        }

        await using var db = builder.CreateContext();
        var store = new TableFillStore(db);

        var table = Assert.Single(await store
            .GetFillCountsAsync(document.DocumentId, document.PeriodKey, [], CancellationToken.None)
            .ConfigureAwait(true));

        Assert.Equal(2, table.FilledCells);
    }

    /// <summary>
    /// Повна семантика чисельника після перенесення фільтра обчислюваних
    /// колонок із SQL у пам'ять: рахується введене (текст, нуль, явна
    /// порожнеча), не рахується обчислене, комірка обчислюваної колонки й
    /// комірка видаленого рядка.
    /// </summary>
    /// <remarks>
    /// ⚠ Список обчислюваних колонок НЕПОРОЖНІЙ навмисно: решта тестів
    /// передають <c>[]</c>, і фільтр, який нічого не відсікає, вони не
    /// відрізнили б від відсутнього.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-10")]
    public async Task Чисельник_рахує_введене_і_відкидає_обчислене_й_видалене()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder
            .BuildAsync(columnCount: 4, rowCount: 2, ct: CancellationToken.None)
            .ConfigureAwait(true);

        var (live, deleted) = (document.RowIds[0], document.RowIds[1]);
        var cols = document.ColumnDefIds;
        var computedColumn = cols[3];

        await using (var seed = builder.CreateContext())
        {
            CellValue Cell(long row, int column, CellValueData data)
                => new(new CellAddress(document.PeriodKey, row, column), document.TableDefId, data);

            seed.CellValues.AddRange(
                Cell(live, cols[0], new CellValueData { ValueString = "текст" }),
                Cell(live, cols[1], new CellValueData { ValueNumeric = 0m }),
                Cell(live, cols[2], new CellValueData { IsEmpty = true }),
                Cell(live, computedColumn, new CellValueData { ValueNumeric = 5m }),
                Cell(deleted, cols[0], new CellValueData { ValueString = "у видаленому" }),
                Cell(deleted, cols[1], new CellValueData { ValueNumeric = 9m, IsCalculated = true }));

            await seed.SaveChangesAsync(CancellationToken.None).ConfigureAwait(true);

            var row = await seed.TableRows
                .SingleAsync(r => r.PeriodKeyValue == document.PeriodKey.Value && r.Id == deleted)
                .ConfigureAwait(true);
            row.SoftDelete(new DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc));
            await seed.SaveChangesAsync(CancellationToken.None).ConfigureAwait(true);
        }

        await using var db = builder.CreateContext();
        var table = Assert.Single(await new TableFillStore(db)
            .GetFillCountsAsync(document.DocumentId, document.PeriodKey, [computedColumn], CancellationToken.None)
            .ConfigureAwait(true));

        Assert.Equal(1, table.RowCount);
        Assert.Equal(3, table.FilledCells);
    }

    /// <summary>Ще кілька таблиць того самого документа й періоду.</summary>
    private static async Task<List<int>> AddTablesAsync(
        TestDocumentBuilder builder, string connectionString, TestDocument document, int count)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var loader = new BulkCellLoader(connectionString, 1000);
        var ids = new List<int>(count);

        await using var db = builder.CreateContext();

        for (var i = 0; i < count; i++)
        {
            var table = new TableDef(
                document.SheetDefId, EcrCode.Create($"T{i}_{tag}"), Name($"Table {i}"), i + 2,
                TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);

            db.TableDefs.Add(table);
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(true);

            db.ColumnDefs.Add(new ColumnDef(
                table.Id, EcrCode.Create($"C{i}_{tag}"), Name("Col"), 1, CellDataType.Decimal));

            var instanceId = await loader
                .ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None)
                .ConfigureAwait(true);
            var rowId = await loader
                .ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None)
                .ConfigureAwait(true);

            db.TableInstances.Add(new TableInstance(
                document.PeriodKey, instanceId, document.DocumentId, table.Id, now));
            db.TableRows.Add(new TableRow(
                document.PeriodKey, rowId, instanceId, RowKey.Create($"R1_{i}_{tag}"), 1, now));

            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(true);

            ids.Add(table.Id);
        }

        return ids;
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
