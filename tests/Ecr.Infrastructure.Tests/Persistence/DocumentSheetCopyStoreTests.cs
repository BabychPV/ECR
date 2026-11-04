// tests/Ecr.Infrastructure.Tests/Persistence/DocumentSheetCopyStoreTests.cs
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// RS (копії аркушів, рішення людини 03.11): склад документа — додати копію, перейменувати групу, видалити копію з
/// даними всіх періодів і перенумерувати решту.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні докази (локально, не комітяться): у <c>DocumentSheetCopyStore.DeleteCopyAsync</c> пропустити перенумерацію
/// (цикл <c>rest</c>) — червоніє <see cref="Видалення_копії_2_із_3_прибирає_її_дані_у_всіх_періодах_і_перенумеровує_решту"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentSheetCopyStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 20, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Копії_нумеруються_2_і_3_а_назва_групи_лежить_на_оригіналі()
    {
        var (builder, chain) = await ArrangeAsync();
        await using var db = builder.CreateContext();
        var store = new DocumentSheetCopyStore(db);

        Assert.Equal(0, (await store.FindGroupAsync(chain.DocumentId, chain.SheetDefId, default))!.CopyCount);

        var (first, firstOrdinal) = await store.AddCopyAsync(chain.DocumentId, chain.SheetDefId, default);
        var (second, secondOrdinal) = await store.AddCopyAsync(chain.DocumentId, chain.SheetDefId, default);
        await store.RenameGroupAsync(
            chain.DocumentId, chain.SheetDefId,
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Stacks", ["ru"] = "Штабели" }), default);

        Assert.Equal((short)2, firstOrdinal);
        Assert.Equal((short)3, secondOrdinal);
        Assert.NotEqual(first, second);

        db.ChangeTracker.Clear();
        var group = (await store.FindGroupAsync(chain.DocumentId, chain.SheetDefId, default))!;
        Assert.Equal(2, group.CopyCount);
        Assert.Equal("Штабели", group.NameOverride!.Get("ru"));

        var copy = (await store.FindAsync(chain.DocumentId, second, default))!;
        Assert.True(copy.IsCopy);
        Assert.Equal(group.OriginalId, copy.OriginalId);
        Assert.False((await store.FindAsync(chain.DocumentId, group.OriginalId, default))!.IsCopy);

        // Чужий документ копії не бачить.
        Assert.Null(await store.FindAsync(chain.DocumentId + 1, second, default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Видалення_копії_2_із_3_прибирає_її_дані_у_всіх_періодах_і_перенумеровує_решту()
    {
        var (builder, chain) = await ArrangeAsync();
        var past = new PeriodKey(202601);
        await builder.AddPeriodAsync(chain, past);

        long copy2, copy3;
        await using (var db = builder.CreateContext())
        {
            var store = new DocumentSheetCopyStore(db);
            copy2 = (await store.AddCopyAsync(chain.DocumentId, chain.SheetDefId, default)).SheetCopyId;
            copy3 = (await store.AddCopyAsync(chain.DocumentId, chain.SheetDefId, default)).SheetCopyId;

            var rowStore = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now));
            await rowStore.EnsureTableInstancesAsync(chain.DocumentId, chain.PeriodKey, default);
            await rowStore.EnsureTableInstancesAsync(chain.DocumentId, past, default);

            // Стан і журнал копії №2, зріз її подання (лишиться), стан копії №3 (лишиться).
            var state2 = new ApprovalState(chain.DocumentId, chain.SheetDefId, chain.PeriodKey.Value, copy2);
            db.ApprovalStates.Add(state2);
            db.ApprovalStates.Add(new ApprovalState(chain.DocumentId, chain.SheetDefId, chain.PeriodKey.Value, copy3));
            db.ApprovalEvents.Add(new ApprovalEvent(
                chain.DocumentId, chain.SheetDefId, chain.PeriodKey.Value, DocumentStatus.Draft, DocumentStatus.Submitted,
                ApprovalAction.Submit, null, Now, sheetCopyId: copy2));
            db.SubmissionSnapshots.Add(new SubmissionSnapshot(
                chain.DocumentId, chain.SheetDefId, chain.PeriodKey.Value, chain.TemplateVersionId, "[]", null, null,
                "{}", new byte[32], Now, 1, copy2));
            await db.SaveChangesAsync();
        }

        // Комірка в кожному періоді кожної копії.
        var cellsOf2 = await SeedCellsAsync(builder, chain, copy2, [chain.PeriodKey, past]);
        var cellsOf3 = await SeedCellsAsync(builder, chain, copy3, [chain.PeriodKey, past]);
        Assert.Equal(2, cellsOf2);
        Assert.Equal(2, cellsOf3);

        int removed;
        await using (var db = builder.CreateContext())
        {
            removed = await new DocumentSheetCopyStore(db).DeleteCopyAsync(chain.DocumentId, copy2, default);
        }

        Assert.Equal(2, removed);

        await using var check = builder.CreateContext();
        var sheets = await check.DocumentSheets.AsNoTracking()
            .Where(s => s.DocumentId == chain.DocumentId)
            .OrderBy(s => s.CopyOrdinal)
            .Select(s => new { s.Id, s.CopyOrdinal })
            .ToListAsync();

        // Копія №3 стала №2 (щільна нумерація), оригінал — 1; рядка копії №2 немає.
        Assert.Equal(2, sheets.Count);
        Assert.Equal((short)1, sheets[0].CopyOrdinal);
        Assert.Equal((copy3, (short)2), (sheets[1].Id, sheets[1].CopyOrdinal));

        foreach (var period in new[] { chain.PeriodKey.Value, past.Value })
        {
            Assert.False(await check.TableInstances.AnyAsync(
                i => i.DocumentId == chain.DocumentId && i.PeriodKeyValue == period && i.SheetCopyId == copy2));
            Assert.True(await check.TableInstances.AnyAsync(
                i => i.DocumentId == chain.DocumentId && i.PeriodKeyValue == period && i.SheetCopyId == copy3));
            Assert.True(await check.TableInstances.AnyAsync(
                i => i.DocumentId == chain.DocumentId && i.PeriodKeyValue == period && i.SheetCopyId == null));
        }

        // Комірка копії №3 у минулому періоді вціліла.
        Assert.True(await check.CellValues.AnyAsync(
            c => c.PeriodKeyValue == past.Value
                 && check.TableRows.Any(r => r.PeriodKeyValue == past.Value && r.Id == c.TableRowId
                                             && check.TableInstances.Any(i => i.PeriodKeyValue == past.Value
                                                                              && i.Id == r.TableInstanceId
                                                                              && i.SheetCopyId == copy3))));
        Assert.False(await check.ApprovalStates.AnyAsync(s => s.DocumentId == chain.DocumentId && s.SheetCopyId == copy2));
        Assert.True(await check.ApprovalStates.AnyAsync(s => s.DocumentId == chain.DocumentId && s.SheetCopyId == copy3));
        Assert.False(await check.ApprovalEvents.AnyAsync(e => e.DocumentId == chain.DocumentId && e.SheetCopyId == copy2));

        // Зріз подання (історія) переживає видалення копії.
        Assert.True(await check.SubmissionSnapshots.AnyAsync(s => s.DocumentId == chain.DocumentId && s.SheetCopyId == copy2));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Подана_або_затверджена_копія_у_будь_якому_періоді_блокує_видалення_а_чернетка_ні()
    {
        var (builder, chain) = await ArrangeAsync();
        long copy;
        await using (var db = builder.CreateContext())
        {
            copy = (await new DocumentSheetCopyStore(db).AddCopyAsync(chain.DocumentId, chain.SheetDefId, default)).SheetCopyId;
            var draft = new ApprovalState(chain.DocumentId, chain.SheetDefId, 202601, copy);
            var submitted = new ApprovalState(chain.DocumentId, chain.SheetDefId, 202602, copy);
            submitted.Submit(1, Now);
            db.ApprovalStates.AddRange(draft, submitted);
            await db.SaveChangesAsync();
        }

        await using var check = builder.CreateContext();
        var store = new DocumentSheetCopyStore(check);

        var block = await store.FindBlockingStateAsync(chain.DocumentId, copy, default);
        Assert.Equal((202602, DocumentStatus.Submitted), (block!.PeriodKey, block.Status));

        // Стан ІНШОЇ копії не рахується.
        Assert.Null(await store.FindBlockingStateAsync(chain.DocumentId, copy + 1_000_000, default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Видалення_документа_забирає_і_копії_аркуша_з_їхніми_даними()
    {
        var (builder, chain) = await ArrangeAsync();
        long copy;
        await using (var db = builder.CreateContext())
        {
            copy = (await new DocumentSheetCopyStore(db).AddCopyAsync(chain.DocumentId, chain.SheetDefId, default)).SheetCopyId;
            await new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now))
                .EnsureTableInstancesAsync(chain.DocumentId, chain.PeriodKey, default);
        }

        await SeedCellsAsync(builder, chain, copy, [chain.PeriodKey]);

        await using (var db = builder.CreateContext())
        {
            await new DocumentDeletionStore(db).DeleteAsync(chain.DocumentId, default);
        }

        await using var check = builder.CreateContext();
        Assert.False(await check.Documents.AnyAsync(d => d.Id == chain.DocumentId));
        Assert.False(await check.DocumentSheets.AnyAsync(s => s.DocumentId == chain.DocumentId));
        Assert.False(await check.TableInstances.AnyAsync(i => i.DocumentId == chain.DocumentId));
    }

    /// <summary>Документ із оригіналом аркуша у складі (як після створення документа).</summary>
    private async Task<(TestDocumentBuilder Builder, TestDocument Chain)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(periodKey: 202603, ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        if (!await db.DocumentSheets.AnyAsync(s => s.DocumentId == chain.DocumentId && s.CopyOfId == null))
        {
            db.DocumentSheets.Add(new DocumentSheet(chain.DocumentId, chain.SheetDefId));
            await db.SaveChangesAsync();
        }

        return (builder, chain);
    }

    /// <summary>По одній комірці (значення 3) у першому рядку екземпляра копії за кожен період; повертає скільки.</summary>
    private async Task<int> SeedCellsAsync(
        TestDocumentBuilder builder, TestDocument chain, long copyId, IReadOnlyList<PeriodKey> periods)
    {
        var seeded = 0;
        await using var db = builder.CreateContext();
        foreach (var period in periods)
        {
            var instance = await db.TableInstances.AsNoTracking().SingleAsync(
                i => i.DocumentId == chain.DocumentId && i.PeriodKeyValue == period.Value && i.SheetCopyId == copyId);
            var rowId = await db.TableRows.AsNoTracking()
                .Where(r => r.PeriodKeyValue == period.Value && r.TableInstanceId == instance.Id)
                .OrderBy(r => r.Ordinal)
                .Select(r => r.Id)
                .FirstAsync();

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty)
                VALUES ({period.Value}, {rowId}, {chain.ColumnDefIds[0]}, {chain.TableDefId}, 3, 0, 0);
                """);
            seeded++;
        }

        return seeded;
    }
}
