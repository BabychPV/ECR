using System.Security.Cryptography;
using System.Text;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Application.Workflow;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// PERF (затвердження документа з 91 таблицею, 268→493 с): <see cref="ApprovalSnapshotRefresh.RefreshAsync"/>
/// читає payload НАЙНОВІШОГО зрізу, а не всіх зрізів аркуша (кожне подання додає зріз ~10 МБ), і не читає
/// його взагалі, якщо хеш живих даних збігся з хешем найновішого зрізу.
/// </summary>
/// <remarks>
/// ⛔ Мутація (локально, не комітиться): повернути в <c>RefreshAsync</c> виклик
/// <c>workflow.GetSnapshotsAsync</c> — червоніють усі три тести (він тягне payload усіх зрізів).
/// </remarks>
public sealed class ApprovalSnapshotRefreshLatestOnlyTests
{
    private const long DocId = 7;
    private const int SheetId = 1;
    private const int TemplateVersion = 3;
    private static readonly PeriodKey Period = new(202610);

    // Живий payload без клітинок і без шапки — голий порожній масив (SubmissionPayload.Write).
    private static readonly string EmptyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("[]")));

    private readonly IWorkflowStore _workflow = Substitute.For<IWorkflowStore>();
    private readonly ISubmitRecalculation _recalculation = Substitute.For<ISubmitRecalculation>();

    private ApprovalSnapshotRefresh Sut()
    {
        var instances = Substitute.For<IRowStore>();
        instances.GetTableInstancesAsync(DocId, Period, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableInstanceRef>>([]));

        var documents = Substitute.For<IDocumentStore>();
        documents.GetTemplateVersionIdAsync(DocId, Arg.Any<CancellationToken>()).Returns(TemplateVersion);

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                TemplateVersion, 0, [],
                new Dictionary<int, ColumnDef>(),
                new Dictionary<(int TableDefId, string RowKey), RowDef>()));

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetValuesAsync(DocId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<int, DocumentHeaderValueData>>(
                new Dictionary<int, DocumentHeaderValueData>()));

        return new ApprovalSnapshotRefresh(
            Substitute.For<ISheetEditGate>(), Substitute.For<ICellStore>(), instances, documents, metadata,
            headers, _workflow, _recalculation);
    }

    [Fact]
    public async Task Hash_equal_to_latest_snapshot_reads_no_payload_at_all()
    {
        _workflow.GetLatestSnapshotKeyAsync(DocId, SheetId, Period, Arg.Any<CancellationToken>())
            .Returns((17L, EmptyHash));

        var taken = await Sut().RefreshAsync(DocId, SheetId, Period, userId: 5, DateTime.UtcNow, CancellationToken.None);

        Assert.False(taken);
        await _workflow.DidNotReceiveWithAnyArgs().GetSnapshotPayloadAsync(default, default);
        await _workflow.DidNotReceiveWithAnyArgs().GetSnapshotsAsync(default, default, default, default);
        await _workflow.DidNotReceiveWithAnyArgs().SaveSnapshotAsync(default!, default);
    }

    [Fact]
    public async Task Hash_differs_reads_only_the_latest_payload_and_still_compares_cells()
    {
        _workflow.GetLatestSnapshotKeyAsync(DocId, SheetId, Period, Arg.Any<CancellationToken>())
            .Returns((17L, "deadbeef"));
        _workflow.GetSnapshotPayloadAsync(17, Arg.Any<CancellationToken>()).Returns("[]");

        var taken = await Sut().RefreshAsync(DocId, SheetId, Period, userId: 5, DateTime.UtcNow, CancellationToken.None);

        Assert.False(taken); // комірки аркуша ті самі — нового зрізу немає
        await _workflow.Received(1).GetSnapshotPayloadAsync(17, Arg.Any<CancellationToken>());
        await _workflow.DidNotReceiveWithAnyArgs().GetSnapshotsAsync(default, default, default, default);
    }

    [Fact]
    public async Task No_snapshot_yet_takes_a_new_one_without_loading_any_payload()
    {
        _workflow.GetLatestSnapshotKeyAsync(DocId, SheetId, Period, Arg.Any<CancellationToken>())
            .Returns((ValueTuple<long, string>?)null);

        var taken = await Sut().RefreshAsync(DocId, SheetId, Period, userId: 5, DateTime.UtcNow, CancellationToken.None);

        Assert.True(taken);
        await _workflow.DidNotReceiveWithAnyArgs().GetSnapshotPayloadAsync(default, default);
        await _workflow.DidNotReceiveWithAnyArgs().GetSnapshotsAsync(default, default, default, default);
        await _workflow.Received(1).SaveSnapshotAsync(Arg.Any<SubmissionSnapshotRecord>(), Arg.Any<CancellationToken>());
    }
}
