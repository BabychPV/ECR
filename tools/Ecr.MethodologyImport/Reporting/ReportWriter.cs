using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecr.MethodologyImport.Analysis;

namespace Ecr.MethodologyImport.Reporting;

public static class ReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Детермінований JSON (без часу й шляхів); <c>\n</c> як роздільник рядків незалежно від ОС.</summary>
    public static string ToJson(AnalysisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, JsonOptions).ReplaceLineEndings("\n");
    }

    public static string ToText(AnalysisReport r, int top, TimeSpan? elapsed = null, long? inputBytes = null)
    {
        ArgumentNullException.ThrowIfNull(r);
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();

        sb.AppendLine("=== Аналіз імпорту методологій з AF XML (сухий прогін, без запису) ===");
        if (inputBytes is not null && elapsed is not null)
        {
            sb.AppendLine(ci, $"Вхід: {inputBytes.Value / 1048576.0:F1} МБ; час розбору й аналізу: {elapsed.Value.TotalSeconds:F1} с");
        }

        sb.AppendLine();
        sb.AppendLine(ci, $"Методологій: {r.Methodologies} (версій: {r.MethodologyVersions})");
        sb.AppendLine(ci, $"Формул (версійних елементів): {r.Formulas}; констант (імен у версіях методологій): {r.Constants}; рядків значень констант: {r.ConstantValueRows}");
        sb.AppendLine(ci, $"Посилання на формули (!Ім'я): {r.FormulaReferences.Total}, резолвних {r.FormulaReferences.Resolved}, нерезолвних {r.FormulaReferences.Unresolved}, через межу методології {r.FormulaReferences.CrossMethodology}");
        sb.AppendLine(ci, $"Посилання на константи (CST.Ім'я): {r.ConstantReferences.Total}, резолвних {r.ConstantReferences.Resolved}, нерезолвних {r.ConstantReferences.Unresolved}, у бібліотеці {r.ConstantReferences.CrossMethodology}");
        sb.AppendLine(ci, $"Аргументи-параметри (@…): {r.ParameterArguments}; інші аргументи: {r.OtherArguments}");
        sb.AppendLine(ci, $"Trim: полів обрізано {r.TrimmedFields}, токенів аргументів з пробілами {r.TrimmedArgumentTokens}");
        sb.AppendLine(ci, $"Дублікати ключа формули: {r.DuplicateFormulaKeys}");
        sb.AppendLine(ci, $"Токени в тексті формули, яких немає в списку аргументів: {r.UndeclaredInTextTotal} (не блокер; CLR такі токени не підставляє)");
        sb.AppendLine();

        sb.AppendLine("По методологіях:");
        foreach (var m in r.ByMethodology)
        {
            foreach (var v in m.Versions)
            {
                sb.AppendLine(ci, $"  {m.Name}/{v.Version}: формул {v.Formulas}, констант {v.Constants}, посилань !: {v.FormulaRefs} (у Common {v.CrossFormulaRefs}), CST.: {v.ConstantRefs} (у Common {v.LibraryConstantRefs}), нерезолвних {v.Unresolved}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(ci, $"Нерезолвні посилання: {r.Unresolved.Count}, з них у формулах з IsAvailable=True: {r.UnresolvedInAvailableFormulas} (перші {Math.Min(top, r.Unresolved.Count)}):");
        foreach (var u in r.Unresolved.Take(top))
        {
            var prefix = u.Kind == ReferenceKind.Formula ? "!" : "CST.";
            sb.AppendLine(ci, $"  {u.Methodology}/{u.MethodologyVersion} · {u.Formula}/{u.FormulaVersion}: {prefix}{u.Token} — {u.Hint}");
        }

        if (r.UnresolvedTokens.Count > 0)
        {
            sb.AppendLine(ci, $"Різних нерезолвних токенів: {r.UnresolvedTokens.Count} (найчастіші):");
            foreach (var t in r.UnresolvedTokens.Take(top))
            {
                sb.AppendLine(ci, $"  {t.Token} × {t.Count}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(ci, $"Цикли посилань: {r.Cycles.Count}");
        foreach (var c in r.Cycles.Take(top))
        {
            sb.AppendLine(ci, $"  {string.Join(" → ", c.Formulas)}");
        }

        sb.AppendLine();
        sb.AppendLine("Читач AF XML:");
        var rd = r.Reader;
        sb.AppendLine(ci, $"  елементів {rd.Elements}, атрибутів {rd.Attributes}, елементів без імені {rd.ElementsWithoutName}, дублікатів атрибутів {rd.DuplicateAttributes}");
        sb.AppendLine(ci, $"  формул (версійних елементів) {rd.FormulaVersionElements}, контейнерів формул {rd.FormulaContainers}, визначень констант {rd.ConstantDefinitions}, елементів значень констант {rd.ConstantValueElements}");
        sb.AppendLine(ci, $"  пропущено в методологіях: {(rd.SkippedMethodologyElements.Count == 0 ? "немає" : string.Join(", ", rd.SkippedMethodologyElements.Select(kv => $"{kv.Key}×{kv.Value}")))}; елементів поза Methodologies: {rd.ElementsOutsideMethodologies}");
        sb.AppendLine(ci, $"  методологію взято зі шляху: {rd.MethodologyFromPath}; суперечність MInfo_* і шляху: {rd.MethodologyPathMismatch}; значень із ConfigString: {rd.ResolvedFromConfigString}; обчислюваних ConfigString (не використано): {rd.ComputedConfigStrings}");
        sb.AppendLine(ci, $"  непізнане (теги): {(rd.Unrecognized.Count == 0 ? "немає" : string.Join(", ", rd.Unrecognized.Select(kv => $"{kv.Key}×{kv.Value}")))}");

        sb.AppendLine();
        if (r.Blockers.Count == 0)
        {
            sb.AppendLine("Блокерів немає.");
        }
        else
        {
            sb.AppendLine("БЛОКЕРИ:");
            foreach (var b in r.Blockers)
            {
                sb.AppendLine("  " + b);
            }
        }

        return sb.ToString().ReplaceLineEndings("\n");
    }
}
