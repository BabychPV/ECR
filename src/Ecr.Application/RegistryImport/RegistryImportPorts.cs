namespace Ecr.Application.RegistryImport;

/// <summary>Where the import lands: who owns the rows and which bucket they replace.</summary>
/// <param name="OwnerKey">Owner of the rows (e.g. the permit id).</param>
/// <param name="ScopeKey">Bucket inside which the owner's existing rows are replaced (e.g. <c>Atyrau/Offshore</c>).</param>
public sealed record ImportScope(string OwnerKey, string ScopeKey);

/// <summary>What to do with a referenced code that is not in the target registry.</summary>
public enum MissingAction
{
    /// <summary>Leave the parent out of the import.</summary>
    Skip,

    /// <summary>Create the missing entry (under <see cref="MissingResolution.CreateUnder"/>) in the same transaction.</summary>
    Create,
}

/// <summary>A human decision about one missing reference.</summary>
/// <param name="Kind">Reference kind (<c>nae</c>).</param>
/// <param name="Code">Normalised code.</param>
/// <param name="Action">Skip or create.</param>
/// <param name="CreateUnder">Node the new entry is created under (target-defined id).</param>
public sealed record MissingResolution(string Kind, string Code, MissingAction Action, string? CreateUnder = null);

/// <summary>A child row already stored.</summary>
/// <param name="Key">Child key.</param>
/// <param name="Values">Stored values, by field name.</param>
public sealed record ExistingChild(string Key, IReadOnlyDictionary<string, object?> Values);

/// <summary>A parent row already stored, with its children.</summary>
/// <param name="OwnerKey">Owner (permit id).</param>
/// <param name="OwnerLabel">Human label of the owner (permit number) for the confirmation.</param>
/// <param name="Key">Parent key.</param>
/// <param name="Values">Stored values, by field name.</param>
/// <param name="Children">Stored children.</param>
public sealed record ExistingParent(
    string OwnerKey,
    string? OwnerLabel,
    string Key,
    IReadOnlyDictionary<string, object?> Values,
    IReadOnlyList<ExistingChild> Children);

/// <summary>Reference to a stored parent that the plan deletes.</summary>
/// <param name="OwnerKey">Owner.</param>
/// <param name="Key">Parent key.</param>
public sealed record ExistingRef(string OwnerKey, string Key);

/// <summary>A missing entry to create with the import.</summary>
/// <param name="Kind">Reference kind.</param>
/// <param name="Code">Normalised code.</param>
/// <param name="UnderNode">Node to create it under.</param>
/// <param name="Row">Excel row of the parent that introduced it.</param>
/// <param name="GroupLabel">Group-row label above that parent (the site).</param>
/// <param name="Values">The parent's values (names, type...) for the new entry.</param>
public sealed record ReferenceCreation(
    string Kind,
    string Code,
    string? UnderNode,
    int Row,
    string? GroupLabel,
    IReadOnlyDictionary<string, object?> Values);

/// <summary>Everything the target must do, in ONE transaction (all or nothing).</summary>
/// <param name="Scope">Owner and bucket.</param>
/// <param name="Delete">Stored parents (and their children) to delete: the owner's rows in the bucket plus the listed rows of other owners.</param>
/// <param name="Insert">Parents with children to insert, in file order.</param>
/// <param name="CreateReferences">Entries to create first.</param>
/// <param name="Fingerprint">Hash of the content above; the same one the preview showed.</param>
/// <param name="FileSha256">Hash of the uploaded file (audit).</param>
/// <param name="ProfileCode">Profile used (audit).</param>
/// <param name="Stats">Counters of the parse.</param>
public sealed record ImportApplyPlan(
    ImportScope Scope,
    IReadOnlyList<ExistingRef> Delete,
    IReadOnlyList<ImportedParent> Insert,
    IReadOnlyList<ReferenceCreation> CreateReferences,
    string Fingerprint,
    string? FileSha256,
    string? ProfileCode,
    ImportStats Stats);

