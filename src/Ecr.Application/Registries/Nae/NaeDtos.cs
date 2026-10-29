// src/Ecr.Application/Registries/Nae/NaeDtos.cs
namespace Ecr.Application.Registries.Nae;

/// <summary>One-time load of the NAE tree from the PI AF element tree (NAE plan N2).</summary>
/// <param name="DataSourceId">PI AF data source to read.</param>
/// <param name="RootPath">
/// AF element that holds the equipment tree; <c>null</c> — <see cref="NaeTreeSchema.DefaultAfRoot"/> (<c>ECR_01_Air Equipment</c>).
/// </param>
/// <param name="RegistryCode">Target registry; <c>null</c> — <see cref="NaeTreeSchema.RegistryCode"/> (<c>NAE_TREE</c>).</param>
/// <param name="DryRun"><c>true</c> — build the report only: nothing is created or changed (not even the registry).</param>
public sealed record ImportNaeFromAfRequest(int DataSourceId, string? RootPath, string? RegistryCode, bool DryRun);

/// <summary>Count of nodes of one level in an import report.</summary>
/// <param name="LevelCode">Level code (<c>REGION</c>, <c>AREA</c>, <c>SITE</c>, <c>EQUIP</c>, <c>SRC</c>).</param>
/// <param name="Create">Nodes that are created (dry run: would be created).</param>
/// <param name="Existing">Nodes that already exist and are left untouched (ECR owns the tree after the first load).</param>
public sealed record NaeAfLevelCountDto(string LevelCode, int Create, int Existing);

/// <summary>One finding of the import: an element that was skipped or a rule that fired.</summary>
/// <param name="Severity"><c>warning</c> (loaded with a caveat) or <c>skipped</c> (not loaded).</param>
/// <param name="Code">
/// <c>noCode</c> (empty NAE code), <c>noRegion</c> (no Region attribute), <c>duplicateBusinessKey</c> (same NAE code, region and
/// area twice in AF), <c>pathFlattened</c> (more than two grouping levels under the area), <c>existingElsewhere</c>
/// (the business key already belongs to another node), <c>sourceIncomplete</c> (AF listing may be truncated).
/// </param>
/// <param name="Path">AF path of the element (or empty for a whole-source finding).</param>
/// <param name="Detail">Short explanation for the report (English; the UI shows its own text per <c>Code</c>).</param>
public sealed record NaeAfIssueDto(string Severity, string Code, string Path, string Detail);

/// <summary>Result of <c>POST /api/v1/nae-tree/af-import</c>.</summary>
/// <param name="RegistryCode">Target registry.</param>
/// <param name="DryRun">The request was a dry run.</param>
/// <param name="Applied">Something was written (never for a dry run or when there was nothing new).</param>
/// <param name="RegistryProvisioned">The registry (fields, key, levels) was created by this call.</param>
/// <param name="ElementsRead">AF elements read under the root.</param>
/// <param name="SourceComplete">The AF listing is complete (<c>false</c> — it may have been truncated).</param>
/// <param name="Levels">Per level: created and existing nodes (levels with no nodes are present with zeros).</param>
/// <param name="Skipped">Elements not loaded (see <paramref name="Issues"/>).</param>
/// <param name="Issues">Findings, at most 200 (the rest is counted in <paramref name="Skipped"/>).</param>
public sealed record NaeAfImportReport(
    string RegistryCode,
    bool DryRun,
    bool Applied,
    bool RegistryProvisioned,
    int ElementsRead,
    bool SourceComplete,
    IReadOnlyList<NaeAfLevelCountDto> Levels,
    int Skipped,
    IReadOnlyList<NaeAfIssueDto> Issues);

/// <summary>One place where a registry entry is used (the "where used" panel of the NAE tree).</summary>
/// <param name="Kind"><c>permit</c> (the only kind today; others may follow).</param>
/// <param name="PermitId">Permit header entry id.</param>
/// <param name="PermitNumber">Permit number as shown to the user.</param>
/// <param name="PermitTypeCode">Permit type code.</param>
/// <param name="ValidFrom">First valid day of the permit.</param>
/// <param name="ValidTo">First NOT valid day of the permit (exclusive).</param>
/// <param name="SubstanceCount">Substances limited for the entry in this permit.</param>
public sealed record RegistryEntryReferenceDto(
    string Kind,
    long PermitId,
    string PermitNumber,
    string PermitTypeCode,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    int SubstanceCount);

/// <summary>Where a registry entry is used.</summary>
/// <param name="RegistryCode">Registry code.</param>
/// <param name="EntryId">Entry.</param>
/// <param name="Items">Uses, permit number then id; empty while no permit table points at the entry.</param>
/// <param name="Total">Number of uses.</param>
public sealed record RegistryEntryReferencesDto(
    string RegistryCode,
    long EntryId,
    IReadOnlyList<RegistryEntryReferenceDto> Items,
    int Total);
