using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using Ecr.MethodologyImport;
using Ecr.MethodologyImport.Analysis;
using Ecr.MethodologyImport.Export;
using Ecr.MethodologyImport.Model;
using Ecr.MethodologyImport.Reporting;

// Коди виходу: 0 — блокерів немає; 2 — є блокери (нерезолвні посилання, цикли, нічого не розпізнано);
// 1 — помилка запуску або вхідного файла. Запису в БД інструмент не робить.
const int Ok = 0;
const int UsageOrInputError = 1;
const int HasBlockers = 2;

Console.OutputEncoding = new UTF8Encoding(false);

if (args.Length < 2 || args[0] is not ("analyze" or "export" or "unresolved"))
{
    Console.Error.WriteLine("""
        Використання:
          Ecr.MethodologyImport analyze <AF.xml> [--json] [--top N] [--library Common] [--out report.json]
          Ecr.MethodologyImport export  <AF.xml> --out package.json [--library Common] [--allow-blockers]
          Ecr.MethodologyImport unresolved <AF.xml> [--out unresolved.md] [--library Common]

        analyze — сухий прогін: розбір AF XML (потоково), Trim, резолвінг !Формула і CST.Константа
        (власна версія → бібліотека), цикли. Код виходу 2, якщо є блокери. Запису в БД немає.
        export  — пакет ecr-methodology-package v1 (JSON) без запису в БД. За наявності блокерів пакет НЕ
        пишеться (код 2); --allow-blockers пише його разом зі списком блокерів (імпортер має відмовити).
        unresolved — нерезолвні посилання з категорією й рекомендацією по кожному (Markdown для методолога).
        Код виходу 2, якщо є блокери. Запису в БД немає.
        """);
    return UsageOrInputError;
}

var command = args[0];
var path = args[1];
var json = false;
var allowBlockers = false;
var normalize = true;
var top = 20;
var library = MethodologyAnalyzer.DefaultLibrary;
string? outFile = null;

for (var i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--json":
            json = true;
            break;
        case "--no-normalize":
            normalize = false;
            break;
        case "--allow-blockers":
            allowBlockers = true;
            break;
        case "--top" when i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var n) && n > 0:
            top = n;
            i++;
            break;
        case "--library" when i + 1 < args.Length:
            library = args[++i];
            break;
        case "--out" when i + 1 < args.Length:
            outFile = args[++i];
            break;
        default:
            Console.Error.WriteLine($"Невідомий або неповний параметр: {args[i]}");
            return UsageOrInputError;
    }
}

if (command == "export" && outFile is null)
{
    Console.Error.WriteLine("export потребує --out <package.json>.");
    return UsageOrInputError;
}

if (!File.Exists(path))
{
    Console.Error.WriteLine($"Файл не знайдено: {path}");
    return UsageOrInputError;
}

var clock = Stopwatch.StartNew();
MethodologyModel model;
AnalysisReport report;
long size;
try
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
    size = stream.Length;
    (model, report) = AnalyzeCommand.Run(stream, library, normalize);
}
catch (XmlException ex)
{
    Console.Error.WriteLine($"Некоректний XML (рядок {ex.LineNumber}, позиція {ex.LinePosition}): {ex.Message}");
    return UsageOrInputError;
}
catch (IOException ex)
{
    Console.Error.WriteLine($"Помилка читання файла: {ex.Message}");
    return UsageOrInputError;
}

clock.Stop();

if (command == "export")
{
    if (report.HasBlockers && !allowBlockers)
    {
        Console.Error.WriteLine("Пакет НЕ записано: є блокери (див. `analyze`), --allow-blockers запише пакет разом із ними.");
        foreach (var b in report.Blockers)
        {
            Console.Error.WriteLine("  " + b);
        }

        return HasBlockers;
    }

    var package = MethodologyPackage.From(model, report, library);
    File.WriteAllText(outFile!, package.ToJson() + "\n", new UTF8Encoding(false));
    Console.Out.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"Пакет {MethodologyPackage.FormatName} v{MethodologyPackage.CurrentVersion} записано: методологій {package.Methodologies.Count}, формул {report.Formulas}, констант {report.Constants}, блокерів {package.Blockers.Count}."));
    return report.HasBlockers ? HasBlockers : Ok;
}

if (command == "unresolved")
{
    var markdown = UnresolvedReferenceReport.ToMarkdown(UnresolvedReferenceReport.Recommend(model, report));
    if (outFile is not null)
    {
        File.WriteAllText(outFile, markdown, new UTF8Encoding(false));
    }

    Console.Out.Write(markdown);
    return report.HasBlockers ? HasBlockers : Ok;
}

if (outFile is not null)
{
    File.WriteAllText(outFile, ReportWriter.ToJson(report) + "\n", new UTF8Encoding(false));
}

Console.Out.Write(json
    ? ReportWriter.ToJson(report) + "\n"
    : ReportWriter.ToText(report, top, clock.Elapsed, size));

return report.HasBlockers ? HasBlockers : Ok;
