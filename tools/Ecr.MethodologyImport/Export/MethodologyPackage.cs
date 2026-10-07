using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecr.MethodologyImport.Analysis;
using Ecr.MethodologyImport.Model;

namespace Ecr.MethodologyImport.Export;

/// <summary>Формула в пакеті. Дати — рядки AF як є (без часового поясу й перерахунку).</summary>
public sealed record PackageFormula(
    string Name,
    string Version,
    string Arguments,
    string Text,
    string StartDate,
    string EndDate,
    bool IsAvailable,
    string Report,
    string ResultType = "Number");

public sealed record PackageConstantValue(
    string Category,
    string Version,
    string Value,
    string StartDate,
    string EndDate);

public sealed record PackageConstant(
    string Name,
    string Parameter,
    string Unit,
    IReadOnlyList<PackageConstantValue> Values);

public sealed record PackageVersion(
    string Version,
    IReadOnlyList<PackageFormula> Formulas,
    IReadOnlyList<PackageConstant> Constants);

public sealed record PackageMethodology(string Name, IReadOnlyList<PackageVersion> Versions);

/// <summary>
/// Пакет <c>ecr-methodology-package</c> v1 — вхід майбутнього <c>POST /methodologies/import?dryRun</c>
/// (лише чернетки). ⚠ Схема введена цим інструментом (у документах її не було) і документується в
/// FEATURE-HSE301-VIEW §11.6; ендпоінта-споживача в продукті ще немає. Непорожній <see cref="Blockers"/> =
/// імпортер ЗОБОВ'ЯЗАНИЙ відмовити. Без часу створення й шляхів середовища: той самий вхід → той самий файл.
/// </summary>
public sealed record MethodologyPackage(
    string Format,
    int Version,
    string Library,
    IReadOnlyList<PackageMethodology> Methodologies,
    IReadOnlyList<string> Blockers)
{
    public const string FormatName = "ecr-methodology-package";
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static MethodologyPackage From(MethodologyModel model, AnalysisReport report, string library)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(report);

        var shapes = FormulaTypeInference.Infer(model, library);
        var formulas = model.Formulas.ToLookup(f => (f.Methodology, f.MethodologyVersion));
        var constants = model.Constants.ToLookup(c => (c.Methodology, c.MethodologyVersion));
        var keys = formulas.Select(g => g.Key).Concat(constants.Select(g => g.Key)).Distinct().ToList();

        var methodologies = keys
            .GroupBy(k => k.Item1, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PackageMethodology(
                g.Key,
                g.Select(k => k.Item2).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal)
                    .Select(v => new PackageVersion(
                        v,
                        formulas[(g.Key, v)]
                            .OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.Version, StringComparer.Ordinal)
                            .Select(f => new PackageFormula(
                                f.Name, f.Version, f.Arguments, f.Text, f.StartDate, f.EndDate, f.Available, f.Report,
                                shapes[f.Key].Kind == FormulaResultKind.Text ? "Text" : "Number"))
                            .ToList(),
                        constants[(g.Key, v)]
                            .GroupBy(c => c.Name, StringComparer.Ordinal)
                            .OrderBy(cg => cg.Key, StringComparer.Ordinal)
                            .Select(cg => new PackageConstant(
                                cg.Key,
                                cg.Select(c => c.Parameter).FirstOrDefault(p => p.Length > 0) ?? string.Empty,
                                CanonicalUnit(cg.Select(c => c.Unit).FirstOrDefault(u => u.Length > 0)),
                                cg.Where(c => c.HasValue)
                                    .OrderBy(c => c.Category, StringComparer.Ordinal).ThenBy(c => c.Version, StringComparer.Ordinal)
                                    .Select(c => new PackageConstantValue(c.Category, c.Version, c.Value, c.StartDate, c.EndDate))
                                    .ToList()))
                            .ToList()))
                    .ToList()))
            .ToList();

        return new MethodologyPackage(FormatName, CurrentVersion, library, methodologies, report.Blockers);
    }

    /// <summary>Код каталогу ECR, якщо одиницю AF зведено; інакше рядок AF як є (нерезолвну звітує analyze, сервер — <c>unitUnknown</c>).</summary>
    private static string CanonicalUnit(string? raw)
    {
        var r = UnitCanonicalizer.Resolve(raw);
        return r.Code ?? (raw ?? string.Empty);
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions).ReplaceLineEndings("\n");
}
