using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// Q-168 (аудит фази 2, продуктивність): попередній перегляд імпорту читає
/// рядки й комірки ОДНИМ пакетом на всю книгу, а не походом у базу на кожну
/// таблицю.
/// </summary>
/// <remarks>
/// ⛔ До фікса (PR A цього ж номера) <c>ImportDiffBuilder.BuildAsync</c> сам
/// ходив у базу тричі НА КОЖНУ таблицю (<c>GetRowIdsAsync</c>,
/// <c>GetRowVersionsAsync</c>, <c>ReadSliceAsync</c>) — 3×N запитів на книгу
/// з N таблицями, доведено попередньою версією цього тесту (9 запитів на
/// три таблиці). Тепер <c>ImportDiffBuilder</c> — чиста функція без бази
/// (<c>Build</c>, синхронний), а <c>ExcelImporter.PreviewAsync</c> читає
/// рядки й комірки ВСІХ таблиць трьома пакетними запитами ОДИН раз:
/// <c>IRowStore.GetRowIdsBatchAsync</c>, <c>IRowStore.GetRowVersionsBatchAsync</c>,
/// <c>ICellStore.ReadSlicesAsync</c>.
/// <para>
/// ⚠ Блоки книги тут навмисно БЕЗ рядків (<c>Rows: []</c>): подвійний цикл
/// по рядках/колонках усередині <c>Build</c> для виміру не потрібен — усі
/// походи в базу трапляються в пакетних запитах ДО нього.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class ImportDiffBuilderPerformanceTests(SqlServerFixture sql)
{
    private const int TableCount = 3;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перегляд_кількох_таблиць_коштує_сталих_трьох_запитів_незалежно_від_їх_кількості()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        using var workbook = new XLWorkbook();
        using var warmCache = new MemoryCache(new MemoryCacheOptions());

        var blocks = new List<(TableDef Table, ExcelTableBlock Block)>();
        var periodKey = new PeriodKey(202601);

        for (var i = 0; i < TableCount; i++)
        {
            var doc = await builder.BuildAsync(rowCount: 2, columnCount: 2, ct: CancellationToken.None);

            // ⚠ Знімок метаданих будується ПОЗА виміром: Q-168 — про читання
            // РЯДКІВ І КОМІРОК на перегляд, не про кеш структури шаблону (той
            // кешується окремо й на цей вимір не впливає).
            await using var warmDb = builder.CreateContext();
            var warmMetadata = new MetadataCache(warmCache, warmDb);
            var snapshot = await warmMetadata.GetAsync(doc.TemplateVersionId, CancellationToken.None);
            var table = snapshot.Sheets.SelectMany(s => s.Tables).Single(t => t.Id == doc.TableDefId);

            var sheetName = $"S{i}";
            workbook.Worksheets.Add(sheetName);

            blocks.Add((table, new ExcelTableBlock(
                doc.TableInstanceId, doc.TableDefId, $"T{i}", sheetName, HeaderRow: 1,
                Columns: [], Rows: [])));
        }

        var executed = new List<string>();
        var bulk = new BulkCellLoader(sql.ConnectionString, 1000);
        await using var countingDb = CreateCountingContext(executed);
        var cellStore = new NormalizedCellStore(countingDb, bulk);
        var rowStore = new RowStore(countingDb, bulk, new TestClock(DateTime.UtcNow));
        var diffBuilder = new ImportDiffBuilder();

        var tableInstanceIds = blocks.ConvertAll(b => b.Block.TableInstanceId);

        // ⛔ Рівно ТРИ пакетні запити на ВСЮ книгу — не на таблицю.
        var rowIdsBatch = await rowStore.GetRowIdsBatchAsync(tableInstanceIds, periodKey, CancellationToken.None);
        var versionsBatch = await rowStore.GetRowVersionsBatchAsync(tableInstanceIds, periodKey, CancellationToken.None);
        var slicesBatch = await cellStore.ReadSlicesAsync(tableInstanceIds, CancellationToken.None);

        var emptyDecisions = new Dictionary<CellAddress, EditDecision>();
        var emptyLookups = new Dictionary<int, IReadOnlyDictionary<string, long>>();
        var emptyRowIds = new Dictionary<string, long>();
        var emptyVersions = new Dictionary<string, string>();
        var emptySlice = Array.Empty<CellRecord>();

        foreach (var (table, block) in blocks)
        {
            // ⚠ Build — синхронний і без бази (Q-168): решта вимірюваних
            // запитів була б ЗАЙВОЮ, якби він досі ходив у базу сам.
            diffBuilder.Build(
                workbook.Worksheet(block.SheetName), block, periodKey.Value, table,
                emptyDecisions, emptyLookups,
                rowIdsBatch.GetValueOrDefault(block.TableInstanceId, emptyRowIds),
                versionsBatch.GetValueOrDefault(block.TableInstanceId, emptyVersions),
                slicesBatch.GetValueOrDefault(block.TableInstanceId, emptySlice));
        }

        // ⛔ Прямий доказ фікса: рівно три запити на ВСЮ книгу, незалежно від
        // TableCount. Мутаційний доказ — у PR: тимчасове повернення виклику
        // на таблицю замість пакетного валило цю саму перевірку.
        var queries = executed.Count(cmd => cmd.Contains("SELECT", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, queries);
    }

    /// <summary>Контекст, який складає кожну виконану команду в список.</summary>
    private EcrDbContext CreateCountingContext(List<string> executed)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .LogTo(executed.Add, [RelationalEventId.CommandExecuted])
            .Options);
}