/// <summary>
/// Storage side of an import. Implemented with the permit tables (plan phase N3); the engine only fakes it in tests.
/// </summary>
public interface IRegistryImportTarget
{
    /// <summary>
    /// Stored parents that the import may touch: EVERY parent of <c>scope.OwnerKey</c> in <c>scope.ScopeKey</c>, and the parents
    /// of OTHER owners whose key is in <paramref name="parentKeys"/>.
    /// </summary>
    public Task<IReadOnlyList<ExistingParent>> ReadExistingAsync(
        ImportScope scope, IReadOnlyCollection<string> parentKeys, CancellationToken ct);

    /// <summary>Applies the plan atomically: on any failure nothing is written.</summary>
    public Task ApplyAsync(ImportApplyPlan plan, CancellationToken ct);
}

/// <summary>Checks codes against a registry (<c>nae</c> tree, <c>substance</c> list).</summary>
public interface IReferenceResolver
{
    /// <summary>Kind named by <see cref="ImportFieldSpec.Reference"/>.</summary>
    public string Kind { get; }

    /// <summary>The subset of <paramref name="codes"/> (normalised) that exists for this import.</summary>
    public Task<IReadOnlySet<string>> FindExistingAsync(
        IReadOnlyCollection<string> codes, ImportScope scope, CancellationToken ct);
}

/// <summary>NAE validation hook (<see cref="IReferenceResolver.Kind"/> is <c>nae</c>); implemented with the NAE tree (N2/N3).</summary>
public interface INaeResolver : IReferenceResolver
{
    /// <summary>Kind constant.</summary>
    public const string NaeKind = "nae";
}

/// <summary>Substance validation hook (<see cref="IReferenceResolver.Kind"/> is <c>substance</c>).</summary>
public interface ISubstanceResolver : IReferenceResolver
{
    /// <summary>Kind constant.</summary>
    public const string SubstanceKind = "substance";
}

/// <summary>What the layout analysis of a file found (wizard steps 1-3): nothing is read from or written to the database.</summary>
/// <param name="FileSha256">Hash of the uploaded file.</param>
/// <param name="SheetName">Sheet that was read.</param>
/// <param name="SheetNames">Visible sheets of the workbook.</param>
/// <param name="Header">Header block and the column mapping; <c>null</c> when no header block was found.</param>
/// <param name="Stats">Counters of the parse.</param>
/// <param name="Groups">Group rows.</param>
/// <param name="Issues">Findings (columns not found, values that do not read).</param>
/// <param name="DroppedIssues">Findings beyond the limit.</param>
/// <param name="CanContinue">The header was found and no finding is an error.</param>
public sealed record ImportAnalysis(
    string FileSha256,
    string SheetName,
    IReadOnlyList<string> SheetNames,
    ImportHeaderReport? Header,
    ImportStats Stats,
    IReadOnlyList<ImportGroupRow> Groups,
    IReadOnlyList<ImportIssue> Issues,
    int DroppedIssues,
    bool CanContinue);

/// <summary>Reads a workbook with a profile and runs the engine; implemented by the Excel adapter.</summary>
public interface IRegistryFileImporter
{
    /// <summary>Layout analysis: header, mapping, counts, findings.</summary>
    public Task<ImportAnalysis> AnalyseAsync(Stream file, RegistryImportSpec spec, CancellationToken ct);

    /// <summary>Preview over the database state; nothing is written.</summary>
    public Task<RegistryImportPreview> PreviewAsync(Stream file, RegistryImportSpec spec, ImportRequest request, CancellationToken ct);

    /// <summary>Commit: the same plan as the preview, all or nothing.</summary>
    public Task<ImportCommitResult> CommitAsync(Stream file, RegistryImportSpec spec, ImportRequest request, CancellationToken ct);
}