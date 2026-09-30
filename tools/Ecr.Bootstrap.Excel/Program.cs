using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecr.Domain.Enums;

namespace Ecr.Bootstrap.Excel;

/// <summary>
/// Імпорт структури шаблону з Excel (ФВ-2.10 bootstrap із чинного <c>.xlsm</c>,
/// ФВ-2.11 довільний <c>.xlsx</c>, ФВ-16.12 нерозпізнані одиниці — у звіт).
/// </summary>
/// <remarks>
/// За замовчуванням — <b>сухий прогін</b>: книга читається, звіт пишеться,
/// у систему не пишеться нічого. Запис — лише з <c>--apply</c>, лише за звіту
/// без помилок, і лише в нову версію-чернетку через HTTP API застосунку
/// (<see cref="TemplateApiWriter"/>).
///
/// ⚠ Лишається ручним (<c>tz/09</c> §9.4): розбір VBA <c>MapAttributes</c>
/// (<see cref="VbaMapAttributesParser"/>), рядкові формули, неоднозначні
/// одиниці. Усе це названо у звіті, а не вгадано.
/// </remarks>
internal static class Program
{
    private const string Usage = """
        Імпорт структури шаблону з Excel (.xlsx/.xlsm).

        Сухий прогін (нічого не пише):
          Ecr.Bootstrap.Excel --workbook книга.xlsx [--out-report звіт.md] [--out-plan план.json]
                              [--row-mode auto|fixed|dynamic] [--layout PerPeriodInstance|Static|MonthsInColumns|MonthsInRows]

        Запис у нову версію-чернетку (лише якщо у звіті немає помилок):
          ... --apply --api https://ecr.example --version 1.0.0.0 [--lang ru]
              (--template-id N | --template-code CODE [--template-name "Назва"])
              (--user ІМ'Я --password-env ЗМІННА | --windows)

        Коди виходу: 0 — успіх; 1 — у звіті є помилки (нічого не записано);
                     2 — неправильні аргументи; 3 — сервер відмовив під час запису.
        """;

