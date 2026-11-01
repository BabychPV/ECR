// src/Ecr.Application/Permits/AirLimits/AirLimitsSchema.cs
using Ecr.Application.Permits.Dto;
using Ecr.Application.Registries.Nae;

namespace Ecr.Application.Permits.AirLimits;

/// <summary>Registry codes the air-limits permit points at (configurable; tests use their own).</summary>
/// <param name="NaeRegistryCode">NAE tree registry.</param>
/// <param name="SubstanceRegistryCode">Substance registry (records <c>E&lt;code&gt;</c> with field <c>SUBSTANCE_CODE</c>).</param>
public sealed record AirLimitsSettings(string NaeRegistryCode, string SubstanceRegistryCode)
{
    /// <summary>Production defaults.</summary>
    public static AirLimitsSettings Default { get; } = new(NaeTreeSchema.RegistryCode, AirLimitsSchema.DefaultSubstanceRegistry);
}

/// <summary>Fixed names of the "Air Limits Import" permit type (NAE plan N3).</summary>
public static class AirLimitsSchema
{
    /// <summary>Permit type code.</summary>
    public const string TypeCode = "AIR_LIMITS";

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
    public static PermitTypeContent Content(AirLimitsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sources = new PermitTypeTableDto(
            SourcesTable,
            Names("Sources (NAE)", "Источники (НАЕ)", "Көздер (НАЕ)"),
            1,
            [
                Lookup(Nae, "NAE", "НАЕ", "НАЕ", "SharedRegistry", settings.NaeRegistryCode, null, key: true, 1),
                Text(Region, "Region", "Регион", required: true, 2),
                Text(Area, "Area", "Район", required: false, 3),
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
            Names("Air Limits Import", "Импорт лимитов воздуха", "Ауа лимиттерін импорттау"),
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
        string code, string en, string ru, string kz, string scope, string? registry, string? sibling, bool key, int order)
        => new(code, Names(en, ru, kz), "Lookup", null, true, key, null, null, null, scope, registry, sibling, null, order);
}
