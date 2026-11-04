// src/Ecr.Application/Registries/Nae/NaeManualDtos.cs
namespace Ecr.Application.Registries.Nae;

/// <summary>Creates a root of the NAE tree by hand (no AF): a region and, optionally, one area under it.</summary>
/// <param name="RegionCode">Region code (<c>ATYRAU</c>); normalised to upper case.</param>
/// <param name="AreaCode">Area code (<c>OFFSHORE</c>); <c>null</c> or empty — only the region.</param>
/// <param name="NameEn">English name of the deepest created node; <c>null</c> — the code as written.</param>
/// <param name="NameRu">Russian name of the deepest created node.</param>
/// <param name="NameKz">Kazakh name of the deepest created node.</param>
public sealed record CreateNaeRootRequest(string RegionCode, string? AreaCode, string? NameEn, string? NameRu, string? NameKz);

/// <summary>Result of <c>POST /api/v1/nae-tree/roots</c>.</summary>
/// <param name="RegistryCode">Registry (<c>NAE_TREE</c>).</param>
/// <param name="RegionEntryId">The region node (created or already there).</param>
/// <param name="AreaEntryId">The area node; <c>null</c> when no area was asked.</param>
/// <param name="Created">Something was created (<c>false</c> — every asked node already existed; nothing was changed).</param>
/// <param name="RegistryProvisioned">The registry (fields, key, levels) was created by this call.</param>
public sealed record NaeRootResultDto(string RegistryCode, long RegionEntryId, long? AreaEntryId, bool Created, bool RegistryProvisioned);

/// <summary>Options of the Excel import of the NAE tree (form part <c>request</c>, JSON).</summary>
/// <param name="ExpectedFingerprint">Apply only: the fingerprint the preview showed; a different plan is refused (<c>regPlanChanged</c>).</param>
public sealed record NaeTreeImportOptions(string? ExpectedFingerprint);

/// <summary>One finding of the Excel import of the NAE tree.</summary>
/// <param name="Severity"><c>error</c> (blocks the apply) or <c>warning</c> (reported, the apply is allowed).</param>
/// <param name="Code">
/// <c>headerNotFound</c>, <c>regionRequired</c>, <c>badCode</c>, <c>areaMismatch</c> (Area and On_Offshore
/// differ), <c>duplicateInFile</c>, <c>existingElsewhere</c> (the business key belongs to a node under another parent),
/// <c>codeTaken</c> (the technical code belongs to another node), <c>nameDiffers</c> (warning: the stored name differs; names are
/// not overwritten).
/// </param>
/// <param name="Row">Excel row (1-based); 0 — the whole file.</param>
/// <param name="Column">Template column name (<c>NAE code</c>), when the finding is about one cell.</param>
/// <param name="NaeCode">Normalised NAE code of the row, when it has one.</param>
/// <param name="Detail">Short explanation (English; the UI shows its own text per <c>Code</c>).</param>
public sealed record NaeTreeImportIssueDto(string Severity, string Code, int Row, string? Column, string? NaeCode, string Detail);

/// <summary>Result of the NAE tree Excel import (analyse, preview and apply return the same shape).</summary>
/// <param name="RegistryCode">Registry (<c>NAE_TREE</c>).</param>
/// <param name="FileSha256">Hash of the uploaded file.</param>
/// <param name="SheetName">Sheet that was read.</param>
/// <param name="RowsRead">Data rows read (empty rows not counted).</param>
/// <param name="Levels">Per level: nodes to create (applied: created) and nodes already in the tree (left untouched).</param>
/// <param name="Issues">Findings, at most 200.</param>
/// <param name="DroppedIssues">Findings beyond the limit.</param>
/// <param name="Fingerprint">Hash of the plan; pass it back to apply as <c>expectedFingerprint</c>.</param>
/// <param name="CanApply">No error finding (analyse: the file itself is clean; the database is not consulted).</param>
/// <param name="Applied">Something was written (apply only; never when there was nothing new).</param>
/// <param name="RegistryProvisioned">The registry was created by this call (apply only).</param>
public sealed record NaeTreeImportReportDto(
    string RegistryCode,
    string FileSha256,
    string SheetName,
    int RowsRead,
    IReadOnlyList<NaeAfLevelCountDto> Levels,
    IReadOnlyList<NaeTreeImportIssueDto> Issues,
    int DroppedIssues,
    string Fingerprint,
    bool CanApply,
    bool Applied,
    bool RegistryProvisioned);

/// <summary>Result of <c>POST /api/v1/nae-tree/seed-roots</c>.</summary>
/// <param name="RegistryCode">Registry (<c>NAE_TREE</c>).</param>
/// <param name="SourceRegistryCode">Registry the region list was read from (<c>LAND_REGION</c>).</param>
/// <param name="Created">Region codes created as roots, in order.</param>
/// <param name="RegistryProvisioned">The registry was created by this call.</param>
public sealed record NaeSeedRootsResultDto(
    string RegistryCode,
    string SourceRegistryCode,
    IReadOnlyList<string> Created,
    bool RegistryProvisioned);
