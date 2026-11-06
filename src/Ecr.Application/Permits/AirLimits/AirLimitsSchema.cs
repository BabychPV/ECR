// src/Ecr.Application/Permits/AirLimits/AirLimitsSchema.cs
using Ecr.Application.Permits.Dto;
using Ecr.Application.Registries.Nae;

namespace Ecr.Application.Permits.AirLimits;

/// <summary>Registry codes the air-limits permit points at (configurable; tests use their own).</summary>
/// <param name="NaeRegistryCode">NAE tree registry.</param>
/// <param name="SubstanceRegistryCode">Substance registry (records <c>E&lt;code&gt;</c> with field <c>SUBSTANCE_CODE</c>).</param>
/// <param name="RegionRegistryCode">✎ AIR-ON-AIR: default registry of the REGION scope column (Land bootstrap: <c>LAND_REGION</c>).</param>
/// <param name="AreaRegistryCode">Default registry of the AREA scope column (created by provisioning: <c>AIR_AREA</c>).</param>
public sealed record AirLimitsSettings(
    string NaeRegistryCode,
    string SubstanceRegistryCode,
    string RegionRegistryCode = AirLimitsSchema.DefaultRegionRegistry,
    string AreaRegistryCode = AirLimitsSchema.DefaultAreaRegistry)
{
    /// <summary>Production defaults.</summary>
    public static AirLimitsSettings Default { get; } = new(NaeTreeSchema.RegistryCode, AirLimitsSchema.DefaultSubstanceRegistry);
}

/// <summary>Fixed names of the "Air Limits Import" permit type (NAE plan N3).</summary>
public static class AirLimitsSchema
{
    /// <summary>
    /// Permit type code. ✎ AIR-ON-AIR (human 02.11: «таблиця параметрів відноситься до пермиту … а не як окремий тип»): the
    /// sources tables belong to the existing «Air emission permit» type <c>AIR</c>; the former separate type <c>AIR_LIMITS</c> is
    /// no longer provisioned (an existing one is left as it is, nothing is deleted).
    /// </summary>
    public const string TypeCode = "AIR";

    /// <summary>The retired separate type (kept only to recognise it; never provisioned again).</summary>
    public const string RetiredTypeCode = "AIR_LIMITS";

    /// <summary>✎ AIR-ON-AIR: default Region registry (Land bootstrap; not created by us).</summary>
    public const string DefaultRegionRegistry = "LAND_REGION";

    /// <summary>✎ AIR-ON-AIR: default Area registry (OFFSHORE / ONSHORE), created by provisioning when absent.</summary>
    public const string DefaultAreaRegistry = "AIR_AREA";

    /// <summary>Scope kind: free text (String column).</summary>
    public const string KindText = "Text";

    /// <summary>Scope kind: entry of a shared registry (Lookup column, <c>SharedRegistry</c>).</summary>
    public const string KindRegistry = "Registry";

    /// <summary>Scope kind: one of the type's own values (Lookup column, <c>TypeOnly</c>; entered separated by «;»).</summary>
    public const string KindList = "List";

    /// <summary>Table of sources (one row per NAE in the permit).</summary>
    public const string SourcesTable = "AIR_SOURCES";

    /// <summary>Table of substances (one row per NAE and substance).</summary>
    public const string SubstancesTable = "AIR_SOURCE_SUBSTANCES";

    /// <summary>Default substance registry of the customer (Land air substances).</summary>
    public const string DefaultSubstanceRegistry = "LAND_AIR_SUBSTANCE";

    /// <summary>Field of the substance registry that holds the numeric substance code.</summary>
    public const string SubstanceCodeField = "SUBSTANCE_CODE";

    /// <summary>Built-in import profile code.</summary>
    public const string ProfileCode = "ERA_TABLE_33";

    /// <summary>Area code of a region without areas.</summary>
    public const string NoArea = NaeAfTreeBuilder.NoArea;

    /// <summary>Registry code of the sources table.</summary>
    public static string SourcesRegistry => PermitHeaderFields.TableRegistryCode(TypeCode, SourcesTable);

