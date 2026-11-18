using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Graph;
using Ecr.Expressions.Parsing;
using Ecr.Infrastructure.Expressions;
using Ecr.Infrastructure.Persistence;
using Ecr.MethodologyScriptMigration.Conversion;
using Ecr.MethodologyScriptMigration.Loading;
using Ecr.Scripting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.MethodologyScriptMigration.Cli;

/// <summary>
/// Команди консолі: розбір аргументів руками (як у <c>tools/Ecr.MethodologyImport</c>) і виконання.
/// Код виходу: 0 — гаразд, 1 — невдалі версії, 2 — запуск/середовище.
/// </summary>
public static class MigrationCli
{
    /// <summary>Усе гаразд.</summary>
    public const int Ok = 0;

    /// <summary>Є невдалі версії (перетворення, запис, пакування).</summary>
    public const int Failures = 1;

    /// <summary>Помилка запуску чи середовища.</summary>
    public const int UsageOrEnvironment = 2;

    /// <summary>Прапорець, що підтверджує: вказана база — копія, а не жива.</summary>
    public const string CopyFlag = "--i-know-this-is-a-copy";

    /// <summary>Слова в імені бази, що дозволяють <c>shadow</c> без <see cref="CopyFlag"/>.</summary>
    private static readonly string[] SafeNameWords = ["copy", "test", "upg", "dev", "stand"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const string Usage = """
        Використання:
          Ecr.MethodologyScriptMigration convert --connection <рядок> --out <тека> [--methodology <код>]
          Ecr.MethodologyScriptMigration shadow  --connection <рядок> --in <тека> [--i-know-this-is-a-copy]
          Ecr.MethodologyScriptMigration pack    --connection <рядок> --in <тека> --out <package.json>
          Ecr.MethodologyScriptMigration parity  (ще не реалізовано)

        convert — для кожної версії рівня Configuration з рушієм Formulas пише <Код>/<Версія>.ecrs і convert-report.json.
        shadow  — записує тексти як тіньові (SetScript) у базу; лише в базу з "copy|test|upg|dev|stand" в імені
                  або з прапорцем --i-know-this-is-a-copy.
        pack    — package.json: код методології, версія, дата чинності, текст, відбиток джерела.
        Коди виходу: 0 — гаразд; 1 — є невдалі версії; 2 — помилка запуску чи середовища.
        """;

    /// <summary>Виконує команду.</summary>
    /// <param name="args">Аргументи командного рядка.</param>
    /// <param name="output">Стандартний вивід.</param>
    /// <param name="error">Потік помилок.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return UsageOrEnvironment;
        }

        var command = args[0];
        if (command == "parity")
        {
            await error.WriteLineAsync(
                "parity ще не реалізовано: порівняння старого модуля й модуля скрипта живе в іншому проєкті "
                + "і буде підключене пізніше (план, розд. 8.4).").ConfigureAwait(false);
            return UsageOrEnvironment;
        }

        if (command is not ("convert" or "shadow" or "pack"))
        {
            await error.WriteLineAsync($"Невідома команда: {command}").ConfigureAwait(false);
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return UsageOrEnvironment;
        }

        var parsed = Options.Parse(command, args, error);
        if (parsed is null)
        {
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return UsageOrEnvironment;
        }

