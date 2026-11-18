// tests/Ecr.Api.Tests/ApprovalRouteRestartNamesQueryCountTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ B155-names: назви в переліку «подані аркуші почнуть погодження з першого кроку» читаються ПАКЕТОМ — число
/// звернень до бази не залежить від довжини переліку (≤ 200), і назви правильні: проєкт, документ, аркуш і копія
/// «&lt;Назва&gt; N».
/// </summary>
/// <remarks>
/// ⛔ Замір, а не таймінг (як <see cref="AdminListQueryCountTests"/>): <c>DbCommandCounter</c> на контексті сховища.
/// Порівнюються ДВА розміри — 1 аркуш і 3 аркуші з двох документів (з копією), — і поруч звіряються самі назви: «запитів
/// мало» було б зеленим і для коду, що нічого не читає.
/// ⛔ МУТАЦІЯ (перевірена перезбіркою): читати документи в <c>WorkflowStore.ListInFlightSheetNamesAsync</c> циклом по
/// документу — числа 3 і 4 замість 3 і 3, тест червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class ApprovalRouteRestartNamesQueryCountTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "B155")]
    public async Task Назви_переліку_перезапуску_читаються_фіксованим_числом_запитів()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var a = await builder.BuildAsync(periodKey: 209903, columnCount: 1, rowCount: 1).ConfigureAwait(true);
        var b = await builder.BuildAsync(periodKey: 209903, columnCount: 1, rowCount: 1).ConfigureAwait(true);

        // A: оригінал аркуша з копією (2) і назвою документа; B: оригінал без копій, документ без назви.
        long copyId;
        await using (var db = Context(counter: null))
        {
            var original = new DocumentSheet(a.DocumentId, a.SheetDefId);
            db.DocumentSheets.Add(original);
            db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));
            var documentA = await db.Documents.SingleAsync(d => d.Id == a.DocumentId).ConfigureAwait(true);
            documentA.SetName(new LocalizedText(new Dictionary<string, string> { ["en"] = "Names doc A" }));
            await db.SaveChangesAsync().ConfigureAwait(true);

            var copy = original.CreateCopy(2);
            db.DocumentSheets.Add(copy);
            await db.SaveChangesAsync().ConfigureAwait(true);
            copyId = copy.Id;
        }

        var sheetB = Sheet(1, b, sheetCopyId: null);
        InFlightSheet[] three = [Sheet(2, a, sheetCopyId: null), Sheet(3, a, copyId), sheetB];

        var one = await MeasureAsync([sheetB]).ConfigureAwait(true);
        var many = await MeasureAsync(three).ConfigureAwait(true);

        Assert.True(
            one.Queries == many.Queries,
            $"1 аркуш дав {one.Queries}, 3 аркуші — {many.Queries}.\n1:\n{one.Detail}\n3:\n{many.Detail}");
        Assert.True(many.Queries <= 3, $"очікувалось ≤ 3 запитів, було {many.Queries}:\n{many.Detail}");

        // Назви — ті, що в базі: без цього рівність вище була б зеленою і для порожнього читання.
        Assert.Single(one.Names);
        Assert.Equal(3, many.Names.Count);

        await using var check = Context(counter: null);
        var projectA = await check.Projects.Where(p => p.Id == a.ProjectId).Select(p => p.Code).SingleAsync().ConfigureAwait(true);
        var docB = await check.Documents.Where(d => d.Id == b.DocumentId).Select(d => d.BusinessKey).SingleAsync().ConfigureAwait(true);

        var originalA = many.Names[2];
        var copyA = many.Names[3];
        var onlyB = many.Names[1];

        Assert.Equal(projectA, originalA.ProjectCode);
        Assert.StartsWith("Project ", originalA.ProjectName.Values["en"], StringComparison.Ordinal);
        Assert.Equal("Names doc A", originalA.DocumentName?.Values["en"]);
        Assert.Equal(a.SheetCode, originalA.SheetCode);
        var sheetNameA = originalA.SheetName.Values["en"];
        Assert.Equal((short)1, originalA.CopyOrdinal);
        Assert.Equal($"{sheetNameA} 1", originalA.SheetDisplayName.Values["en"]);
        Assert.Equal((short)2, copyA.CopyOrdinal);
        Assert.Equal($"{sheetNameA} 2", copyA.SheetDisplayName.Values["en"]);

        Assert.Equal(docB, onlyB.DocumentCode);
        Assert.Null(onlyB.DocumentName);
        Assert.Equal(b.SheetCode, onlyB.SheetCode);
        Assert.Equal((short)1, onlyB.CopyOrdinal);
        Assert.Equal(onlyB.SheetName.Values["en"], onlyB.SheetDisplayName.Values["en"]);
    }

    private static InFlightSheet Sheet(long stateId, TestDocument doc, long? sheetCopyId)
        => new(stateId, doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value, sheetCopyId, doc.ProjectId, doc.TemplateId,
            doc.TemplateVersionId, CurrentStepId: null);

    private async Task<(int Queries, IReadOnlyDictionary<long, InFlightSheetNames> Names, string Detail)> MeasureAsync(
        IReadOnlyCollection<InFlightSheet> sheets)
    {
        var counter = new DbCommandCounter();
        await using var db = Context(counter);
        var store = new WorkflowStore(db);

        // Прогрів: перший запит на з'єднанні несе службові команди EF.
        await store.ListInFlightSheetNamesAsync(sheets, CancellationToken.None).ConfigureAwait(true);

        counter.Tally.Reset();
        var names = await store.ListInFlightSheetNamesAsync(sheets, CancellationToken.None).ConfigureAwait(true);
        var seen = counter.Tally.Snapshot();

        return (seen.Total, names, seen.Format());
    }

    private EcrDbContext Context(DbCommandCounter? counter)
    {
        var options = new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString);
        if (counter is not null)
        {
            options.AddInterceptors(counter);
        }

        return new EcrDbContext(options.Options);
    }
}
