namespace Ecr.Application.RegistryImport;

/// <summary>
/// Profiles shipped with the system (seeded into <c>cfg.RegistryImportProfile</c> by the permit lane, N3).
/// </summary>
public static class BuiltInImportProfiles
{
    /// <summary>Code of the ERA table 3.3 profile.</summary>
    public const string EraTable33Code = "ERA_TABLE_33";

    /// <summary>
    /// "Parameters of emissions of pollutants into the atmosphere" (ERA v3.0, table 3.3) - the file the legacy
    /// <c>HSE 401 Import</c> VBA reads. Parent = emission source (NAE), child = substance of that source.
    /// </summary>
    /// <remarks>
    /// Source-level columns (A-T) are filled down within their vertical merge only; substance columns (W-AB) never are.
    /// Not imported: workshop (B), gas cleaning (R-V, empty in practice), year of NDV (AB).
    /// </remarks>
    public static RegistryImportSpec EraTable33 { get; } = BuildEraTable33();

    private static RegistryImportSpec BuildEraTable33()
    {
        static ImportFieldSpec Source(
            string name, string pattern, ImportParser parser = ImportParser.Text, bool required = false, bool nonEmpty = false,
            bool nonNegative = false, IReadOnlyList<string>? unit = null, string? reference = null)
            => new()
            {
                Name = name,
                Level = ImportLevel.Parent,
                Patterns = [pattern],
                UnitQualifier = unit ?? [],
                Parser = parser,
                Required = required,
                NonEmpty = nonEmpty,
                NonNegative = nonNegative,
                FillDown = FillDown.Merge,
                Reference = reference,
            };

        static ImportFieldSpec Substance(
            string name, string pattern, ImportParser parser = ImportParser.Text, bool required = false,
            IReadOnlyList<string>? unit = null, string? reference = null, bool nonNegative = false, bool warnIfEmpty = false)
            => new()
            {
                Name = name,
                WarnIfEmpty = warnIfEmpty,
                Level = ImportLevel.Child,
                Patterns = [pattern],
                UnitQualifier = unit ?? [],
                Parser = parser,
                Required = required,
                NonNegative = nonNegative,
                FillDown = FillDown.None,
                Reference = reference,
            };

        return new RegistryImportSpec
        {
            Sheet = null,
            GroupRows = new GroupRowSpec { Column = 1, MinMergedColumns = 10 },
            ParentKeyField = "naeCode",
            ChildKeyField = "substanceCode",
            Fields =
            [
                Source("productionCode", "Произ-водство"),
                Source("sourceName", "Источник выделения загрязняющих веществ…Наименование", required: true, nonEmpty: true),
                Source("quantity", "Количество, шт.", ImportParser.Decimal, nonNegative: true),
                Source("hoursPerYear", "Число часов работы в году", ImportParser.Decimal, nonNegative: true),
                Source("emissionPointName", "Наименование источника выброса вредных веществ"),
                Source("naeCode", "Номер источника выбросов на карте-схеме", ImportParser.Code, required: true, nonEmpty: true, reference: INaeResolver.NaeKind),
                new ImportFieldSpec
                {
                    Name = "sourceType",
                    Level = ImportLevel.Parent,
                    Patterns = ["Т И П ИЗА"],
                    Parser = ImportParser.Choice,
                    FillDown = FillDown.Merge,
                    Choices = new Dictionary<string, string> { ["Т"] = "T", ["П1"] = "P1" },
                },
                Source("height", "Высота источника выбросов, м", ImportParser.Decimal, required: true, nonEmpty: true, nonNegative: true),
                Source("diameter", "Диаметр устья трубы, м", ImportParser.Decimal, nonNegative: true),
                Source("velocity", "Скорость", ImportParser.Decimal, nonNegative: true),
                Source("volume", "Объем смеси", ImportParser.Decimal, nonNegative: true),
                Source("temperature", "Температура смеси", ImportParser.Decimal),
                Source("x1", "Координаты источника", ImportParser.Decimal, unit: ["Х1|X1"]),
                Source("y1", "Координаты источника", ImportParser.Decimal, unit: ["Y1|У1"]),
                Source("x2", "Координаты источника", ImportParser.Decimal, unit: ["Х2|X2"]),
                Source("y2", "Координаты источника", ImportParser.Decimal, unit: ["Y2|У2"]),
                Substance("substanceCode", "Код вещества", ImportParser.Code, required: true, reference: ISubstanceResolver.SubstanceKind),
                Substance("substanceName", "Наименование вещества"),
                Substance("gramsPerSecond", "Выбросы загрязняющего вещества", ImportParser.Decimal, unit: ["г/с"], nonNegative: true),
                Substance("mgPerNm3", "Выбросы загрязняющего вещества", ImportParser.Decimal, unit: ["мг/нм3"], nonNegative: true),
                Substance("tonsPerYear", "Выбросы загрязняющего вещества", ImportParser.Decimal, required: true, unit: ["т/год"], nonNegative: true, warnIfEmpty: true),
            ],
        }.Validated();
    }
}