    /// <summary>Registry code of the substances table.</summary>
    public static string SubstancesRegistry => PermitHeaderFields.TableRegistryCode(TypeCode, SubstancesTable);

    /// <summary>Header registry code.</summary>
    public static string HeaderRegistry => PermitHeaderFields.HeaderRegistryCode(TypeCode);

    // ---- AIR_SOURCES columns ----
    public const string Nae = "NAE";
    public const string Region = "REGION";
    public const string Area = "AREA";
    public const string SiteLabel = "SITE_LABEL";
    public const string ProductionCode = "PRODUCTION_CODE";
    public const string SourceName = "SOURCE_NAME";
    public const string Quantity = "QUANTITY";
    public const string HoursPerYear = "HOURS_PER_YEAR";
    public const string PointName = "POINT_NAME";
    public const string SourceType = "SOURCE_TYPE";
    public const string Height = "HEIGHT";
    public const string Diameter = "DIAMETER";
    public const string Velocity = "VELOCITY";
    public const string Volume = "VOLUME";
    public const string Temperature = "TEMPERATURE";
    public const string X1 = "X1";
    public const string Y1 = "Y1";
    public const string X2 = "X2";
    public const string Y2 = "Y2";

    // ---- AIR_SOURCE_SUBSTANCES columns ----
    public const string Source = "SOURCE";
    public const string Substance = "SUBSTANCE";
    public const string GramsPerSecond = "GRAMS_PER_SECOND";
    public const string MgPerNm3 = "MG_PER_NM3";
    public const string TonsPerYear = "TONS_PER_YEAR";

    /// <summary>Import field name (profile) → source column.</summary>
    public static IReadOnlyDictionary<string, string> ParentColumns { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["productionCode"] = ProductionCode,
        ["sourceName"] = SourceName,
        ["quantity"] = Quantity,
        ["hoursPerYear"] = HoursPerYear,
        ["emissionPointName"] = PointName,
        ["sourceType"] = SourceType,
        ["height"] = Height,
        ["diameter"] = Diameter,
        ["velocity"] = Velocity,
        ["volume"] = Volume,
        ["temperature"] = Temperature,
        ["x1"] = X1,
        ["y1"] = Y1,
        ["x2"] = X2,
        ["y2"] = Y2,
    };

    /// <summary>Import field name (profile) → substance column.</summary>
    public static IReadOnlyDictionary<string, string> ChildColumns { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["gramsPerSecond"] = GramsPerSecond,
        ["mgPerNm3"] = MgPerNm3,
        ["tonsPerYear"] = TonsPerYear,
    };

