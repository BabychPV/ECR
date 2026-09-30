using System.Text.RegularExpressions;
using Ecr.MethodologyImport.Reading;

namespace Ecr.MethodologyImport.Model;

/// <summary>
/// Складає <see cref="MethodologyModel"/> із записів <see cref="AfElementRecord"/>.
/// Класифікація за атрибутами (<c>FInfo_*</c> → формула, <c>CInfo_*</c> → константа) з запасним
/// виведенням імені/версії із шляху <c>Methodologies\&lt;М&gt;\&lt;ВерсіяМ&gt;\Formulas\&lt;Ф&gt;\&lt;ВерсіяФ&gt;</c>.
/// Усі текстові поля обрізаються (Trim): у даних AF трапляються пробіли в кінці імен і аргументів.
/// </summary>
public sealed partial class MethodologyModelBuilder
{
    private const string FormulaPrefix = "FInfo_";
    private const string ConstantPrefix = "CInfo_";
    private const string RootSegment = "Methodologies";

    private readonly List<FormulaDef> _formulas = [];
    private readonly List<ConstantDef> _constants = [];
    private readonly ModelBuildStats _stats = new();

    [GeneratedRegex(@"%((?:\.\.\\)*)Element%", RegexOptions.CultureInvariant)]
    private static partial Regex ElementTokenRegex();

