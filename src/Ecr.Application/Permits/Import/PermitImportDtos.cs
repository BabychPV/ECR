// src/Ecr.Application/Permits/Import/PermitImportDtos.cs
namespace Ecr.Application.Permits.Import;

/// <summary>
/// Saved column mapping of «Paste from Excel…» / «Import file…» for one permit table (<c>PRM_&lt;type&gt;_&lt;table&gt;</c>), shared by
/// every user: normalised file header → column code.
/// </summary>
/// <param name="RegistryCode">Table registry.</param>
/// <param name="Columns">Normalised header (lower case, letters and digits only) → column code; empty when nothing is saved.</param>
/// <param name="RowVersion">Token for <c>If-Match</c> of the next <c>PUT</c>; <c>null</c> — nothing saved yet.</param>
/// <param name="UpdatedAt">Last change (UTC); <c>null</c> — nothing saved yet.</param>
/// <param name="NaeColumns">Columns that reference the NAE tree (their sources give Region/Area).</param>
/// <param name="Derivable">Region/Area columns filled from the NAE source when the file leaves them empty.</param>
public sealed record PermitImportMappingDto(
    string RegistryCode,
    IReadOnlyDictionary<string, string> Columns,
    string? RowVersion,
    DateTime? UpdatedAt,
    IReadOnlyList<string> NaeColumns,
    IReadOnlyList<string> Derivable);

/// <summary>Body of <c>PUT …/import-mapping</c>; the token goes in <c>If-Match</c>.</summary>
/// <param name="Columns">File header → column code (the header is normalised by the server as well).</param>
public sealed record SavePermitImportMappingRequest(IReadOnlyDictionary<string, string>? Columns);

/// <summary>Distinct file values of the lookup columns to resolve to registry entries.</summary>
/// <param name="Columns">Per column: the distinct texts.</param>
/// <param name="Derive">Also derive Region/Area of every resolved NAE source.</param>
public sealed record PermitImportLookupRequest(IReadOnlyList<PermitImportLookupColumn>? Columns, bool Derive = true);

/// <summary>Values of one column.</summary>
/// <param name="Column">Column code.</param>
/// <param name="Values">Distinct texts as they are in the file.</param>
public sealed record PermitImportLookupColumn(string Column, IReadOnlyList<string>? Values);

/// <summary>Resolution of the lookup values, and Region/Area derived from the NAE sources.</summary>
/// <param name="Columns">Resolved columns (a column that is not a lookup to a shared registry or a type list is left out).</param>
/// <param name="Derived">Per resolved NAE source: Region/Area entries of the derivable columns.</param>
public sealed record PermitImportLookupResultDto(
    IReadOnlyList<PermitImportLookupColumnResultDto> Columns,
    IReadOnlyList<PermitImportDerivedDto> Derived);

/// <summary>Resolution of one column.</summary>
/// <param name="Column">Column code.</param>
/// <param name="RegistryCode">Registry the column points at.</param>
/// <param name="Values">One result per requested text.</param>
public sealed record PermitImportLookupColumnResultDto(string Column, string RegistryCode, IReadOnlyList<PermitImportLookupValueDto> Values);

/// <summary>One text: the entry it names, or why none.</summary>
/// <param name="Text">The text as sent.</param>
/// <param name="EntryId">Entry; <c>null</c> — not resolved.</param>
/// <param name="Label">Entry name in the user's language (code when it has none).</param>
/// <param name="Reason"><c>unknown</c>, <c>ambiguous</c> (2+ entries) or <c>notSource</c> (a NAE node that is not a source); <c>null</c> when resolved.</param>
/// <param name="Matches">How many entries the text names.</param>
public sealed record PermitImportLookupValueDto(string Text, long? EntryId, string? Label, string? Reason, int Matches);

/// <summary>Region/Area of one NAE source.</summary>
/// <param name="NaeEntryId">NAE source entry.</param>
/// <param name="Cells">One per derivable column that has a scope in the tree.</param>
public sealed record PermitImportDerivedDto(long NaeEntryId, IReadOnlyList<PermitImportDerivedCellDto> Cells);

/// <summary>A derived cell.</summary>
/// <param name="Column">Column code.</param>
/// <param name="EntryId">Entry; <c>null</c> — not derived.</param>
/// <param name="Label">Entry name in the user's language.</param>
/// <param name="Reason"><c>noScope</c> (the source has no region/area in the tree), <c>noMatch</c> or <c>ambiguous</c>; <c>null</c> when derived.</param>
/// <param name="Scope">NAE tree region/area code the source sits in.</param>
public sealed record PermitImportDerivedCellDto(string Column, long? EntryId, string? Label, string? Reason, string? Scope);

/// <summary>One sheet of an <c>.xlsx</c> as text: the first row is the header (if the file has one).</summary>
/// <param name="Sheet">Sheet read.</param>
/// <param name="Sheets">Visible sheets of the workbook.</param>
/// <param name="Rows">Rows as text (numbers invariant, dates <c>yyyy-MM-dd</c>, logical <c>true</c>/<c>false</c>).</param>
/// <param name="Truncated">The sheet has more rows than were read (the paste limit).</param>
public sealed record PermitImportSheetDto(string Sheet, IReadOnlyList<string> Sheets, IReadOnlyList<IReadOnlyList<string>> Rows, bool Truncated);

/// <summary>Reasons of an unresolved value.</summary>
public static class PermitImportReasons
{
    /// <summary>No entry has this code or name.</summary>
    public const string Unknown = "unknown";

    /// <summary>Two or more entries have this name.</summary>
    public const string Ambiguous = "ambiguous";

    /// <summary>The text names a NAE node that is not a source (region, area, site, equipment).</summary>
    public const string NotSource = "notSource";

    /// <summary>The NAE source has no region/area in the tree.</summary>
    public const string NoScope = "noScope";

    /// <summary>No Region/Area entry maps to the source's region/area.</summary>
    public const string NoMatch = "noMatch";
}
