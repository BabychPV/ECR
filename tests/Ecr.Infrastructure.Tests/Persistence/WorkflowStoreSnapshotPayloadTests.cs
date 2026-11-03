using System.Text;
using Ecr.Domain.Entities.Workflow;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// AN-135: <see cref="WorkflowStore.GetSnapshotPayloadAsync"/> читає <c>PayloadJson</c> сирим запитом із
/// <c>SequentialAccess</c> (замість EF <c>FirstOrDefaultAsync</c>, що на ~10 МБ коштував 11–12 с). Значення мусить
/// прийти байт-у-байт тим самим — у транзакції затвердження й поза нею; відсутній зріз — <c>null</c>.
/// </summary>
[Collection("SqlServer")]
public sealed class WorkflowStoreSnapshotPayloadTests(SqlServerFixture sql)
{
    /// <summary>Багатопакетне значення (~3 млн символів, кирилиця й сурогатна пара) — не влазить в один пакет TDS.</summary>
    private static string LargePayload()
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < 40_000; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"row\":").Append(i).Append(",\"column\":7,\"value\":\"значення ").Append(i).Append(" 😀 ")
              .Append('x', i % 23).Append("\"}");
        }

        return sb.Append(']').ToString();
    }

    private static async Task<long> AddAsync(TestDocumentBuilder chain, TestDocument d, string payload)
    {
        await using var db = chain.CreateContext();
        var snapshot = new SubmissionSnapshot(
            d.DocumentId, d.SheetDefId, d.PeriodKey.Value, d.TemplateVersionId,
            "[]", numericMode: null, calendarMode: null, payload, new byte[32], DateTime.UtcNow, 1);
        db.SubmissionSnapshots.Add(snapshot);
        await db.SaveChangesAsync(CancellationToken.None);
        return snapshot.Id;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Large_payload_is_read_verbatim_inside_the_approval_transaction()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await chain.BuildAsync();
        var payload = LargePayload();
        var id = await AddAsync(chain, doc, payload);

        await using var db = chain.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync(CancellationToken.None);
        var read = await new WorkflowStore(db).GetSnapshotPayloadAsync(id, CancellationToken.None);

        Assert.True(payload.Length > 1_000_000);
        Assert.Equal(payload, read);

        // Зʼєднання лишилося робочим для EF у тій самій транзакції.
        Assert.True(await db.SubmissionSnapshots.AnyAsync(s => s.Id == id, CancellationToken.None));
        await tx.CommitAsync(CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Payload_is_read_without_a_transaction_and_missing_snapshot_is_null()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await chain.BuildAsync();
        var id = await AddAsync(chain, doc, "{\"cells\":[],\"header\":{\"k\":{\"value\":\"ї\"}}}");

        await using var db = chain.CreateContext();
        var store = new WorkflowStore(db);

        Assert.Equal("{\"cells\":[],\"header\":{\"k\":{\"value\":\"ї\"}}}", await store.GetSnapshotPayloadAsync(id, CancellationToken.None));
        Assert.Null(await store.GetSnapshotPayloadAsync(long.MaxValue, CancellationToken.None));
        Assert.Equal(System.Data.ConnectionState.Closed, db.Database.GetDbConnection().State);
    }
}
