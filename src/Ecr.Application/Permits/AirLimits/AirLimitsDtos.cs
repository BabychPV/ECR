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
/// <param name="Region">
/// ✎ AIR-ON-AIR: value of the REGION scope column when it is configured as Text or as a List (a choice, by text). For a Registry
/// column use <paramref name="RegionEntryId"/>. Form field <c>region</c> overrides it.
/// </param>
/// <param name="Area">Value of the AREA scope column (Text / List); optional. Form field <c>area</c> overrides it.</param>
/// <param name="Resolutions">Decisions about NAE codes missing in the tree: skip, or create under a node (<c>createUnder</c> = node entry id).</param>
/// <param name="ConfirmedOtherOwners">Permits (<c>ownerKey</c> = permit id) whose rows the human agreed to replace; apply only.</param>
/// <param name="ExpectedFingerprint">Fingerprint the preview showed; apply refuses a different plan (<c>regPlanChanged</c>).</param>
/// <param name="RegionEntryId">
/// ✎ AIR-ON-AIR (human 02.11): entry of the registry the REGION scope column points at (Registry kind; default <c>LAND_REGION</c>);
/// also the form field <c>regionEntryId</c>. 422 when it is not an entry of that registry.
/// </param>
/// <param name="AreaEntryId">Entry of the registry of the AREA scope column (default <c>AIR_AREA</c>); optional; form field <c>areaEntryId</c>.</param>
public sealed record AirLimitsImportOptions(
    string? ProfileCode,
    RegistryImportSpec? Spec,
    string? Region,
    string? Area,
    IReadOnlyList<MissingResolution>? Resolutions,
    IReadOnlyList<string>? ConfirmedOtherOwners,
    string? ExpectedFingerprint,
    long? RegionEntryId = null,
    long? AreaEntryId = null);

/// <summary>One value the import wizard offers for a scope column, with the NAE tree scope it maps to.</summary>
/// <param name="EntryId">Registry entry id (Registry kind: what <c>regionEntryId</c>/<c>areaEntryId</c> take); <c>null</c> for a List choice.</param>
/// <param name="Code">Entry code (<c>REG_001</c>, <c>OFFSHORE</c>) or the choice text.</param>
/// <param name="Name">Name (default language) or the choice text.</param>
/// <param name="NaeRegion">NAE tree region code the value maps to (REGION column; by normalised name); <c>null</c> — not in the NAE tree.</param>
/// <param name="NaeArea">
/// NAE tree area code (AREA column; only where the tree region has areas: <c>Offshore</c> → <c>OFFSHORE</c>); <c>null</c> — match by
/// region only.
/// </param>
public sealed record AirScopeEntryDto(long? EntryId, string Code, string Name, string? NaeRegion, string? NaeArea);

/// <summary>
/// ✎ AIR-ON-AIR: one scope column of the AIR permit's sources table (REGION or AREA) as configured on the AIR type with the SAME
/// column settings as any permit-type column (data type + list scope): <c>Text</c> (String), <c>Registry</c> (Lookup, shared
/// registry), <c>List</c> (Lookup, values of the type, entered separated by «;»).
/// </summary>
/// <param name="ColumnCode">Column code (<c>REGION</c>, <c>AREA</c>).</param>
/// <param name="Kind"><c>Text</c>, <c>Registry</c> or <c>List</c>.</param>
/// <param name="RegistryCode">Registry the value comes from (Registry: <c>LAND_REGION</c>, <c>AIR_AREA</c>; List: the type's own list registry).</param>
/// <param name="Required">A value is required (REGION) or optional (AREA).</param>
/// <param name="RegistryFound">The registry exists; <c>false</c> — empty state, the import cannot use this column's values.</param>
/// <param name="Options">Values to pick from (Registry / List), by code; empty for Text.</param>
public sealed record AirScopeFieldDto(
    string ColumnCode,
    string Kind,
    string? RegistryCode,
    bool Required,
    bool RegistryFound,
    IReadOnlyList<AirScopeEntryDto> Options);

/// <summary>Result of <c>GET /api/v1/air-limits/scope-fields</c>: how the import scope is configured on the AIR type, with values.</summary>
/// <param name="TypeCode">Permit type (<c>AIR</c>).</param>
/// <param name="Region">The REGION scope column.</param>
/// <param name="Area">The AREA scope column.</param>
public sealed record AirScopeFieldsDto(string TypeCode, AirScopeFieldDto Region, AirScopeFieldDto Area);

/// <summary>Result of <c>POST /api/v1/air-limits/import/analyse</c>: layout of the file, no database.</summary>
/// <param name="ProfileCode">Profile used; <c>null</c> for an unsaved specification.</param>
/// <param name="Analysis">Header block, mapped and unmapped columns, counts, findings.</param>
/// <param name="UnsavedFields">
/// ✎ Pass 3: fields of the profile that match no column of the target tables (neither by code nor by display name) — their
/// values are read but WILL NOT BE SAVED; the wizard warns about each.
/// </param>
public sealed record AirLimitsAnalysisDto(string? ProfileCode, ImportAnalysis Analysis, IReadOnlyList<string>? UnsavedFields = null);

