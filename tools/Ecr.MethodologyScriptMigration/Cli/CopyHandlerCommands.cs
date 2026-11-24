using System.Globalization;
using System.Text;
using System.Text.Json;
using Ecr.Application.Calculations.Scripting.Migration;
using Ecr.Application.Errors;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.MethodologyScriptMigration.Cli;

/// <summary>
/// ✎ D-571: команди <c>apply</c> і <c>switch</c> на КОПІЇ бази — ті самі обробники, що й ендпоінти
/// <c>POST api/v1/methodologies/script-migration/apply|switch</c> (відбиток джерела, компіляція, склад, золотий набір PSS-23,
/// цикл залежностей, журнал <c>aud.PublicationEvent</c>, D-568 поля читання), від імені наявного користувача копії з правом
/// <c>Calculation.Publish</c>. Потрібні для генеральної репетиції вікна на відновленій копії без паролів живих користувачів.
/// ⛔ Лише копія: ім'я бази з «copy|test|upg|dev|stand|ms» або прапорець <see cref="MigrationCli.CopyFlag"/>; на живу базу
/// перенесення йде ЛИШЕ через API (вхід користувача, журнал під його іменем).
/// </summary>
public static class CopyHandlerCommands
{
    /// <summary>Розбирає й виконує <c>apply</c> або <c>switch</c>.</summary>
    /// <param name="command">Команда.</param>
    /// <param name="args">Усі аргументи.</param>
    /// <param name="output">Вивід.</param>
    /// <param name="error">Помилки.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<int> RunAsync(string command, string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? connection = null, input = null, engine = null, reason = null, outPath = null;
        int? asUser = null;
        var dryRun = false;
        var copy = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--connection" when i + 1 < args.Length:
                    connection = args[++i];
                    break;
                case "--in" when i + 1 < args.Length:
                    input = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;
                case "--engine" when i + 1 < args.Length && command == "switch":
                    engine = args[++i];
                    break;
                case "--reason" when i + 1 < args.Length && command == "switch":
                    reason = args[++i];
                    break;
                case "--as-user" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var user))
                    {
                        await error.WriteLineAsync($"--as-user: очікується число, отримано {args[i]}").ConfigureAwait(false);
                        return MigrationCli.UsageOrEnvironment;
                    }

                    asUser = user;
                    break;
                case "--dry-run" when command == "apply":
                    dryRun = true;
                    break;
                case MigrationCli.CopyFlag:
                    copy = true;
                    break;
                default:
                    await error.WriteLineAsync($"Невідомий або неповний параметр: {args[i]}").ConfigureAwait(false);
                    return MigrationCli.UsageOrEnvironment;
            }
        }

        if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(input) || asUser is null
            || (command == "switch" && (string.IsNullOrWhiteSpace(engine) || string.IsNullOrWhiteSpace(reason))))
        {
            await error.WriteLineAsync(
                command == "apply"
                    ? "apply потребує: --connection, --in <package.json>, --as-user <id> [--dry-run] [--out <result.json>]"
                    : "switch потребує: --connection, --in <eligible.json>, --engine Script|Formulas, --reason <текст>, --as-user <id> [--out <result.json>]")
                .ConfigureAwait(false);
            return MigrationCli.UsageOrEnvironment;
        }

        var database = new SqlConnectionStringBuilder(connection).InitialCatalog;
        if (!copy && !LooksLikeCopy(database))
        {
            await error.WriteLineAsync(
                $"{command} відмовлено: «{database}» не схоже на копію. На живій базі перенесення — лише через API "
                + $"(script-migration/{command}); для копії з іншим іменем додайте {MigrationCli.CopyFlag}.").ConfigureAwait(false);
            return MigrationCli.UsageOrEnvironment;
        }

        if (!File.Exists(input))
        {
            await error.WriteLineAsync($"Файл не знайдено: {input}").ConfigureAwait(false);
            return MigrationCli.UsageOrEnvironment;
        }

        var json = await File.ReadAllTextAsync(input, new UTF8Encoding(false), ct).ConfigureAwait(false);
        await using var provider = ParityHost.Build(connection, asUser);
        await using var scope = provider.CreateAsyncScope();
        try
        {
            object result;
            int code;
            if (command == "apply")
            {
                var request = JsonSerializer.Deserialize<ApplyScriptPackageRequest>(json, Web)
                              ?? throw new InvalidOperationException("Порожній пакет.");
                var response = await scope.ServiceProvider.GetRequiredService<ApplyScriptPackageHandler>()
                    .HandleAsync(request, dryRun, ct).ConfigureAwait(false);
                await output.WriteLineAsync(
                    $"apply{(dryRun ? " (dry-run)" : string.Empty)}: прийнято {response.Applied}, відмов {response.Refused}").ConfigureAwait(false);
                foreach (var refused in response.Entries.Where(e => e.Status == "refused"))
                {
                    await output.WriteLineAsync($"  REFUSED {refused.MethodologyCode} {refused.Version}: {refused.Reason} {refused.Message}")
                        .ConfigureAwait(false);
                }

                result = response;
                code = response.Refused > 0 ? MigrationCli.Failures : MigrationCli.Ok;
            }
            else
            {
                var eligible = JsonSerializer.Deserialize<EligibleIds>(json, Web)
                               ?? throw new InvalidOperationException("Порожній перелік версій.");
                var response = await scope.ServiceProvider.GetRequiredService<SwitchScriptEngineHandler>()
                    .HandleAsync(new SwitchScriptEngineRequest(eligible.VersionIds, engine, reason), ct).ConfigureAwait(false);
                await output.WriteLineAsync($"switch: {response.Engine}, версій {response.Versions.Count}").ConfigureAwait(false);
                result = response;
                code = MigrationCli.Ok;
            }

            if (outPath is not null)
            {
                await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(result, Web) + "\n", new UTF8Encoding(false), ct)
                    .ConfigureAwait(false);
            }

            return code;
        }
        catch (EcrException ex)
        {
            // Відмова обробника (switchChecksFailed, switchGoldenSetDiverged, ...): транзакція відкочена, нічого не змінено.
            await output.WriteLineAsync($"{command}: ВІДМОВА {ex.ErrorCode} {ex.Message}").ConfigureAwait(false);
            if (ex.Details is { Count: > 0 } details)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(details, Web)).ConfigureAwait(false);
            }

            return MigrationCli.Failures;
        }
    }

    /// <summary>Чи схоже ім'я бази на копію (слова <see cref="MigrationCli.LooksSafe"/> або «ms» — стенд репетиції EcrLandMs).</summary>
    /// <param name="database">Ім'я бази.</param>
    public static bool LooksLikeCopy(string? database)
        => MigrationCli.LooksSafe(database)
           || (database is not null && database.EndsWith("Ms", StringComparison.Ordinal));

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record EligibleIds(IReadOnlyList<int> VersionIds);
}
