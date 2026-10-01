using System.Text.RegularExpressions;
using Ecr.MethodologyImport.Reading;

namespace Ecr.MethodologyImport.Model;

/// <summary>
/// Складає <see cref="MethodologyModel"/> із записів <see cref="AfElementRecord"/>. Структура (звірена з
/// <c>ECR_01_Air.xml</c>): <c>Methodologies\&lt;М&gt;\&lt;ВерсіяМ&gt;\Formulas\&lt;Ф&gt;\&lt;ВерсіяФ&gt;</c> — формула;
/// <c>…\Constants\&lt;К&gt;</c> — визначення константи, <c>…\Constants\&lt;К&gt;\&lt;Категорія&gt;\&lt;Версія&gt;</c> — її
/// значення. Методологія може бути вкладеною (<c>Methodologies\EmissionCalculationWork\ECW_C05_…\ECW_C05_01</c>),
/// тому ім'я/версія беруться з <c>MInfo_*</c> (ConfigString <c>%..\..\..\Element%</c>), а не з фіксованих сегментів;
/// шлях — запасний варіант і перевірка. Усі текстові поля обрізаються (Trim): у даних AF трапляються пробіли
/// в кінці імен і аргументів.
/// </summary>
public sealed partial class MethodologyModelBuilder
{
    private const string RootSegment = "Methodologies";
    private const string FormulasFolder = "Formulas";
    private const string ConstantsFolder = "Constants";

    private readonly List<FormulaDef> _formulas = [];
    private readonly List<ConstantDef> _constants = [];
    private readonly ModelBuildStats _stats = new();

    [GeneratedRegex(@"%((?:\.\.\\)*)Element%", RegexOptions.CultureInvariant)]
    private static partial Regex ElementTokenRegex();

    public void Add(AfElementRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var path = record.Path;
        if (path.Count == 0 || !string.Equals(path[0], RootSegment, StringComparison.Ordinal))
        {
            _stats.ElementsOutsideMethodologies++;
            return;
        }

        var f = LastIndex(path, FormulasFolder);
        var c = LastIndex(path, ConstantsFolder);

        if (f > c && f >= 2)
        {
            AddFormulaLevel(record, f);
        }
        else if (c > f && c >= 2)
        {
            AddConstantLevel(record, c);
        }
        else
        {
            Skip(path.Contains("Rules", StringComparer.Ordinal) ? "Rules"
                : path.Contains("Settings", StringComparer.Ordinal) ? "Settings"
                : "(корінь методології)");
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
            .ThenBy(c => c.Category, StringComparer.Ordinal)
            .ThenBy(c => c.Version, StringComparer.Ordinal)
            .ThenBy(c => c.Path, StringComparer.Ordinal)
            .ToList();

        _stats.DuplicateFormulaKeys = formulas
            .GroupBy(f => f.Key, StringComparer.Ordinal)
            .Sum(g => g.Count() - 1);

        return new MethodologyModel { Formulas = formulas, Constants = constants, BuildStats = _stats };
    }

    private static int LastIndex(IReadOnlyList<string> path, string segment)
    {
        for (var i = path.Count - 1; i >= 0; i--)
        {
            if (string.Equals(path[i], segment, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private void Skip(string folder) => _stats.SkippedMethodologyElements[folder] = _stats.SkippedMethodologyElements.GetValueOrDefault(folder) + 1;

    private void AddFormulaLevel(AfElementRecord rec, int f)
    {
        var path = rec.Path;
        var hasBody = rec.Values.ContainsKey("FInfo_Text") || rec.Values.ContainsKey("FInfo_Arguments");

        if (path.Count == f + 3 && hasBody)
        {
            _stats.FormulaVersionElements++;
            var (methodology, methodologyVersion) = MethodologyOf(rec, f);
            _formulas.Add(new FormulaDef(
                methodology,
                methodologyVersion,
                path[f + 1],
                path[f + 2],
                Field(rec, "FInfo_Arguments"),
                Field(rec, "FInfo_Text"),
                Field(rec, "FInfo_StartDate"),
                Field(rec, "FInfo_EndDate"),
                Field(rec, "FInfo_IsAvailable"),
                Field(rec, "FInfo_Report"),
                string.Join('\\', path)));
        }
        else if (path.Count == f + 2)
        {
            _stats.FormulaContainers++;
        }
        else
        {
            Skip(FormulasFolder);
        }
    }

    private void AddConstantLevel(AfElementRecord rec, int c)
    {
        var path = rec.Path;
        var hasValue = rec.Values.ContainsKey("CInfo_Value");

        if (path.Count == c + 2)
        {
            _stats.ConstantDefinitions++;
        }
        else if (path.Count >= c + 3 && hasValue)
        {
            _stats.ConstantValueElements++;
        }
        else
        {
            Skip(ConstantsFolder);
            return;
        }

        var (methodology, methodologyVersion) = MethodologyOf(rec, c);
        _constants.Add(new ConstantDef(
            methodology,
            methodologyVersion,
            path[c + 1],
            path.Count > c + 2 ? path[c + 2] : string.Empty,
            path.Count > c + 3 ? path[c + 3] : string.Empty,
            hasValue,
            Field(rec, "CInfo_Value"),
            Field(rec, "CInfo_Parameter"),
            Field(rec, "CInfo_StartDate"),
            Field(rec, "CInfo_EndDate"),
            Field(rec, "CInfo_Unit"),
            string.Join('\\', path)));
    }

    /// <summary>
    /// Методологія й версія: <c>MInfo_Name</c>/<c>MInfo_Version</c> (розкриті ConfigString), інакше два сегменти
    /// перед папкою <c>Formulas</c>/<c>Constants</c>. Розбіжність зі шляхом лічиться окремо.
    /// </summary>
    private (string Methodology, string Version) MethodologyOf(AfElementRecord rec, int folderIndex)
    {
        var pathName = rec.Path[folderIndex - 2];
        var pathVersion = rec.Path[folderIndex - 1];
        var name = Field(rec, "MInfo_Name");
        var version = Field(rec, "MInfo_Version");

        if (name.Length == 0 || version.Length == 0)
        {
            _stats.MethodologyFromPath++;
        }
        else if (!string.Equals(name, pathName, StringComparison.Ordinal)
                 || !string.Equals(version, pathVersion, StringComparison.Ordinal))
        {
            _stats.MethodologyPathMismatch++;
        }

        return (name.Length > 0 ? name : pathName, version.Length > 0 ? version : pathVersion);
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
                _stats.ComputedConfigStrings++;
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
    /// Розкриває літерал String Builder <c>"%..\..\Element%";</c>: <c>%Element%</c> — власне ім'я елемента,
    /// <c>%..\Element%</c> — ім'я предка. <c>null</c>, якщо вираз не є простим літералом (посилання на
    /// атрибути <c>'…'</c>, конкатенація) або лишився невідомий токен.
    /// </summary>
    public static string? ResolveConfig(string config, IReadOnlyList<string> path)
    {
        var s = config.Trim().TrimEnd(';').Trim();
        if (s.Length < 2 || s[0] != '"' || s[^1] != '"')
        {
            return null;
        }

        s = s[1..^1];
        var failed = false;
        var result = ElementTokenRegex().Replace(s, m =>
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

        return failed || result.IndexOfAny(['%', '"', '\'', ';']) >= 0 ? null : result;
    }
}
