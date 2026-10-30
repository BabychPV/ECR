// src/Ecr.Application/Permits/AirLimits/AirLimitsDtos.cs
using Ecr.Application.RegistryImport;

namespace Ecr.Application.Permits.AirLimits;

/// <summary>Result of <c>POST /api/v1/air-limits/provision</c>.</summary>
/// <param name="TypeCode">Permit type code (<c>AIR_LIMITS</c>).</param>
/// <param name="TypeCreated">The type was created by this call.</param>
/// <param name="Published">The type is published (permits can be created).</param>
/// <param name="Version">Published version of the type.</param>
/// <param name="SourcesRegistryCode">Registry code of the <c>AIR_SOURCES</c> table (what the generic registry rows API takes).</param>
/// <param name="SubstancesRegistryCode">Registry code of the <c>AIR_SOURCE_SUBSTANCES</c> table.</param>
/// <param name="ProfileCode">Built-in import profile (<c>ERA_TABLE_33</c>).</param>
/// <param name="ProfileCreated">The profile row was created by this call.</param>
public sealed record AirLimitsProvisionReport(
    string TypeCode,
    bool TypeCreated,
    bool Published,
    int Version,
    string SourcesRegistryCode,
    string SubstancesRegistryCode,
    string ProfileCode,
    bool ProfileCreated);

/// <summary>A stored import profile in a list.</summary>
/// <param name="Code">Profile code.</param>
/// <param name="NameL10n">Name by language.</param>
/// <param name="BuiltIn">Shipped with the system (<c>ERA_TABLE_33</c>); a built-in profile is reset by provisioning when it is deleted but can be edited.</param>
/// <param name="UpdatedAt">Last change, UTC.</param>
public sealed record ImportProfileSummaryDto(string Code, Dictionary<string, string> NameL10n, bool BuiltIn, DateTime UpdatedAt);

/// <summary>A stored import profile with its specification.</summary>
/// <param name="Code">Profile code.</param>
/// <param name="NameL10n">Name by language.</param>
/// <param name="BuiltIn">Shipped with the system.</param>
/// <param name="Spec">The specification: header block, fields with header patterns, group rows, fill rules, parsers.</param>
/// <param name="RowVersion">Concurrency token for the next <c>PUT</c>.</param>
/// <param name="UpdatedAt">Last change, UTC.</param>
public sealed record ImportProfileDto(
    string Code,
    Dictionary<string, string> NameL10n,
    bool BuiltIn,
    RegistryImportSpec Spec,
    string RowVersion,
    DateTime UpdatedAt);

/// <summary>Body of <c>PUT /api/v1/air-limits/import-profiles/{code}</c> (create or replace).</summary>
/// <param name="NameL10n">Name by language.</param>
/// <param name="Spec">The specification (validated; an invalid one is <c>422 regSpecInvalid</c>).</param>
/// <param name="RowVersion">Token read with the profile; <c>null</c> when the profile is new.</param>
public sealed record SaveImportProfileRequest(Dictionary<string, string> NameL10n, RegistryImportSpec Spec, string? RowVersion);

/// <summary>
/// The JSON part named <c>request</c> of the multipart import calls (<c>analyse</c>, <c>preview</c>, <c>apply</c>).
/// </summary>
/// <param name="ProfileCode">Stored profile; <c>null</c> — <c>ERA_TABLE_33</c> unless <paramref name="Spec"/> is given.</param>
/// <param name="Spec">An unsaved specification (the mapping editor's live preview); wins over <paramref name="ProfileCode"/>.</param>
/// <param name="Region">Region code (<c>ATYRAU</c>, <c>MANGISTAU</c>): with the area it scopes the NAE lookup and the replace.</param>
/// <param name="Area">Area code (<c>ONSHORE</c>, <c>OFFSHORE</c>); empty — a region without areas.</param>
/// <param name="Resolutions">Decisions about NAE codes missing in the tree: skip, or create under a node (<c>createUnder</c> = node entry id).</param>
/// <param name="ConfirmedOtherOwners">Permits (<c>ownerKey</c> = permit id) whose rows the human agreed to replace; apply only.</param>
/// <param name="ExpectedFingerprint">Fingerprint the preview showed; apply refuses a different plan (<c>regPlanChanged</c>).</param>
public sealed record AirLimitsImportOptions(
    string? ProfileCode,
    RegistryImportSpec? Spec,
    string? Region,
    string? Area,
    IReadOnlyList<MissingResolution>? Resolutions,
    IReadOnlyList<string>? ConfirmedOtherOwners,
    string? ExpectedFingerprint);

/// <summary>Result of <c>POST /api/v1/air-limits/import/analyse</c>: layout of the file, no database.</summary>
/// <param name="ProfileCode">Profile used; <c>null</c> for an unsaved specification.</param>
/// <param name="Analysis">Header block, mapped and unmapped columns, counts, findings.</param>
public sealed record AirLimitsAnalysisDto(string? ProfileCode, ImportAnalysis Analysis);

/// <summary>Result of <c>POST /api/v1/permits/{permitId}/air-limits/import/preview</c>.</summary>
/// <param name="PermitId">Permit.</param>
/// <param name="PermitNumber">Permit number.</param>
/// <param name="Region">Region code the import is scoped to.</param>
/// <param name="Area">Area code (<c>-</c> — none).</param>
/// <param name="ProfileCode">Profile used; <c>null</c> for an unsaved specification.</param>
/// <param name="Preview">Counts, findings (errors block, warnings do not), missing NAE and substances, replace scope, fingerprint.</param>
public sealed record AirLimitsPreviewDto(
    long PermitId,
    string PermitNumber,
    string Region,
    string Area,
    string? ProfileCode,
    RegistryImportPreview Preview);