    private static readonly JsonSerializerOptions PlanJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Точка входу.</summary>
    /// <param name="args">Аргументи командного рядка (див. <see cref="Usage"/>).</param>
    /// <returns>Код виходу.</returns>
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var parsed = CommandLine.Parse(args);
        if (parsed.Error is not null)
        {
            await Console.Error.WriteLineAsync(parsed.Error).ConfigureAwait(false);
            await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        return await RunAsync(parsed.Options!, Console.Out, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Виконує імпорт за розібраними аргументами.</summary>
    /// <param name="options">Аргументи.</param>
    /// <param name="log">Куди писати підсумок.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="handler">Транспорт HTTP; <c>null</c> — справжній із cookie.</param>
    /// <returns>Код виходу.</returns>
    internal static async Task<int> RunAsync(
        CommandLineOptions options, TextWriter log, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        StructurePlan plan;
        ImportReport report;
        await using (var file = File.OpenRead(options.Workbook))
        {
            (plan, report) = new StructureWorkbookReader().Read(
                file, Path.GetFileName(options.Workbook), new ReaderOptions(options.RowMode, options.Layout));
        }

        if (options.PlanPath is not null)
        {
            await File.WriteAllTextAsync(options.PlanPath, JsonSerializer.Serialize(plan, PlanJson), ct).ConfigureAwait(false);
        }

        string outcome;
        var exitCode = 0;

        if (!options.Apply)
        {
            outcome = report.HasErrors
                ? "сухий прогін; у звіті є помилки — запис буде заборонено"
                : "сухий прогін; нічого не записано";
            exitCode = report.HasErrors ? 1 : 0;
        }
        else if (report.HasErrors)
        {
            outcome = "запис НЕ виконано: у звіті є помилки";
            exitCode = 1;
        }
        else
        {
            (outcome, exitCode) = await ApplyAsync(options, plan, report, handler, ct).ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(options.ReportPath, report.ToMarkdown(plan, outcome), ct).ConfigureAwait(false);

        var tables = plan.Tables.ToList();
        await log.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"{outcome}. Аркушів {plan.Sheets.Count}, таблиць {tables.Count}, колонок {tables.Sum(t => t.Columns.Count)}, рядків {tables.Sum(t => t.Rows.Count)}; помилок {report.Count(IssueSeverity.Error)}, ручних рішень {report.Count(IssueSeverity.ManualReview)}. Звіт: {options.ReportPath}")).ConfigureAwait(false);
        return exitCode;
    }

    private static async Task<(string Outcome, int ExitCode)> ApplyAsync(
        CommandLineOptions options, StructurePlan plan, ImportReport report, HttpMessageHandler? handler, CancellationToken ct)
    {
        using var transport = handler is null
            ? new HttpClientHandler { CookieContainer = new CookieContainer(), UseDefaultCredentials = options.Windows }
            : null;
        using var http = new HttpClient(handler ?? transport!, disposeHandler: false)
        {
            BaseAddress = new Uri(options.Api!.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(2),
        };
        var writer = new TemplateApiWriter(http);

        try
        {
            if (options.Windows)
            {
                await writer.LoginWindowsAsync(ct).ConfigureAwait(false);
            }
            else
            {
                var password = Environment.GetEnvironmentVariable(options.PasswordEnv!) ?? string.Empty;
                await writer.LoginLocalAsync(options.User!, password, ct).ConfigureAwait(false);
            }

            var units = await writer.GetUnitsAsync(ct).ConfigureAwait(false);
            TemplateApiWriter.CheckUnits(plan, units, report);
            if (report.HasErrors)
            {
                return ("запис НЕ виконано: одиниць плану немає в каталозі сервера", 1);
            }

            var result = await writer.ApplyAsync(
                plan,
                new ApplyTarget(options.TemplateId, options.TemplateCode, options.TemplateName, options.Version!, options.Language),
                units,
                ct).ConfigureAwait(false);

            return (string.Create(
                CultureInfo.InvariantCulture,
                $"записано в шаблон {result.TemplateId}, версія-чернетка {result.VersionId} ({result.Requests} запитів); опублікуйте після звірки"), 0);
        }
        catch (ApplyFailedException ex)
        {
            report.Error("запис", ex.Message);
            var where = ex.VersionId is { } id
                ? string.Create(CultureInfo.InvariantCulture, $"; частково заповнена чернетка {id} — видаліть її або допишіть вручну")
                : string.Empty;
            return ($"запис перервано відмовою сервера{where}", 3);
        }
        catch (HttpRequestException ex)
        {
            report.Error("запис", $"сервер недоступний: {ex.Message}");
            return ("запис перервано: сервер недоступний", 3);
        }
    }
}

/// <summary>Розібрані аргументи командного рядка.</summary>
internal sealed record CommandLineOptions(
    string Workbook,
    string ReportPath,
    string? PlanPath,
    RowModeChoice RowMode,
    TableLayoutKind Layout,
    bool Apply,
    string? Api,
    string? Version,
    string Language,
    int? TemplateId,
    string? TemplateCode,
    string? TemplateName,
    string? User,
    string? PasswordEnv,
    bool Windows);

/// <summary>Розбір аргументів.</summary>
internal static class CommandLine
{
    /// <summary>Розбирає аргументи.</summary>
    /// <param name="args">Аргументи.</param>
    /// <returns>Налаштування або помилка.</returns>
    public static (CommandLineOptions? Options, string? Error) Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] flagNames = ["--apply", "--dry-run", "--windows"];
        string[] valueNames =
        [
            "--workbook", "--out-report", "--out-plan", "--row-mode", "--layout", "--api", "--version", "--lang",
            "--template-id", "--template-code", "--template-name", "--user", "--password-env",
        ];

        for (var i = 0; i < args.Length; i++)
        {
            if (flagNames.Contains(args[i], StringComparer.OrdinalIgnoreCase))
            {
                flags.Add(args[i]);
            }
            else if (valueNames.Contains(args[i], StringComparer.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                values[args[i]] = args[++i];
            }
            else
            {
                return (null, $"Невідомий аргумент «{args[i]}».");
            }
        }

        if (!values.TryGetValue("--workbook", out var workbook))
        {
            return (null, "Не задано --workbook.");
        }

        if (!File.Exists(workbook))
        {
            return (null, $"Файлу «{workbook}» немає.");
        }

        if (flags.Contains("--apply") && flags.Contains("--dry-run"))
        {
            return (null, "--apply і --dry-run взаємовиключні.");
        }

        var rowMode = RowModeChoice.Auto;
        if (values.TryGetValue("--row-mode", out var rm) && !Enum.TryParse(rm, true, out rowMode))
        {
            return (null, $"--row-mode: очікується auto, fixed або dynamic, а не «{rm}».");
        }

        var layout = TableLayoutKind.PerPeriodInstance;
        if (values.TryGetValue("--layout", out var lk) && (!Enum.TryParse(lk, true, out layout) || !Enum.IsDefined(layout)))
        {
            return (null, $"--layout: невідома розкладка «{lk}».");
        }

        int? templateId = null;
        if (values.TryGetValue("--template-id", out var tid))
        {
            if (!int.TryParse(tid, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedId))
            {
                return (null, $"--template-id: «{tid}» не є числом.");
            }

            templateId = parsedId;
        }

        var apply = flags.Contains("--apply");
        var windows = flags.Contains("--windows");
        values.TryGetValue("--api", out var api);
        values.TryGetValue("--version", out var version);
        values.TryGetValue("--template-code", out var templateCode);
        values.TryGetValue("--user", out var user);
        values.TryGetValue("--password-env", out var passwordEnv);

        if (apply)
        {
            if (api is null || !Uri.TryCreate(api, UriKind.Absolute, out _))
            {
                return (null, "--apply потребує --api з абсолютною адресою застосунку.");
            }

            if (version is null)
            {
                return (null, "--apply потребує --version (Major.Minor.Patch.Build).");
            }

            if ((templateId is null) == (templateCode is null))
            {
                return (null, "--apply потребує рівно одного з --template-id або --template-code.");
            }

            if (!windows && (user is null || passwordEnv is null))
            {
                return (null, "--apply потребує --windows або пари --user і --password-env (пароль — лише через змінну оточення).");
            }
        }

        var report = values.TryGetValue("--out-report", out var r) ? r : Path.ChangeExtension(workbook, ".import-report.md");
        values.TryGetValue("--out-plan", out var plan);
        values.TryGetValue("--template-name", out var templateName);

        return (new CommandLineOptions(
            workbook, report, plan, rowMode, layout, apply, api, version,
            values.TryGetValue("--lang", out var lang) ? lang : "ru",
            templateId, templateCode, templateName, user, passwordEnv, windows), null);
    }
}