        try
        {
            return command switch
            {
                "convert" => await ConvertAsync(parsed, output, error, ct).ConfigureAwait(false),
                "shadow" => await ShadowAsync(parsed, output, error, ct).ConfigureAwait(false),
                _ => await PackAsync(parsed, output, error, ct).ConfigureAwait(false),
            };
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException or UnauthorizedAccessException
                                       or ArgumentException)
        {
            await error.WriteLineAsync($"Помилка середовища: {ex.Message}").ConfigureAwait(false);
            return UsageOrEnvironment;
        }
    }

    // ---- convert ----

    private static async Task<int> ConvertAsync(Options options, TextWriter output, TextWriter error, CancellationToken ct)
    {
        _ = error;
        Directory.CreateDirectory(options.Out!);
        await using var db = CreateContext(options.Connection!, readOnly: true);
        var loader = new VersionSourceLoader(db, CreateFormulaEngine());

        var versions = await loader.ListAsync(options.Methodology, ct).ConfigureAwait(false);
        if (options.Methodology is not null && versions.Count == 0)
        {
            await error.WriteLineAsync($"Методологію «{options.Methodology}» не знайдено або в неї немає версій.").ConfigureAwait(false);
            return UsageOrEnvironment;
        }

        var entries = new List<ReportEntry>();
        foreach (var reference in versions)
        {
            ct.ThrowIfCancellationRequested();
            entries.Add(await ConvertOneAsync(loader, reference, options.Out!, ct).ConfigureAwait(false));
        }

        var report = new ConvertReport(
            entries.Count(e => e.Status == ReportStatus.Converted),
            entries.Count(e => e.Status == ReportStatus.Failed),
            entries.Count(e => e.Status == ReportStatus.Skipped),
            entries);
        await File.WriteAllTextAsync(
                Path.Combine(options.Out!, "convert-report.json"),
                JsonSerializer.Serialize(report, JsonOptions) + "\n",
                new UTF8Encoding(false),
                ct)
            .ConfigureAwait(false);

        await output.WriteLineAsync(
            $"convert: перетворено {report.Converted}, невдало {report.Failed}, пропущено {report.Skipped}. Звіт: convert-report.json").ConfigureAwait(false);
        foreach (var failed in entries.Where(e => e.Status == ReportStatus.Failed))
        {
            await output.WriteLineAsync(
                $"  FAIL {failed.Methodology} {failed.Version}: "
                + string.Join("; ", failed.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Code))).ConfigureAwait(false);
        }

        return report.Failed > 0 ? Failures : Ok;
    }

    private static async Task<ReportEntry> ConvertOneAsync(
        VersionSourceLoader loader, VersionRef reference, string outDirectory, CancellationToken ct)
    {
        ReportEntry Skipped(string reason) => new(
            reference.MethodologyCode, reference.Version, reference.VersionId, ReportStatus.Skipped, null, null, reason, []);

        if (reference.Level == CalculationLevel.Module)
        {
            return Skipped("Level Module");
        }

        if (reference.Engine == MethodologyEngine.Script)
        {
            return Skipped("already Script");
        }

        if (reference.EffectiveFrom is null)
        {
            return Skipped("draft without EffectiveFrom");
        }

        var issues = new List<ConversionIssue>();
        string? text = null;
        string? fingerprint = null;
        try
        {
            var loaded = await loader.LoadAsync(reference.VersionId, ct).ConfigureAwait(false);
            issues.AddRange(loaded.Issues);
            fingerprint = Fingerprint.ComputeHex(loaded.Source);
            if (!loaded.HasErrors)
            {
                var result = ScriptConverter.Convert(loaded.Source);
                issues.AddRange(result.Issues);
                text = result.Text;
            }
        }
        catch (Exception ex) when (ex is DomainException or InvalidOperationException or DbUpdateException or SqlException)
        {
            issues.Add(new ConversionIssue(IssueSeverity.Error, "loadFailed", ex.Message));
        }

        var path = ScriptFiles.PathOf(outDirectory, reference.MethodologyCode, reference.Version);
        if (text is null || issues.Any(i => i.Severity == IssueSeverity.Error))
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return new ReportEntry(
                reference.MethodologyCode, reference.Version, reference.VersionId, ReportStatus.Failed, null, fingerprint, null, issues);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new UTF8Encoding(false).GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal)), ct)
            .ConfigureAwait(false);
        return new ReportEntry(
            reference.MethodologyCode, reference.Version, reference.VersionId, ReportStatus.Converted,
            ScriptFiles.RelativeName(reference.MethodologyCode, reference.Version), fingerprint, null, issues);
    }

    // ---- shadow ----

    private static async Task<int> ShadowAsync(Options options, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var database = new SqlConnectionStringBuilder(options.Connection).InitialCatalog;
        if (!options.IKnowThisIsACopy && !LooksSafe(database))
        {
            await error.WriteLineAsync(
                $"shadow відмовлено: ім'я бази «{database}» не містить жодного з [{string.Join(", ", SafeNameWords)}]. "
                + $"Якщо це справді копія, додайте {CopyFlag}.").ConfigureAwait(false);
            return UsageOrEnvironment;
        }

        var files = ScriptFiles.Enumerate(options.In!);
        List<VersionRef> versions;
        await using (var listDb = CreateContext(options.Connection!, readOnly: true))
        {
            versions = [.. await new VersionSourceLoader(listDb, CreateFormulaEngine()).ListAsync(null, ct).ConfigureAwait(false)];
        }

        var failed = 0;
        var written = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var reference = ScriptFiles.Find(versions, file);
            if (reference is null)
            {
                failed++;
                await error.WriteLineAsync($"  FAIL {file.RelativeName}: версії в базі не знайдено (або назва неоднозначна).").ConfigureAwait(false);
                continue;
            }

            var text = await File.ReadAllTextAsync(file.FullPath, new UTF8Encoding(false), ct).ConfigureAwait(false);
            try
            {
                await using var db = CreateContext(options.Connection!, readOnly: false);
                var version = await db.MethodologyVersions.FirstAsync(v => v.Id == reference.VersionId, ct).ConfigureAwait(false);
                version.SetScript(text, ScriptCompiler.Hash(text));
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                written++;
            }
            catch (Exception ex) when (ex is DomainException or DbUpdateException or InvalidOperationException)
            {
                failed++;
                await error.WriteLineAsync($"  FAIL {file.RelativeName}: {ex.Message}").ConfigureAwait(false);
            }
        }

        await output.WriteLineAsync($"shadow: записано {written}, невдало {failed}.").ConfigureAwait(false);
        return failed > 0 ? Failures : Ok;
    }

    /// <summary>Чи містить ім'я бази одне зі «безпечних» слів (копія, тест, апгрейд, dev, стенд).</summary>
    /// <param name="databaseName">Ім'я бази з рядка підключення.</param>
    public static bool LooksSafe(string? databaseName)
        => !string.IsNullOrEmpty(databaseName)
           && SafeNameWords.Any(w => databaseName.Contains(w, StringComparison.OrdinalIgnoreCase));

    // ---- pack ----

    private static async Task<int> PackAsync(Options options, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var files = ScriptFiles.Enumerate(options.In!);
        await using var db = CreateContext(options.Connection!, readOnly: true);
        var loader = new VersionSourceLoader(db, CreateFormulaEngine());
        var versions = await loader.ListAsync(null, ct).ConfigureAwait(false);

        var entries = new List<PackageEntry>();
        var failed = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var reference = ScriptFiles.Find(versions, file);
            if (reference is null)
            {
                failed++;
                await error.WriteLineAsync($"  FAIL {file.RelativeName}: версії в базі не знайдено (або назва неоднозначна).").ConfigureAwait(false);
                continue;
            }

            var text = await File.ReadAllTextAsync(file.FullPath, new UTF8Encoding(false), ct).ConfigureAwait(false);
            var loaded = await loader.LoadAsync(reference.VersionId, ct).ConfigureAwait(false);
            entries.Add(new PackageEntry(
                reference.MethodologyCode,
                reference.Version,
                reference.EffectiveFrom?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                text,
                Fingerprint.ComputeHex(loaded.Source)));
        }

        if (failed > 0)
        {
            await error.WriteLineAsync($"pack: пакет НЕ записано, невдало {failed}.").ConfigureAwait(false);
            return Failures;
        }

        var package = new ScriptPackage(
            ScriptPackage.FormatName,
            ScriptPackage.CurrentVersion,
            [.. entries.OrderBy(e => e.MethodologyCode, StringComparer.Ordinal)
                .ThenBy(e => e.EffectiveFrom, StringComparer.Ordinal)
                .ThenBy(e => e.Version, StringComparer.Ordinal)]);
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.Out!));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(options.Out!, JsonSerializer.Serialize(package, JsonOptions) + "\n", new UTF8Encoding(false), ct)
            .ConfigureAwait(false);
        await output.WriteLineAsync($"pack: у пакеті {package.Entries.Count} версій → {options.Out}").ConfigureAwait(false);
        return Ok;
    }

    // ---- спільне ----

    /// <summary>Контекст бази за рядком підключення.</summary>
    /// <param name="connectionString">Рядок підключення.</param>
    /// <param name="readOnly">Без відстеження змін (лише читання).</param>
    public static EcrDbContext CreateContext(string connectionString, bool readOnly)
    {
        var builder = new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(connectionString, o => o.CommandTimeout(600));
        if (readOnly)
        {
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        }

        return new EcrDbContext(builder.Options);
    }

    /// <summary>Справжній рушій формул (потрібен лише для топологічного порядку чернеток).</summary>
    public static IFormulaEngine CreateFormulaEngine()
        => new FormulaEngine(new Parser(), new Evaluator(new FunctionRegistry()), new TopologicalSorter());

    private sealed record Options(
        string? Connection, string? Out, string? In, string? Methodology, bool IKnowThisIsACopy)
    {
        public static Options? Parse(string command, string[] args, TextWriter error)
        {
            string? connection = null;
            string? outPath = null;
            string? inPath = null;
            string? methodology = null;
            var copy = false;
            for (var i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--connection" when i + 1 < args.Length:
                        connection = args[++i];
                        break;
                    case "--out" when i + 1 < args.Length:
                        outPath = args[++i];
                        break;
                    case "--in" when i + 1 < args.Length:
                        inPath = args[++i];
                        break;
                    case "--methodology" when i + 1 < args.Length && command == "convert":
                        methodology = args[++i];
                        break;
                    case CopyFlag when command == "shadow":
                        copy = true;
                        break;
                    default:
                        error.WriteLine($"Невідомий або неповний параметр: {args[i]}");
                        return null;
                }
            }

            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(connection))
            {
                missing.Add("--connection");
            }

            if (command is "convert" or "pack" && string.IsNullOrWhiteSpace(outPath))
            {
                missing.Add("--out");
            }

            if (command is "shadow" or "pack" && string.IsNullOrWhiteSpace(inPath))
            {
                missing.Add("--in");
            }

            if (missing.Count > 0)
            {
                error.WriteLine($"{command} потребує: {string.Join(", ", missing)}");
                return null;
            }

            if (command is "shadow" or "pack" && !Directory.Exists(inPath))
            {
                error.WriteLine($"Теку не знайдено: {inPath}");
                return null;
            }

            return new Options(connection, outPath, inPath, methodology, copy);
        }
    }
}