/// <summary>Result of <c>POST /api/v1/permits/{permitId}/air-limits/import/apply</c>.</summary>
/// <param name="PermitId">Permit.</param>
/// <param name="PermitNumber">Permit number.</param>
/// <param name="Region">Region code.</param>
/// <param name="Area">Area code.</param>
/// <param name="Sources">Source rows now in the permit for this region and area.</param>
/// <param name="Substances">Substance rows inserted.</param>
/// <param name="CreatedNae">NAE nodes created in the tree.</param>
/// <param name="ReplacedOwn">The permit's own source rows that were replaced.</param>
/// <param name="ReplacedOthers">Rows of OTHER permits that were replaced (confirmed), per permit.</param>
/// <param name="Diff">New / changed / unchanged / removed counts.</param>
/// <param name="Fingerprint">Fingerprint of the applied plan (equals the preview's).</param>
/// <param name="FileSha256">Hash of the uploaded file.</param>
public sealed record AirLimitsImportResultDto(
    long PermitId,
    string PermitNumber,
    string Region,
    string Area,
    int Sources,
    int Substances,
    int CreatedNae,
    int ReplacedOwn,
    IReadOnlyList<OwnerReplace> ReplacedOthers,
    ImportDiffCounts Diff,
    string Fingerprint,
    string FileSha256);

/// <summary>One substance limit of a source.</summary>
/// <param name="Id">Row id in <c>AIR_SOURCE_SUBSTANCES</c>.</param>
/// <param name="SubstanceEntryId">Entry in the substance registry.</param>
/// <param name="SubstanceCode">Normalised substance code (4 digits).</param>
/// <param name="SubstanceName">Name from the registry (default language).</param>
/// <param name="GramsPerSecond">g/s.</param>
/// <param name="MgPerNm3">mg/nm3.</param>
/// <param name="TonsPerYear">t/year (full precision, no rounding).</param>
public sealed record AirSubstanceDto(
    long Id,
    long SubstanceEntryId,
    string SubstanceCode,
    string? SubstanceName,
    decimal? GramsPerSecond,
    decimal? MgPerNm3,
    decimal? TonsPerYear);

/// <summary>One source (NAE) of a permit with its substances.</summary>
/// <param name="Id">Row id in <c>AIR_SOURCES</c>.</param>
/// <param name="NaeEntryId">Node in the NAE tree.</param>
/// <param name="NaeCode">Normalised NAE code.</param>
/// <param name="Region">Region code.</param>
/// <param name="Area">Area code (<c>-</c> — none).</param>
/// <param name="SiteLabel">Production / site label from the group row of the file.</param>
/// <param name="ProductionCode">Production code (column A).</param>
/// <param name="SourceName">Source name.</param>
/// <param name="Quantity">Number of sources.</param>
/// <param name="HoursPerYear">Hours per year.</param>
/// <param name="EmissionPointName">Emission point name.</param>
/// <param name="SourceType">Canonical type: <c>T</c> (point) or <c>P1</c> (diffuse), Latin letters; the UI maps the label.</param>
/// <param name="Height">Height, m.</param>
/// <param name="Diameter">Diameter, m.</param>
/// <param name="Velocity">Gas velocity.</param>
/// <param name="Volume">Gas volume.</param>
/// <param name="Temperature">Gas temperature.</param>
/// <param name="X1">Coordinate X1.</param>
/// <param name="Y1">Coordinate Y1.</param>
/// <param name="X2">Coordinate X2.</param>
/// <param name="Y2">Coordinate Y2.</param>
/// <param name="Substances">Substances, by code.</param>
public sealed record AirSourceDto(
    long Id,
    long NaeEntryId,
    string NaeCode,
    string? Region,
    string? Area,
    string? SiteLabel,
    string? ProductionCode,
    string? SourceName,
    decimal? Quantity,
    decimal? HoursPerYear,
    string? EmissionPointName,
    string? SourceType,
    decimal? Height,
    decimal? Diameter,
    decimal? Velocity,
    decimal? Volume,
    decimal? Temperature,
    decimal? X1,
    decimal? Y1,
    decimal? X2,
    decimal? Y2,
    IReadOnlyList<AirSubstanceDto> Substances);

/// <summary>Total of one substance over all sources of the permit (computed, never stored).</summary>
/// <param name="SubstanceEntryId">Entry in the substance registry.</param>
/// <param name="SubstanceCode">Substance code.</param>
/// <param name="SubstanceName">Name from the registry.</param>
/// <param name="Sources">Sources that limit the substance.</param>
/// <param name="GramsPerSecond">Sum of g/s.</param>
/// <param name="TonsPerYear">Sum of t/year.</param>
public sealed record AirTotalDto(
    long SubstanceEntryId,
    string SubstanceCode,
    string? SubstanceName,
    int Sources,
    decimal GramsPerSecond,
    decimal TonsPerYear);

/// <summary>Result of <c>GET /api/v1/permits/{permitId}/air-limits</c>: the permit card content.</summary>
/// <param name="PermitId">Permit.</param>
/// <param name="PermitNumber">Permit number.</param>
/// <param name="TypeCode">Permit type code.</param>
/// <param name="ValidFrom">First valid day.</param>
/// <param name="ValidTo">First NOT valid day.</param>
/// <param name="Sources">Sources with substances, by NAE code.</param>
/// <param name="Totals">Totals per substance, by code.</param>
/// <param name="SubstanceRows">Substance rows in all sources.</param>
public sealed record AirLimitsPermitDto(
    long PermitId,
    string PermitNumber,
    string TypeCode,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    IReadOnlyList<AirSourceDto> Sources,
    IReadOnlyList<AirTotalDto> Totals,
    int SubstanceRows);