    [GeneratedRegex(@"^V\d+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex VersionSegmentRegex();

    public void Add(AfElementRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var isFormula = record.Values.Keys.Concat(record.ConfigStrings.Keys)
            .Any(k => k.StartsWith(FormulaPrefix, StringComparison.Ordinal));
        var isConstant = record.Values.Keys.Concat(record.ConfigStrings.Keys)
            .Any(k => k.StartsWith(ConstantPrefix, StringComparison.Ordinal));

        if (isFormula)
        {
            _stats.FormulaElements++;
            AddFormula(record);
        }
        else if (isConstant)
        {
            _stats.ConstantElements++;
            AddConstant(record);
        }
        else
        {
            _stats.OtherElementsWithAttributes++;
        }
    }

    public MethodologyModel Build()
    {
        var formulas = _formulas
            .OrderBy(f => f.Methodology, StringComparer.Ordinal)
            .ThenBy(f => f.MethodologyVersion, StringComparer.Ordinal)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ThenBy(f => f.Version, StringComparer.Ordinal)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
        var constants = _constants
            .OrderBy(c => c.Methodology, StringComparer.Ordinal)
            .ThenBy(c => c.MethodologyVersion, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.Location, StringComparer.Ordinal)
            .ThenBy(c => c.Version, StringComparer.Ordinal)
            .ThenBy(c => c.Path, StringComparer.Ordinal)
            .ToList();

        _stats.DuplicateFormulaKeys = formulas
            .GroupBy(f => f.Key, StringComparer.Ordinal)
            .Sum(g => g.Count() - 1);

        return new MethodologyModel { Formulas = formulas, Constants = constants, BuildStats = _stats };
    }

    private void AddFormula(AfElementRecord rec)
    {
        var (methodology, methodologyVersion) = MethodologyOf(rec);
        var path = rec.Path;
        var last = path.Count > 0 ? path[^1] : string.Empty;
        var parent = path.Count > 1 ? path[^2] : string.Empty;

        var name = Field(rec, "FInfo_Name");
        var version = Field(rec, "FInfo_Version");
        if (name.Length == 0)
        {
            // Елемент «версія формули» називається V1, ім'я формули — у батька.
            name = VersionSegmentRegex().IsMatch(last) ? parent : last;
        }

        if (version.Length == 0 && VersionSegmentRegex().IsMatch(last))
        {
            version = last;
        }

        _formulas.Add(new FormulaDef(
            methodology,
            methodologyVersion,
            name,
            version,
            Field(rec, "FInfo_Arguments"),
            Field(rec, "FInfo_Text"),
            Field(rec, "FInfo_StartDate"),
            Field(rec, "FInfo_EndDate"),
            Field(rec, "FInfo_IsAvailable"),
            Field(rec, "FInfo_Report"),
            string.Join('\\', path)));
    }

    private void AddConstant(AfElementRecord rec)
    {
        var (methodology, methodologyVersion) = MethodologyOf(rec);
        var name = Field(rec, "CInfo_Name");
        if (name.Length == 0)
        {
            name = rec.Path.Count > 0 ? rec.Path[^1] : string.Empty;
        }

        _constants.Add(new ConstantDef(
            methodology,
            methodologyVersion,
            name,
            Field(rec, "CInfo_Parameter"),
            Field(rec, "CInfo_Value"),
            Field(rec, "CInfo_Version"),
            Field(rec, "CInfo_Location"),
            Field(rec, "CInfo_StartDate"),
            Field(rec, "CInfo_EndDate"),
            Field(rec, "CInfo_Unit"),
            string.Join('\\', rec.Path)));
    }

    private (string Methodology, string Version) MethodologyOf(AfElementRecord rec)
    {
        var name = Field(rec, "MInfo_Name");
        var version = Field(rec, "MInfo_Version");
        var fromPath = false;

        // У даних AF трапляються сміттєві значення на кшталт «..\..\..\..\|Status» — це не ім'я.
        if (name.Length == 0 || IsJunk(name))
        {
            name = SegmentAfterRoot(rec.Path, 1);
            fromPath = true;
        }

        if (version.Length == 0 || IsJunk(version))
        {
            version = SegmentAfterRoot(rec.Path, 2);
            fromPath = true;
        }

        if (fromPath)
        {
            _stats.MethodologyFromPath++;
        }

        return (name, version);
    }

    private static bool IsJunk(string value)
        => value.Contains('\\', StringComparison.Ordinal)
           || value.Contains('|', StringComparison.Ordinal)
           || value.Contains('%', StringComparison.Ordinal);

    private static string SegmentAfterRoot(IReadOnlyList<string> path, int offset)
    {
        for (var i = 0; i < path.Count; i++)
        {
            if (string.Equals(path[i], RootSegment, StringComparison.Ordinal))
            {
                return i + offset < path.Count ? path[i + offset] : string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>Значення атрибута після Trim: <c>Value</c>, інакше розкритий <c>ConfigString</c>.</summary>
    private string Field(AfElementRecord rec, string key)
    {
        if (rec.Values.TryGetValue(key, out var raw) && raw.Trim().Length > 0)
        {
            return Trimmed(raw);
        }

        if (rec.ConfigStrings.TryGetValue(key, out var config) && config.Trim().Length > 0)
        {
            var resolved = ResolveConfig(config, rec.Path);
            if (resolved is null)
            {
                _stats.UnresolvedConfigStrings++;
                return string.Empty;
            }

            _stats.ResolvedFromConfigString++;
            return Trimmed(resolved);
        }

        return string.Empty;
    }

    private string Trimmed(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length != value.Length)
        {
            _stats.TrimmedFields++;
        }

        return trimmed;
    }

    /// <summary>
    /// Розкриває <c>%Element%</c> (власне ім'я) і <c>%..\Element%</c> (ім'я предка); <c>null</c>, якщо
    /// після підстановки лишився невідомий токен <c>%…%</c>.
    /// </summary>
    internal static string? ResolveConfig(string config, IReadOnlyList<string> path)
    {
        var failed = false;
        var result = ElementTokenRegex().Replace(config, m =>
        {
            var ups = m.Groups[1].Value.Length / 3; // кожне «..\» — 3 символи
            var index = path.Count - 1 - ups;
            if (index < 0)
            {
                failed = true;
                return m.Value;
            }

            return path[index];
        });

        return failed || result.Contains('%', StringComparison.Ordinal) ? null : result;
    }
}
