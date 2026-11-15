using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ B131 (D-491): новий рядок книги Excel (<c>HandleWorkbookAsync</c>, одна задача на книгу) і поштучного PATCH
/// (<c>HandleAsync</c>) дає насіння перерахунку й у колонці з придатним дефолтом, яку батч не писав: дефолт входить в
/// агрегати, тож новий рядок їх змінює. Без дефолту — насіння лише записаних комірок. Справжні сховища й метадані.
/// </summary>
/// <remarks>
/// Мутації: у <c>PatchCellsHandler.WithRealRowIds</c> не передавати <c>NewRowIds</c> — обидва шляхи без насіння
/// дефолту, рядок «5» червоний; у <c>ColumnDefaultValues.SeedColumns</c> прибрати умову <c>Parse</c> — рядок «abc»
/// червоний.
/// </remarks>
public sealed partial class PatchCellsWorkbookTests
{
    /// <param name="defaultValue">Дефолт числової колонки C2 базової таблиці (<c>null</c> — без дефолту; пробіли й текст —
    /// непридатні, як у <c>ColumnDefaultValues</c>).</param>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-491")]
    [Trait("Finding", "B131")]
    [InlineData("5")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("abc")]
    public async Task B131_новий_рядок_книги_і_PATCH_сіє_колонку_з_дефолтом_яку_не_писали(string? defaultValue)
    {
        var bookWorld = await ArrangeAsync([1]);
        var singleWorld = await ArrangeAsync([1]);

        foreach (var (world, book) in new[] { (bookWorld, true), (singleWorld, false) })
        {
            var table = world.Tables[0];
            if (defaultValue is not null)
            {
                await ExecuteAsync($"UPDATE cfg.ColumnDef SET DefaultValue = N'{defaultValue}' WHERE Id = {table.ColumnIds[1]}");
            }

            List<PatchCellsRequest> requests =
            [
                new(table.InstanceId, PeriodKeyValue, "Import",
                    [new PatchRow(NewRowKey, null, [new PatchCell(table.ColumnCodes[^1], 7m)])]),
            ];

            var run = book
                ? await RunWorkbookAsync(world, Writer(world), requests)
                : await RunSequentialAsync(world, Writer(world), requests);

            long newRow = 0;
            await QueryAsync(
                $"SELECT Id FROM doc.TableRow WHERE PeriodKey = {PeriodKeyValue} AND TableInstanceId = {table.InstanceId} " +
                $"AND RowKey = N'{NewRowKey}'",
                reader => newRow = reader.GetInt64(0));
            Assert.NotEqual(0, newRow);

            var seeds = run.Seeds.Select(s => (s.RowId, s.ColumnDefId)).ToList();
            List<(long, int)> expected = defaultValue is not "5"
                ? [(newRow, table.ColumnIds[^1])]
                : [(newRow, table.ColumnIds[^1]), (newRow, table.ColumnIds[1])];
            Assert.Equal(expected.Order(), seeds.Order());
        }
    }
}