/// <summary>Result of <c>POST /api/v1/permits/{permitId}/air-limits/import/preview</c>.</summary>
/// <param name="PermitId">Permit.</param>
/// <param name="PermitNumber">Permit number.</param>
/// <param name="Region">Region code the import is scoped to.</param>
/// <param name="Area">Area code (<c>-</c> — none).</param>
/// <param name="ProfileCode">Profile used; <c>null</c> for an unsaved specification.</param>
/// <param name="Preview">Counts, findings (errors block, warnings do not), missing NAE and substances, replace scope, fingerprint.</param>
/// <param name="RegionEntryId">Region registry entry the import is scoped to (stored on each source row).</param>
/// <param name="AreaEntryId">Area registry entry; <c>null</c> — none.</param>
/// <param name="ScopeInNaeTree">
/// The region (and area) map to the NAE tree; <c>false</c> — «region/area not in NAE tree»: NAE codes are matched without the tree scope.
/// </param>
/// <param name="UnsavedFields">✎ Pass 3: fields that match no target column — their values will not be saved.</param>
public sealed record AirLimitsPreviewDto(
    long PermitId,
    string PermitNumber,
    string Region,
    string Area,
    string? ProfileCode,
    RegistryImportPreview Preview,
    long? RegionEntryId = null,
    long? AreaEntryId = null,
    bool ScopeInNaeTree = true,
    IReadOnlyList<string>? UnsavedFields = null);

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
/// <param name="RegionEntryId">Region registry entry the import was scoped to.</param>
/// <param name="AreaEntryId">Area registry entry; <c>null</c> — none.</param>
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
    string FileSha256,
    long? RegionEntryId = null,
    long? AreaEntryId = null);

/// <summary>One substance limit of a source.</summary>
/// <param name="Id">Row id in <c>AIR_SOURCE_SUBSTANCES</c>.</param>
/// <param name="SubstanceEntryId">Entry in the substance registry.</param>
/// <param name="SubstanceCode">Normalised substance code (4 digits).</param>
/// <param name="SubstanceName">Name from the registry (default language).</param>
/// <param name="GramsPerSecond">g/s.</param>
/// <param name="MgPerNm3">mg/nm3.</param>
/// <param name="TonsPerYear">t/year (full precision, no rounding).</param>
/// <param name="Added">
/// P1 07.11: values of the columns ADDED to <c>AIR_SOURCE_SUBSTANCES</c> (the card's <c>addedColumns</c>), by code:
/// number (Int / Decimal), text, ISO date <c>yyyy-MM-dd</c>, bool or lookup entry id; a column without a value is absent.
/// </param>
/// <param name="Version">Row version — the <c>baseVersion</c> of <c>POST /api/v1/registries/{code}/entries/batch</c>.</param>
public sealed record AirSubstanceDto(
    long Id,
    long SubstanceEntryId,
    string SubstanceCode,
    string? SubstanceName,
    decimal? GramsPerSecond,
    decimal? MgPerNm3,
    decimal? TonsPerYear,
    IReadOnlyDictionary<string, object?>? Added = null,
    string? Version = null);

/// <summary>A column added to a sources table of the AIR type (P1 06.11 «Add as new column»), for the card headers.</summary>
/// <param name="Table"><c>sources</c> (AIR_SOURCES) or <c>substances</c> (AIR_SOURCE_SUBSTANCES).</param>
/// <param name="Code">Column (registry field) code, e.g. <c>FILE_B</c>.</param>
/// <param name="NameL10n">Display name by language.</param>
/// <param name="DataType"><c>String</c>, <c>Int</c>, <c>Decimal</c>, <c>Date</c>, <c>Bool</c> or <c>Lookup</c>.</param>
public sealed record AirAddedColumnDto(string Table, string Code, IReadOnlyDictionary<string, string> NameL10n, string DataType);

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
/// <param name="RegionEntryId">Region registry entry of the import block this row came from.</param>
/// <param name="RegionEntryCode">Its code (<c>REG_001</c>).</param>
/// <param name="AreaEntryId">Area registry entry; <c>null</c> — none.</param>
/// <param name="AreaEntryCode">Its code (<c>AREA_018</c>).</param>
/// <param name="Added">P1 07.11: values of the columns added to <c>AIR_SOURCES</c>, by code (as the substance's <c>added</c>).</param>
/// <param name="Version">Row version — the <c>baseVersion</c> of the generic batch write.</param>
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
    IReadOnlyList<AirSubstanceDto> Substances,
    long? RegionEntryId = null,
    string? RegionEntryCode = null,
    long? AreaEntryId = null,
    string? AreaEntryCode = null,
    IReadOnlyDictionary<string, object?>? Added = null,
    string? Version = null);

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
/// <param name="AddedColumns">P1 07.11: columns added to the two tables (sources first, then substances), in table order.</param>
/// <param name="SourcesRegistryCode">Registry of <c>AIR_SOURCES</c> — where the card writes an edited source row (generic batch).</param>
/// <param name="SubstancesRegistryCode">Registry of <c>AIR_SOURCE_SUBSTANCES</c>.</param>
public sealed record AirLimitsPermitDto(
    long PermitId,
    string PermitNumber,
    string TypeCode,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    IReadOnlyList<AirSourceDto> Sources,
    IReadOnlyList<AirTotalDto> Totals,
    int SubstanceRows,
    IReadOnlyList<AirAddedColumnDto>? AddedColumns = null,
    string? SourcesRegistryCode = null,
    string? SubstancesRegistryCode = null);
