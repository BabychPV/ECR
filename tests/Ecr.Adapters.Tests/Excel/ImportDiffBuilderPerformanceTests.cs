using ClosedXML.Excel;
using Ecr.Adapters.Excel;
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
/// Q-168 (аудит фази 2, продуктивність): <c>ImportDiffBuilder.BuildAsync</c>
/// коштує кількох походів у базу НА КОЖНУ таблицю книги — синхронно, поки
/// користувач стоїть над результатом попереднього перегляду імпорту.
/// </summary>
/// <remarks>
/// ⚠ «Тести спершу» (рішення людини): цей файл ставить бюджет запитів на
/// сьогоднішню поведінку — три походи в базу (<c>GetRowIdsAsync</c>,
/// <c>GetRowVersionsAsync</c>, <c>ReadSliceAsync</c>) НА КОЖНУ таблицю, лінійно
/// від їхньої кількості. Фікс (Q-168, PR B) пакетує ці три виклики через
/// <c>IRowStore.GetRowIdsBatchAsync</c>, аналогічний пакетний метод версій
/// рядків і <c>ICellStore.ReadSlicesAsync</c> — після нього той самий тест
/// затягується до сталого числа запитів, незалежного від кількості таблиць.
/// <para>
/// ⚠ Блоки книги тут навмисно БЕЗ рядків (<c>Rows: []</c>): подвійний цикл
/// по рядках/колонках усередині <c>BuildAsync</c> для виміру не потрібен —
/// три походи в базу трапляються ДО нього, незалежно від того, скільки в
/// таблиці рядків.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class ImportDiffBuilderPerformanceTests(SqlServerFixture sql)
{
    private const int TableCount = 3;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перегляд_кількох_таблиць_масштабується_лінійно_від_їх_кількості()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        using var workbook = new XLWorkbook();
        using var warmCache = new MemoryCache(new MemoryCacheOptions());

        var blocks = new List<(TableDef Table, ExcelTableBlock Block)>();

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
        var diffBuilder = new ImportDiffBuilder(cellStore, rowStore);

        var emptyDecisions = new Dictionary<CellAddress, EditDecision>();
        var emptyLookups = new Dictionary<int, IReadOnlyDictionary<string, long>>();

        foreach (var (table, block) in blocks)
        {
            await diffBuilder.BuildAsync(
                workbook.Worksheet(block.SheetName), block, periodKey: 202601,
                table, emptyDecisions, emptyLookups, CancellationToken.None);
        }

        // ⛔ Сьогоднішній бюджет: рівно три походи в базу НА ТАБЛИЦЮ
        // (GetRowIdsAsync, GetRowVersionsAsync, ReadSliceAsync) — прямий доказ
        // знахідки Q-168. Після пакетного фікса (PR B) це число перестає
        // рости з кількістю таблиць; тест тоді звузити до сталої стелі.
        var queries = executed.Count(cmd => cmd.Contains("SELECT", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3 * TableCount, queries);
    }

    /// <summary>Контекст, який складає кожну виконану команду в список.</summary>
    private EcrDbContext CreateCountingContext(List<string> executed)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .LogTo(executed.Add, [RelationalEventId.CommandExecuted])
            .Options);
}
