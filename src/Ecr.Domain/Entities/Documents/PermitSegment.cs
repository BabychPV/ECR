using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Feature (j), human 08.11 («Користувач має сам поділити значення для місяця на 2 частини»): a LATER permit of one row's
/// permit cell, or of one header field, in force «from date» (<c>doc.PermitSegment</c>). The base permit stays the
/// cell/header value; a segment ends the day before the next segment of the same target starts.
/// </summary>
/// <remarks>
/// ⚠ Target — exactly one: a header field (<see cref="HeaderFieldDefId"/>), or a row's permit column addressed LOGICALLY
/// (<see cref="TableDefId"/>, <see cref="SheetCopyId"/>, <see cref="RowKey"/>, <see cref="ColumnDefId"/>): the row key is
/// the same in every period instance, so the history does not depend on Q-17 month routing. Rows without history have
/// no segments — nothing changes for them.
/// </remarks>
public sealed class PermitSegment : Entity<int>
{
    /// <summary>Longest row key — as <c>doc.TableRow.RowKey</c>.</summary>
    public const int MaxRowKeyLength = 100;

    private PermitSegment() { }

    private PermitSegment(
        long documentId, int? headerFieldDefId, int? tableDefId, long? sheetCopyId, string? rowKey, int? columnDefId,
        DateOnly validFrom, long registryEntryId, int createdByUserId, DateTime createdAt)
    {
        DocumentId = documentId;
        HeaderFieldDefId = headerFieldDefId;
        TableDefId = tableDefId;
        SheetCopyId = sheetCopyId;
        RowKey = rowKey;
        ColumnDefId = columnDefId;
        ValidFrom = validFrom;
        RegistryEntryId = registryEntryId;
        CreatedByUserId = createdByUserId;
        CreatedAt = createdAt;
    }

    /// <summary>Document.</summary>
    public long DocumentId { get; private set; }

    /// <summary>Header field target; <c>null</c> — a row target.</summary>
    public int? HeaderFieldDefId { get; private set; }

    /// <summary>Row target: table.</summary>
    public int? TableDefId { get; private set; }

    /// <summary>Row target: sheet copy (RS); <c>null</c> — the original sheet.</summary>
    public long? SheetCopyId { get; private set; }

    /// <summary>Row target: row key.</summary>
    public string? RowKey { get; private set; }

    /// <summary>Row target: the permit column.</summary>
    public int? ColumnDefId { get; private set; }

    /// <summary>First day the permit is in force for the target.</summary>
    public DateOnly ValidFrom { get; private set; }

    /// <summary>The permit (registry entry).</summary>
    public long RegistryEntryId { get; private set; }

    /// <summary>Who added it.</summary>
    public int CreatedByUserId { get; private set; }

    /// <summary>When (UTC).</summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>A later permit of a row's permit cell.</summary>
    public static PermitSegment ForRow(
        long documentId, int tableDefId, long? sheetCopyId, string rowKey, int columnDefId,
        DateOnly validFrom, long registryEntryId, int userId, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rowKey);
        if (rowKey.Length > MaxRowKeyLength)
        {
            throw new ArgumentOutOfRangeException(nameof(rowKey), rowKey.Length, $"Row key is longer than {MaxRowKeyLength}.");
        }

        return new PermitSegment(
            documentId, null, tableDefId, sheetCopyId, rowKey, columnDefId, validFrom, registryEntryId, userId, utcNow);
    }

    /// <summary>A later permit of a header field.</summary>
    public static PermitSegment ForHeader(
        long documentId, int headerFieldDefId, DateOnly validFrom, long registryEntryId, int userId, DateTime utcNow)
        => new(documentId, headerFieldDefId, null, null, null, null, validFrom, registryEntryId, userId, utcNow);
}
