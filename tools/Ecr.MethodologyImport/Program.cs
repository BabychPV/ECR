using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using Ecr.MethodologyImport;
using Ecr.MethodologyImport.Analysis;
using Ecr.MethodologyImport.Export;
using Ecr.MethodologyImport.Model;
using Ecr.MethodologyImport.Reading;
using Ecr.MethodologyImport.Reporting;

// Коди виходу: 0 — блокерів немає; 2 — є блокери (нерезолвні посилання, цикли, нічого не розпізнано);
// 1 — помилка запуску або вхідного файла. Запису в БД інструмент не робить.
const int Ok = 0;
const int UsageOrInputError = 1;
const int HasBlockers = 2;

Console.OutputEncoding = new UTF8Encoding(false);

if (args.Length < 2 || args[0] is not ("analyze" or "export" or "unresolved") || (args[1] == "--sql" && args.Length < 3))
{
    Console.Error.WriteLine("""
        Використання:
          Ecr.MethodologyImport analyze <AF.xml> [--json] [--top N] [--library Common] [--out report.json]
          Ecr.MethodologyImport export  <AF.xml> --out package.json [--library Common] [--allow-blockers]
          Ecr.MethodologyImport unresolved <AF.xml> [--out unresolved.md] [--library Common]

        Джерело — AF XML (перший аргумент) АБО дзеркала SQL старої системи (AF_Formulas, AF_Constants) замість файла:
          Ecr.MethodologyImport export --sql "<рядок з'єднання>" --out package.json [--timezone Asia/Atyrau] [--methodology <код>]
        --sql читає лише SELECT … WITH (NOLOCK) (намір ReadOnly, копія бази старої системи, не live) і складає ту саму
        модель, що й XML; дати дзеркала — локальний час майданчика, --timezone (за замовчуванням Asia/Atyrau) переводить
        їх у UTC-рядки AF. Далі — той самий аналіз, нормалізація й пакет.

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
var sqlMode = args[1] == "--sql";
var path = sqlMode ? string.Empty : args[1];
string? sqlConnection = sqlMode ? args[2] : null;
var siteTimeZone = LegacyMirrorReader.DefaultTimeZone;
string? onlyMethodology = null;
var json = false;
var allowBlockers = false;
var normalize = true;
var top = 20;
var library = MethodologyAnalyzer.DefaultLibrary;
string? outFile = null;

for (var i = sqlMode ? 3 : 2; i < args.Length; i++)
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
        case "--timezone" when i + 1 < args.Length && sqlMode:
            siteTimeZone = args[++i];
            break;
        case "--methodology" when i + 1 < args.Length && sqlMode:
            onlyMethodology = args[++i];
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

if (!sqlMode && !File.Exists(path))
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
    if (sqlMode)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(siteTimeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            Console.Error.WriteLine($"Невідомий часовий пояс: {siteTimeZone}");
            return UsageOrInputError;
        }

        var (formulaRows, constantRows) = LegacySqlMirrorSource.Read(sqlConnection!);
        var (mirrorModel, readStats, mirrorStats) = LegacyMirrorReader.Build(formulaRows, constantRows, zone, onlyMethodology);
        size = 0;
        Console.Error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Дзеркала: рядків формул {mirrorStats.FormulaRows}, констант {mirrorStats.ConstantRows}, пропущено {mirrorStats.SkippedRows}."));
        (model, report) = AnalyzeCommand.Analyze(mirrorModel, readStats, library, normalize);
    }
    else
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        size = stream.Length;
        (model, report) = AnalyzeCommand.Run(stream, library, normalize);
    }
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
catch (Microsoft.Data.SqlClient.SqlException ex)
{
    Console.Error.WriteLine($"Помилка читання дзеркал SQL: {ex.Message}");
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
