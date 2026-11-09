// src/Ecr.Application/Permits/History/PermitHistoryContracts.cs
namespace Ecr.Application.Permits.History;

/// <summary>j1: add a permit to a row's permit cell, in force from <see cref="ValidFrom"/>.</summary>
/// <param name="TableInstanceId">A table instance of the document (any period of the row).</param>
/// <param name="RowKey">Row key.</param>
/// <param name="ColumnCode">The permit column.</param>
/// <param name="RegistryEntryId">The new permit.</param>
/// <param name="ValidFrom">First day the new permit is in force for the row.</param>
public sealed record AddRowPermitRequest(long TableInstanceId, string RowKey, string ColumnCode, long RegistryEntryId, DateOnly ValidFrom);

/// <summary>Undo the latest permit added to a row's permit cell.</summary>
/// <param name="TableInstanceId">A table instance of the document.</param>
/// <param name="RowKey">Row key.</param>
/// <param name="ColumnCode">The permit column.</param>
public sealed record UndoRowPermitRequest(long TableInstanceId, string RowKey, string ColumnCode);

/// <summary>jh1: add a permit to a header field, in force from <see cref="ValidFrom"/>.</summary>
/// <param name="FieldCode">Header field code.</param>
/// <param name="RegistryEntryId">The new permit.</param>
/// <param name="ValidFrom">First day the new permit is in force for the document.</param>
/// <param name="PeriodKey">The period the user is in (<c>YYYYMM</c>) — the reporting window and the rows to name.</param>
public sealed record AddHeaderPermitRequest(string FieldCode, long RegistryEntryId, DateOnly ValidFrom, int PeriodKey);

/// <summary>Undo the latest permit added to a header field.</summary>
/// <param name="FieldCode">Header field code.</param>
public sealed record UndoHeaderPermitRequest(string FieldCode);

/// <summary>One permit of a history and the days it is in force.</summary>
/// <param name="SegmentId">The stored segment; <c>null</c> — the base permit (the cell/header value).</param>
/// <param name="RegistryEntryId">The permit.</param>
/// <param name="Permit">Permit number as people know it.</param>
/// <param name="From">First day in force; <c>null</c> — the base permit.</param>
/// <param name="Until">Last day in force; <c>null</c> — still in force.</param>
/// <param name="AddedBy">Who added it; <c>null</c> for the base permit.</param>
/// <param name="AddedAt">When (UTC); <c>null</c> for the base permit.</param>
public sealed record PermitHistoryItemDto(
    int? SegmentId, long RegistryEntryId, string Permit, DateOnly? From, DateOnly? Until, string? AddedBy, DateTime? AddedAt);

/// <summary>One part of a month split between permits.</summary>
/// <param name="PartNo">1 = A, 2 = B, …</param>
/// <param name="RegistryEntryId">The permit of the part.</param>
/// <param name="Permit">Permit number.</param>
/// <param name="DayFrom">First day of the part.</param>
/// <param name="DayTo">Last day of the part.</param>
public sealed record PermitPartSpanDto(int PartNo, long RegistryEntryId, string Permit, DateOnly DayFrom, DateOnly DayTo);

/// <summary>A month of a row that must be split between permits (j2).</summary>
/// <param name="Month">First day of the month.</param>
/// <param name="ColumnCodes">Cells of the row with entered data in that month that are not split yet.</param>
/// <param name="Parts">The parts the month splits into.</param>
/// <param name="Cells">The month cells of the row: current total and the entered parts (pre-fill of the split dialog).</param>
public sealed record SplitMonthDto(
    DateOnly Month, IReadOnlyList<string> ColumnCodes, IReadOnlyList<PermitPartSpanDto> Parts, IReadOnlyList<CellSplitValueDto> Cells);

/// <summary>One month cell of a shared month.</summary>
/// <param name="ColumnCode">The month column.</param>
/// <param name="Total">The stored value (the sum once split; the single «unsplit» value before).</param>
/// <param name="Parts">Entered part values by part number (A, B, …); <c>null</c> — not entered.</param>
/// <param name="IsSplit">Every part holds a value (the cell no longer blocks Submit).</param>
public sealed record CellSplitValueDto(string ColumnCode, decimal? Total, IReadOnlyList<decimal?> Parts, bool IsSplit);

/// <summary>j2/jh3: save the parts of month cells (partial save allowed — a cell whose parts are all entered gets the total).</summary>
/// <param name="PeriodKey">The period the user is in (<c>YYYYMM</c>).</param>
/// <param name="Items">Cells to split.</param>
public sealed record SaveCellSplitsRequest(int PeriodKey, IReadOnlyList<CellSplitItem> Items);

/// <summary>One month cell and its parts typed by the user (never divided by days).</summary>
/// <param name="TableInstanceId">A table instance of the document.</param>
/// <param name="RowKey">Row key.</param>
/// <param name="ColumnCode">The month column (or a numeric column of a table without month columns).</param>
/// <param name="BaseVersion">Row version the user started from (as in <c>PATCH …/cells</c>).</param>
/// <param name="Parts">Part values A, B, … in date order; <c>null</c> — not entered yet.</param>
public sealed record CellSplitItem(
    long TableInstanceId, string RowKey, string ColumnCode, string BaseVersion, IReadOnlyList<decimal?> Parts);

/// <summary>Permit history of one row's permit cell.</summary>
/// <param name="TableDefId">Table.</param>
/// <param name="TableCode">Table code.</param>
/// <param name="SheetCopyId">Sheet copy; <c>null</c> — the original.</param>
/// <param name="RowKey">Row key.</param>
/// <param name="ColumnCode">The permit column.</param>
/// <param name="Permits">The history, base permit first.</param>
/// <param name="SplitMonths">Months that still need a split.</param>
public sealed record RowPermitHistoryDto(
    int TableDefId, string TableCode, long? SheetCopyId, string RowKey, string ColumnCode,
    IReadOnlyList<PermitHistoryItemDto> Permits, IReadOnlyList<SplitMonthDto> SplitMonths);

/// <summary>Permit history of one header field.</summary>
/// <param name="FieldCode">Header field code.</param>
/// <param name="Permits">The history, base permit first.</param>
public sealed record HeaderPermitHistoryDto(string FieldCode, IReadOnlyList<PermitHistoryItemDto> Permits);

/// <summary>Permit history of a document and how many cells still need a split.</summary>
/// <param name="Header">Header fields with history.</param>
/// <param name="Rows">Rows with history (only those the reader can see).</param>
/// <param name="CellsNeedingSplit">Cells with entered data in a month shared by two permits and not split yet.</param>
public sealed record DocumentPermitHistoryDto(
    IReadOnlyList<HeaderPermitHistoryDto> Header, IReadOnlyList<RowPermitHistoryDto> Rows, int CellsNeedingSplit);