    /// <summary>Columns of AIR_SOURCES the import fills itself (never from a profile field of the same code).</summary>
    private static readonly HashSet<string> SourceSystemColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        PermitHeaderFields.PermitFieldCode, PermitHeaderFields.PrimaryKeyCode, Nae, Region, Area, SiteLabel,
    };

    /// <summary>Columns of AIR_SOURCE_SUBSTANCES the import fills itself.</summary>
    private static readonly HashSet<string> SubstanceSystemColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        PermitHeaderFields.PermitFieldCode, PermitHeaderFields.PrimaryKeyCode, Source, Substance,
    };

    /// <summary>
    /// Import field name → source column: the built-in <see cref="ParentColumns"/> plus every column added to the table later
    /// (P1 06.11, «Add as new column»: the user adds a column through the permit-type column API and maps a file column to a
    /// profile field with the SAME code). System columns and the built-in targets are never taken this way.
    /// </summary>
    /// <param name="sources">The AIR_SOURCES definition.</param>
    public static IReadOnlyDictionary<string, string> ParentColumnsOf(Domain.Entities.Configuration.RegistryDef sources)
        => WithAddedColumns(ParentColumns, sources, SourceSystemColumns);

    /// <summary>Import field name → substance column, like <see cref="ParentColumnsOf"/>.</summary>
    /// <param name="substances">The AIR_SOURCE_SUBSTANCES definition.</param>
    public static IReadOnlyDictionary<string, string> ChildColumnsOf(Domain.Entities.Configuration.RegistryDef substances)
        => WithAddedColumns(ChildColumns, substances, SubstanceSystemColumns);

    /// <summary>
    /// Columns ADDED to AIR_SOURCES later (neither system nor built-in), in table order — the same rule as <see cref="ParentColumnsOf"/>;
    /// the permit card shows them (P1 07.11: «the user must SEE ALL imported data»).
    /// </summary>
    /// <param name="sources">The AIR_SOURCES definition.</param>
    public static IReadOnlyList<Domain.Entities.Configuration.RegistryFieldDef> AddedSourceColumns(Domain.Entities.Configuration.RegistryDef sources)
        => AddedColumns(ParentColumns, sources, SourceSystemColumns);

    /// <summary>Columns added to AIR_SOURCE_SUBSTANCES later, like <see cref="AddedSourceColumns"/>.</summary>
    /// <param name="substances">The AIR_SOURCE_SUBSTANCES definition.</param>
    public static IReadOnlyList<Domain.Entities.Configuration.RegistryFieldDef> AddedSubstanceColumns(Domain.Entities.Configuration.RegistryDef substances)
        => AddedColumns(ChildColumns, substances, SubstanceSystemColumns);

    private static List<Domain.Entities.Configuration.RegistryFieldDef> AddedColumns(
        IReadOnlyDictionary<string, string> builtIn, Domain.Entities.Configuration.RegistryDef table, HashSet<string> system)
    {
        ArgumentNullException.ThrowIfNull(table);
        return [.. table.Fields
            .Where(f => !system.Contains(f.Code) && !builtIn.Values.Contains(f.Code, StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f.Ordinal)
            .ThenBy(f => f.Id)];
    }

    private static Dictionary<string, string> WithAddedColumns(
        IReadOnlyDictionary<string, string> builtIn, Domain.Entities.Configuration.RegistryDef table, HashSet<string> system)
    {
        var result = new Dictionary<string, string>(builtIn, StringComparer.Ordinal);
        foreach (var field in AddedColumns(builtIn, table, system))
        {
            result.TryAdd(field.Code, field.Code);
        }

        return result;
    }

    /// <summary>Normalises a region or area code like the NAE tree does (upper case, letters and digits, others to <c>_</c>).</summary>
    /// <param name="text">Raw code.</param>
    /// <param name="emptyAs">Result for an empty value.</param>
    public static string NormalizeScope(string? text, string emptyAs)
    {
        var clean = NaeCodeNormalizer.Clean(text).ToUpperInvariant();
        if (clean.Length == 0)
        {
            return emptyAs;
        }

        return new string([.. clean.Select(c => char.IsLetterOrDigit(c) ? c : '_')]);
    }

    /// <summary>The published definition of the type.</summary>
    /// <param name="settings">Registries the lookups point at.</param>
    /// <param name="regionAsText">✎ AIR-ON-AIR: the Region registry does not exist — REGION is a Text column (fallback with a warning).</param>
    public static PermitTypeContent Content(AirLimitsSettings settings, bool regionAsText = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sources = new PermitTypeTableDto(
            SourcesTable,
            Names("Sources (NAE)", "Источники (НАЕ)", "Көздер (НАЕ)"),
            1,
            [
                Lookup(Nae, "NAE", "НАЕ", "НАЕ", "SharedRegistry", settings.NaeRegistryCode, null, key: true, 1),
                // ✎ AIR-ON-AIR: import scope columns; kind (Text / Registry / List) is editable on the type like any column.
                regionAsText
                    ? Text(Region, "Region", "Регион", required: true, 2)
                    : Lookup(Region, "Region", "Регион", "Аймақ", "SharedRegistry", settings.RegionRegistryCode, null, key: false, 2),
                Lookup(Area, "Area", "Район", "Аудан", "SharedRegistry", settings.AreaRegistryCode, null, key: false, 3, required: false),
                Text(SiteLabel, "Site / production", "Площадка / производство", false, 4),
                Text(ProductionCode, "Production code", "Код производства", false, 5),
                Text(SourceName, "Source name", "Наименование источника", true, 6),
                Num(Quantity, "Quantity, pcs", "Количество, шт.", 7),
                Num(HoursPerYear, "Hours per year", "Часов работы в году", 8),
                Text(PointName, "Emission point", "Точка выброса", false, 9),
                Text(SourceType, "Type (T / P1)", "Тип (Т / П1)", false, 10),
                Num(Height, "Height, m", "Высота, м", 11),
                Num(Diameter, "Diameter, m", "Диаметр, м", 12),
                Num(Velocity, "Gas velocity", "Скорость газовоздушной смеси", 13),
                Num(Volume, "Gas volume", "Объем газовоздушной смеси", 14),
                Num(Temperature, "Gas temperature", "Температура смеси", 15),
                Num(X1, "X1", "X1", 16),
                Num(Y1, "Y1", "Y1", 17),
                Num(X2, "X2", "X2", 18),
                Num(Y2, "Y2", "Y2", 19),
            ],
            []);

        var substances = new PermitTypeTableDto(
            SubstancesTable,
            Names("Substances of sources", "Вещества источников", "Көздердің заттары"),
            2,
            [
                Lookup(Source, "Source", "Источник", "Көз", "SamePermitTable", null, SourcesTable, key: true, 1),
                Lookup(Substance, "Substance", "Вещество", "Зат", "SharedRegistry", settings.SubstanceRegistryCode, null, key: true, 2),
                Num(GramsPerSecond, "g/s", "г/с", 3),
                Num(MgPerNm3, "mg/nm3", "мг/нм3", 4),
                Num(TonsPerYear, "t/year", "т/год", 5),
            ],
            []);

        return new PermitTypeContent(
            Names("Air emission permit", "Разрешение на выбросы в атмосферу", "Атмосфераға шығарындыларға рұқсат"),
            "Air",
            [sources, substances],
            []);
    }

    private static Dictionary<string, string> Names(string en, string ru, string kz)
        => new() { ["en"] = en, ["ru"] = ru, ["kz"] = kz };

    private static PermitTypeColumnDto Text(string code, string en, string ru, bool required, int order)
        => new(code, Names(en, ru, en), "String", null, required, false, null, null, null, null, null, null, null, order);

    private static PermitTypeColumnDto Num(string code, string en, string ru, int order)
        => new(code, Names(en, ru, en), "Decimal", null, false, false, null, null, null, null, null, null, null, order);

    private static PermitTypeColumnDto Lookup(
        string code, string en, string ru, string kz, string scope, string? registry, string? sibling, bool key, int order, bool required = true)
        => new(code, Names(en, ru, kz), "Lookup", null, required, key, null, null, null, scope, registry, sibling, null, order);

    /// <summary>✎ AIR-ON-AIR: entries of the default Area registry (<see cref="DefaultAreaRegistry"/>): code → en / ru / kz.</summary>
    public static IReadOnlyList<(string Code, string En, string Ru, string Kz)> AreaEntries { get; } =
    [
        ("OFFSHORE", "Offshore", "Морской", "Теңіздегі"),
        ("ONSHORE", "Onshore", "Наземный", "Құрлықтағы"),
    ];

    /// <summary>Content of the AIR type with the two sources tables appended (when absent); other tables and rules untouched.</summary>
    /// <param name="current">Current content of the type.</param>
    /// <param name="settings">Registries the lookups point at.</param>
    /// <param name="regionAsText">The Region registry does not exist: REGION as a Text column.</param>
    /// <returns>The merged content, or <c>null</c> when both tables are already there.</returns>
    public static PermitTypeContent? WithSourcesTables(PermitTypeContent current, AirLimitsSettings settings, bool regionAsText = false)
    {
        ArgumentNullException.ThrowIfNull(current);
        var ours = Content(settings, regionAsText).Tables;
        var missing = ours
            .Where(t => !current.Tables.Any(c => string.Equals(c.Code, t.Code, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (missing.Count == 0)
        {
            return null;
        }

        var next = current.Tables.Count == 0 ? 1 : current.Tables.Max(t => t.Order) + 1;
        var appended = missing.Select((t, i) => t with { Order = next + i });
        return current with { Tables = [.. current.Tables, .. appended] };
    }
}
