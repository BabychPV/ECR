// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewGeneratorTests.SheetCopies.cs
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// RS (копії аркушів, людина 03.11): звітні вʼюхи віддають оригінал і кожну копію ОКРЕМО — з власним станом подання,
/// номером у групі та назвою «&lt;Назва&gt; N»; кількість рядків вʼюхи дорівнює кількості рядків/комірок даних.
/// </summary>
/// <remarks>
/// ⛔ Мутація (локально, не комітиться): прибрати з приєднання <c>wf.ApprovalState</c> умову за <c>SheetCopyId</c> у
/// <c>rpt.v_DocumentCells</c> і в <c>usp_GenerateTemplateViews</c> — кожен рядок оригіналу й копії з'єднується з обома станами
/// (подвоєння), обидва тести червоні.
/// </remarks>
public sealed partial class ReportViewGeneratorTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Широка_вʼюха_віддає_оригінал_і_копію_окремими_рядками_з_власним_станом_і_назвою()
    {
        var (doc, view, _) = await PublishedDocumentAsync();
        await using (var db = sql.CreateContext())
        {
            await new ReportViewGenerator(db).GenerateAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        var copyId = await AddApprovedCopyAsync(doc);

        var rows = await QueryAsync<(long, byte, short, string)>(
            $"SELECT _RowId, _Status, _SheetCopyOrdinal, _SheetDisplayName FROM rpt.[{view}] WHERE _DocumentId = @doc "
            + "ORDER BY _SheetCopyOrdinal, _RowOrdinal",
            doc.DocumentId,
            r => (r.GetInt64(0), r.GetByte(1), r.GetInt16(2), r.GetString(3)));

        // Рядок = рядок таблиці: 2 оригінала + 2 копії; жодного подвоєння від двох станів документа.
        Assert.Equal(4, rows.Count);
        var name = await SheetNameAsync(doc);
        Assert.Equal(
            [((byte)1, (short)1, $"{name} 1"), ((byte)1, (short)1, $"{name} 1"), ((byte)2, (short)2, $"{name} 2"), ((byte)2, (short)2, $"{name} 2")],
            rows.Select(r => (r.Item2, r.Item3, r.Item4)));
        Assert.True(copyId > 0);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Довга_вʼюха_віддає_рівно_стільки_рядків_скільки_комірок_у_даних_оригіналу_і_копії()
    {
        var (doc, _, _) = await PublishedDocumentAsync();
        await AddApprovedCopyAsync(doc);

        var viewCount = (await QueryAsync<int>(
            "SELECT COUNT(*) FROM rpt.v_DocumentCells WHERE DocumentId = @doc", doc.DocumentId, r => r.GetInt32(0)))[0];
        var cellCount = (await QueryAsync<int>(
            $"""
            SELECT COUNT(*) FROM doc.CellValue AS cv
             WHERE cv.IsEmpty = 0 AND cv.PeriodKey = {doc.PeriodKey.Value}
               AND EXISTS (SELECT 1 FROM doc.TableRow AS r JOIN doc.TableInstance AS ti
                             ON ti.PeriodKey = r.PeriodKey AND ti.Id = r.TableInstanceId
                            WHERE r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId AND r.IsDeleted = 0 AND ti.DocumentId = @doc)
            """,
            doc.DocumentId, r => r.GetInt32(0)))[0];

        // 3 комірки оригінала + 2 комірки копії.
        Assert.Equal(5, cellCount);
        Assert.Equal(cellCount, viewCount);

        var byOrdinal = await QueryAsync<(short, byte, int)>(
            "SELECT SheetCopyOrdinal, Status, COUNT(*) FROM rpt.v_DocumentCells WHERE DocumentId = @doc "
            + "GROUP BY SheetCopyOrdinal, Status ORDER BY SheetCopyOrdinal",
            doc.DocumentId, r => (r.GetInt16(0), r.GetByte(1), r.GetInt32(2)));
        Assert.Equal([((short)1, (byte)1, 3), ((short)2, (byte)2, 2)], byOrdinal);
    }

    /// <summary>Копія аркуша з порожніми Fixed-рядками, двома комірками й власним станом <c>Approved</c> (оригінал — Submitted).</summary>
    private async Task<long> AddApprovedCopyAsync(TestDocument doc)
    {
        long copyId;
        await using (var db = sql.CreateContext())
        {
            var original = await db.DocumentSheets.SingleOrDefaultAsync(
                s => s.DocumentId == doc.DocumentId && s.CopyOfId == null);
            if (original is null)
            {
                original = new DocumentSheet(doc.DocumentId, doc.SheetDefId);
                db.DocumentSheets.Add(original);
                await db.SaveChangesAsync();
            }

            var copy = original.CreateCopy(2);
            db.DocumentSheets.Add(copy);
            await db.SaveChangesAsync();
            copyId = copy.Id;

            await new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now))
                .EnsureTableInstancesAsync(doc.DocumentId, doc.PeriodKey, CancellationToken.None);

            var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value, copyId);
            state.Submit(1, Now);
            state.Approve(1, Now);
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync();
        }

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"""
            DECLARE @inst bigint = (SELECT Id FROM doc.TableInstance
                                     WHERE PeriodKey = {doc.PeriodKey.Value} AND DocumentId = {doc.DocumentId} AND SheetCopyId = {copyId});
            DECLARE @r1 bigint = (SELECT TOP (1) Id FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value} AND TableInstanceId = @inst ORDER BY Ordinal);
            DECLARE @r2 bigint = (SELECT Id FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value} AND TableInstanceId = @inst ORDER BY Ordinal
                                  OFFSET 1 ROWS FETCH NEXT 1 ROWS ONLY);
            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric)
            VALUES ({doc.PeriodKey.Value}, @r1, {doc.ColumnDefIds[0]}, {doc.TableDefId}, N'copy', NULL),
                   ({doc.PeriodKey.Value}, @r2, {doc.ColumnDefIds[1]}, {doc.TableDefId}, NULL, 7);
            """);

        return copyId;
    }

    private async Task<string> SheetNameAsync(TestDocument doc)
    {
        await using var db = sql.CreateContext();
        var sheet = await db.SheetDefs.AsNoTracking().SingleAsync(s => s.Id == doc.SheetDefId);

        return sheet.NameL10n.Values.TryGetValue("en", out var en) && !string.IsNullOrEmpty(en) ? en : sheet.Code;
    }
}
