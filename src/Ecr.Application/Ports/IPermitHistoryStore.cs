using Ecr.Domain.Entities.Documents;

namespace Ecr.Application.Ports;

/// <summary>A stored later permit with who added it (for the history read).</summary>
/// <param name="Segment">The segment.</param>
/// <param name="AddedBy">Display name of the author; <c>null</c> — unknown.</param>
public sealed record PermitSegmentRecord(PermitSegment Segment, string? AddedBy);

/// <summary>
/// Feature (j): permit history of rows and header fields (<c>doc.PermitSegment</c>) and the month parts
/// (<c>doc.CellPermitPart</c>).
/// </summary>
public interface IPermitHistoryStore
{
    /// <summary>All segments of the document (header and rows), not tracked, with the author's name.</summary>
    public Task<IReadOnlyList<PermitSegmentRecord>> ListAsync(long documentId, CancellationToken ct);

    /// <summary>Segments of one row's permit cell, TRACKED, in date order.</summary>
    public Task<IReadOnlyList<PermitSegment>> RowSegmentsAsync(
        long documentId, int tableDefId, long? sheetCopyId, string rowKey, int columnDefId, CancellationToken ct);

    /// <summary>Segments of one header field, TRACKED, in date order.</summary>
    public Task<IReadOnlyList<PermitSegment>> HeaderSegmentsAsync(long documentId, int headerFieldDefId, CancellationToken ct);

    /// <summary>D-1: сегменти рядка під UPDLOCK+HOLDLOCK до кінця транзакції (викликати лише в транзакції).</summary>
    public Task<IReadOnlyList<PermitSegment>> LockRowSegmentsAsync(
        long documentId, int tableDefId, long? sheetCopyId, string rowKey, int columnDefId, CancellationToken ct);

    /// <summary>D-1: сегменти поля шапки під UPDLOCK+HOLDLOCK до кінця транзакції.</summary>
    public Task<IReadOnlyList<PermitSegment>> LockHeaderSegmentsAsync(long documentId, int headerFieldDefId, CancellationToken ct);

    /// <summary>Parts of the document's months, not tracked (months of <paramref name="periodKeys"/>).</summary>
    public Task<IReadOnlyList<CellPermitPart>> PartsAsync(long documentId, IReadOnlyCollection<int> periodKeys, CancellationToken ct);

    /// <summary>Whether any part of the row refers to <paramref name="registryEntryId"/> (an entered split depends on it).</summary>
    public Task<bool> RowHasPartsOfAsync(
        long documentId, int tableDefId, long? sheetCopyId, string rowKey, long registryEntryId, CancellationToken ct);

    /// <summary>Adds a segment (saved by the unit of work).</summary>
    public void Add(PermitSegment segment);

    /// <summary>Removes a tracked segment (saved by the unit of work).</summary>
    public void Remove(PermitSegment segment);

    /// <summary>Parts of ONE month cell, TRACKED, by part number.</summary>
    public Task<IReadOnlyList<CellPermitPart>> CellPartsAsync(
        int periodKey, long documentId, int tableDefId, long? sheetCopyId, string rowKey, int columnDefId, CancellationToken ct);

    /// <summary>Adds a part (saved by the unit of work).</summary>
    public void AddPart(CellPermitPart part);

    /// <summary>Removes a tracked part (saved by the unit of work).</summary>
    public void RemovePart(CellPermitPart part);
}
