namespace Ecr.Application.RegistryImport;

/// <summary>Message keys of the registry import engine (catalog rows live in <c>09-seed.sql</c>, section NAE:n4-import).</summary>
public static class RegistryImportKeys
{
    /// <summary>The profile (spec) is not valid.</summary>
    public const string SpecInvalid = "err.ECR-IMP-0422.regSpecInvalid";

    /// <summary>No header block could be found.</summary>
    public const string HeaderNotFound = "err.ECR-IMP-0422.regHeaderNotFound";

    /// <summary>The import has errors; nothing was written.</summary>
    public const string Rejected = "err.ECR-IMP-0422.regRejected";

    /// <summary>Rows of other owners would be replaced and the caller did not confirm them.</summary>
    public const string ReplaceNotConfirmed = "err.ECR-IMP-0422.regReplaceNotConfirmed";

    /// <summary>The plan applied differs from the plan previewed.</summary>
    public const string PlanChanged = "err.ECR-IMP-0422.regPlanChanged";

    /// <summary>A required column was not found in the header.</summary>
    public const string ColumnMissing = "err.ECR-IMP-0422.regColumnMissing";

    /// <summary>More than one column matches a field.</summary>
    public const string ColumnAmbiguous = "err.ECR-IMP-0422.regColumnAmbiguous";

    /// <summary>One column is claimed by two fields.</summary>
    public const string ColumnClaimed = "err.ECR-IMP-0422.regColumnClaimed";

    /// <summary>A cell does not read as the field's type.</summary>
    public const string ValueInvalid = "err.ECR-IMP-0422.regValueInvalid";

    /// <summary>A required value is empty.</summary>
    public const string ValueRequired = "err.ECR-IMP-0422.regValueRequired";

    /// <summary>A value that should be filled is empty (warning, per row).</summary>
    public const string ValueEmpty = "err.ECR-IMP-0422.regValueEmpty";

    /// <summary>A negative number where only non-negative is allowed.</summary>
    public const string ValueNegative = "err.ECR-IMP-0422.regValueNegative";

    /// <summary>A number with one separator and three digits after it was read as a decimal (warning).</summary>
    public const string ValueAmbiguous = "err.ECR-IMP-0422.regValueAmbiguous";

    /// <summary>The same parent key twice in the file.</summary>
    public const string DuplicateParent = "err.ECR-IMP-0422.regDuplicateParent";

    /// <summary>The same child key twice under one parent.</summary>
    public const string DuplicateChild = "err.ECR-IMP-0422.regDuplicateChild";

    /// <summary>A referenced code is not in the target registry and has no resolution.</summary>
    public const string ReferenceMissing = "err.ECR-IMP-0422.regReferenceMissing";

    /// <summary>No resolver is registered for a reference kind: codes were not checked (warning).</summary>
    public const string ReferenceUnchecked = "err.ECR-IMP-0422.regReferenceUnchecked";

    /// <summary>A child row appears before any parent.</summary>
    public const string ChildWithoutParent = "err.ECR-IMP-0422.regChildWithoutParent";

    /// <summary>A parent has no child rows (warning).</summary>
    public const string ParentWithoutChildren = "err.ECR-IMP-0422.regParentWithoutChildren";

    /// <summary>The file holds no data rows.</summary>
    public const string NoRows = "err.ECR-IMP-0422.regNoRows";
}

/// <summary>Issue severity.</summary>
public enum ImportSeverity
{
    /// <summary>Does not block the import.</summary>
    Warning,

    /// <summary>Blocks the import (all or nothing).</summary>
    Error,
}

/// <summary>A finding of the parser or the planner.</summary>
/// <param name="Severity">Warning or error.</param>
/// <param name="MessageKey">Catalog key.</param>
/// <param name="Row">Excel row, when the finding is about one.</param>
/// <param name="Column">Excel column letter.</param>
/// <param name="Field">Field name.</param>
/// <param name="Args">Placeholders of the catalog text.</param>
public sealed record ImportIssue(
    ImportSeverity Severity,
    string MessageKey,
    int? Row = null,
    string? Column = null,
    string? Field = null,
    IReadOnlyDictionary<string, string>? Args = null);