/// <summary>Стан версії у звіті.</summary>
public enum ReportStatus
{
    /// <summary>Текст згенеровано.</summary>
    Converted,

    /// <summary>Перетворити не вдалося.</summary>
    Failed,

    /// <summary>Пропущено (рівень Module, уже Script, чернетка без дати).</summary>
    Skipped,
}

/// <summary>Запис звіту про одну версію.</summary>
/// <param name="Methodology">Код методології.</param>
/// <param name="Version">Версія.</param>
/// <param name="VersionId">Id версії в базі.</param>
/// <param name="Status">Стан.</param>
/// <param name="File">Файл відносно теки виводу.</param>
/// <param name="SourceFingerprint">Відбиток джерела (SHA-256, hex).</param>
/// <param name="Reason">Причина пропуску.</param>
/// <param name="Issues">Зауваження.</param>
public sealed record ReportEntry(
    string Methodology,
    string Version,
    int VersionId,
    ReportStatus Status,
    string? File,
    string? SourceFingerprint,
    string? Reason,
    IReadOnlyList<ConversionIssue> Issues);

/// <summary>Звіт <c>convert-report.json</c>.</summary>
/// <param name="Converted">Скільки перетворено.</param>
/// <param name="Failed">Скільки невдало.</param>
/// <param name="Skipped">Скільки пропущено.</param>
/// <param name="Versions">Версії.</param>
public sealed record ConvertReport(int Converted, int Failed, int Skipped, IReadOnlyList<ReportEntry> Versions);

/// <summary>Запис пакета: одна версія.</summary>
/// <param name="MethodologyCode">Код методології.</param>
/// <param name="Version">Версія.</param>
/// <param name="EffectiveFrom">Дата чинності.</param>
/// <param name="Text">Текст скрипта.</param>
/// <param name="SourceFingerprint">Відбиток джерела (SHA-256, hex).</param>
public sealed record PackageEntry(
    string MethodologyCode, string Version, string? EffectiveFrom, string Text, string SourceFingerprint);

/// <summary>Пакет <c>package.json</c>.</summary>
/// <param name="Format">Назва формату.</param>
/// <param name="FormatVersion">Версія формату.</param>
/// <param name="Entries">Версії.</param>
public sealed record ScriptPackage(string Format, int FormatVersion, IReadOnlyList<PackageEntry> Entries)
{
    /// <summary>Назва формату.</summary>
    public const string FormatName = "ecr-methodology-script-package";

    /// <summary>Поточна версія формату.</summary>
    public const int CurrentVersion = 1;
}
