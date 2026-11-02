using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Журнал рядків (<c>aud.RowChange</c>) на шляхах імпорту: новий рядок книги Excel
/// (<c>HandleWorkbookAsync</c>) і поштучного PATCH (<c>HandleAsync</c>) лягає в журнал рядків тією самою
/// транзакцією, з походженням батчу (<c>Import</c>).
/// </summary>
/// <remarks>
/// ⛔ Мутація: приберіть виклик <c>WriteRowAuditAsync</c> у <c>PersistWorkbookAsync</c> (книга) або в
/// <c>MaterializeNewRowsAsync</c> (поштучний) — відповідний світ дає порожній журнал і тест червоніє.
/// </remarks>
public sealed partial class PatchCellsWorkbookTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.21")]
    public async Task Новий_рядок_книги_і_поштучного_імпорту_лягає_в_журнал_рядків_як_Import()
    {
        var bookWorld = await ArrangeAsync([1], mixedExtras: true);
        var singleWorld = await ArrangeAsync([1], mixedExtras: true);

        await RunWorkbookAsync(
            bookWorld, Writer(bookWorld), Requests(bookWorld, await VersionsAsync(bookWorld), newRowsEverywhere: true));
        await RunSequentialAsync(
            singleWorld, Writer(singleWorld), Requests(singleWorld, await VersionsAsync(singleWorld), newRowsEverywhere: true));

        foreach (var world in new[] { bookWorld, singleWorld })
        {
            // Один запис на новий рядок кожної таблиці: дія 1 (Added), походження батчу, автор, справжній Id рядка.
            Assert.Equal(
                world.Tables.Select((_, i) => $"{i}|{NewRowKey}|1|Import|1|row-ok").Order(StringComparer.Ordinal),
                await RowJournalAsync(world));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.21")]
    public async Task Відхилена_книга_не_лишає_в_журналі_рядків_нічого()
    {
        // Стеля 3 рядки в базовій таблиці (уже 3): книга з новим рядком відхиляється ВСЯ — і рядки інших таблиць
        // теж не створені, тож у журналі рядків документа порожньо.
        var world = await ArrangeAsync([1], maxDynamicRows: 3, mixedExtras: true);
        var requests = Requests(world, await VersionsAsync(world), newRowsEverywhere: true);

        await Assert.ThrowsAnyAsync<Exception>(() => RunWorkbookAsync(world, Writer(world), requests));

        Assert.Empty(await RowJournalAsync(world));
    }

    /// <summary>Журнал рядків документа в індексах світу: таблиця|ключ|дія|походження|автор|рядок існує.</summary>
    private async Task<List<string>> RowJournalAsync(World world)
    {
        var lines = new List<string>();
        await QueryAsync(
            $"""
            SELECT rc.TableInstanceId, rc.RowKey, rc.Action, rc.Origin, rc.ChangedByUserId,
                   CASE WHEN EXISTS (SELECT 1 FROM doc.TableRow AS r
                                      WHERE r.PeriodKey = rc.PeriodKey AND r.Id = rc.TableRowId
                                        AND r.TableInstanceId = rc.TableInstanceId AND r.RowKey = rc.RowKey)
                        THEN N'row-ok' ELSE N'row-missing' END
              FROM aud.RowChange AS rc
             WHERE rc.DocumentId = {world.Doc.DocumentId} AND rc.PeriodKey = {PeriodKeyValue}
            """,
            reader => lines.Add(
                $"{IndexOf(world, reader.GetInt64(0))}|{reader.GetString(1)}|{reader.GetByte(2)}|{reader.GetString(3)}|"
                + $"{reader.GetInt32(4)}|{reader.GetString(5)}"));

        return [.. lines.Order(StringComparer.Ordinal)];
    }
}