/// <summary>A field matched to a column.</summary>
/// <param name="Field">Field name.</param>
/// <param name="Level">Parent or child.</param>
/// <param name="Column">1-based column.</param>
/// <param name="ColumnLetter">Column letter.</param>
/// <param name="HeaderText">The combined header text that matched.</param>
public sealed record MappedColumn(string Field, ImportLevel Level, int Column, string ColumnLetter, string HeaderText);

/// <summary>A non-empty column no field uses.</summary>
/// <param name="Column">1-based column.</param>
/// <param name="ColumnLetter">Column letter.</param>
/// <param name="HeaderText">Combined header text.</param>
/// <param name="HasData">There is data under it.</param>
public sealed record UnmappedColumn(int Column, string ColumnLetter, string HeaderText, bool HasData);

/// <summary>What the header detection found.</summary>
/// <param name="HeaderFromRow">First header row.</param>
/// <param name="HeaderToRow">Last header row.</param>
/// <param name="DataFromRow">First data row.</param>
/// <param name="Mapped">Matched columns.</param>
/// <param name="Unmapped">Columns with a header that no field uses.</param>
public sealed record ImportHeaderReport(
    int HeaderFromRow,
    int HeaderToRow,
    int DataFromRow,
    IReadOnlyList<MappedColumn> Mapped,
    IReadOnlyList<UnmappedColumn> Unmapped);

/// <summary>A group row.</summary>
/// <param name="Row">Excel row.</param>
/// <param name="Label">Label text.</param>
public sealed record ImportGroupRow(int Row, string Label);

/// <summary>A child row (e.g. a substance).</summary>
/// <param name="Row">Excel row.</param>
/// <param name="Key">Normalised child key.</param>
/// <param name="Values">Field values (decimal, long, string, DateTime or null).</param>
public sealed record ImportedChild(int Row, string Key, IReadOnlyDictionary<string, object?> Values);

/// <summary>A parent row with its children (e.g. an emission source and its substances).</summary>
/// <param name="Row">Excel row of the parent.</param>
/// <param name="Key">Normalised parent key.</param>
/// <param name="GroupLabel">Label of the group row above (context, not data).</param>
/// <param name="GroupRow">Row of that group row.</param>
/// <param name="Values">Parent field values.</param>
/// <param name="Children">Child rows.</param>
public sealed record ImportedParent(
    int Row,
    string Key,
    string? GroupLabel,
    int? GroupRow,
    IReadOnlyDictionary<string, object?> Values,
    IReadOnlyList<ImportedChild> Children);

/// <summary>Counters of a parse.</summary>
/// <param name="Parents">Parent rows.</param>
/// <param name="Children">Child rows.</param>
/// <param name="DistinctChildKeys">Distinct child keys.</param>
/// <param name="GroupRows">Group rows.</param>
/// <param name="SkippedRows">Non-data rows with content (totals, notes).</param>
public sealed record ImportStats(int Parents, int Children, int DistinctChildKeys, int GroupRows, int SkippedRows);

/// <summary>Result of reading a file with a spec.</summary>
/// <param name="Header">Header report; <c>null</c> when the header block was not found.</param>
/// <param name="Parents">Parents in file order.</param>
/// <param name="Groups">Group rows.</param>
/// <param name="Issues">Findings.</param>
/// <param name="Stats">Counters.</param>
/// <param name="DroppedIssues">Findings beyond <see cref="RegistryImportParser.MaxIssues"/> that were counted but not kept.</param>
public sealed record ParsedImport(
    ImportHeaderReport? Header,
    IReadOnlyList<ImportedParent> Parents,
    IReadOnlyList<ImportGroupRow> Groups,
    IReadOnlyList<ImportIssue> Issues,
    ImportStats Stats,
    int DroppedIssues = 0)
{
    /// <summary>At least one error.</summary>
    public bool HasErrors => Issues.Any(i => i.Severity == ImportSeverity.Error);
}
