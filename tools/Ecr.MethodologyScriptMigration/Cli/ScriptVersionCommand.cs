using System.Globalization;
using System.Text;
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Scripting;
using Ecr.Application.Errors;
using Ecr.Domain.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.MethodologyScriptMigration.Cli;

/// <summary>
/// ✎ P-1124-2 (вода ECW_C09_02_01): <c>script-version</c> на КОПІЇ — нова версія методології ОДРАЗУ на рушії Script (порожня
/// чернетка-скрипт тієї самої методології), текст скрипта, золотий набір із пропозиції <c>parity-cases --golden-out --golden-all</c> (рядки старої системи) і
/// публікація з датою «чинна + 1 день». Ті самі обробники, що й ендпоінти (POST versions, PUT script, PUT tests, POST publish);
/// на live — ті самі виклики через API у стадії вікна.
/// </summary>
/// <remarks>
/// Навіщо окремо від <c>golden-versions</c>: той клонує версію на Formulas, а формульна вода не публікується в жодному шаблоні
/// (<c>ECR-CALC-0438</c>: у таблицях, до яких прив'язана методологія, немає колонок @Concentration/@Volume — місячні псевдоніми
/// розуміє лише скрипт). Невдала публікація — чернетку видалено, нічого не лишається.
/// </remarks>
public static class ScriptVersionCommand
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Виконує команду.</summary>
    /// <param name="args">Аргументи.</param>
    /// <param name="output">Вивід.</param>
    /// <param name="error">Помилки.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? connection = null, textPath = null, proposalPath = null, outPath = null, reason = null;
        int? fromVersion = null, asUser = null;
        var copy = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--connection" when i + 1 < args.Length: connection = args[++i]; break;
                case "--from" when i + 1 < args.Length: fromVersion = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--text" when i + 1 < args.Length: textPath = args[++i]; break;
                case "--golden" when i + 1 < args.Length: proposalPath = args[++i]; break;
                case "--reason" when i + 1 < args.Length: reason = args[++i]; break;
                case "--as-user" when i + 1 < args.Length: asUser = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
                case MigrationCli.CopyFlag: copy = true; break;
                default:
                    await error.WriteLineAsync($"Невідомий або неповний параметр: {args[i]}").ConfigureAwait(false);
                    return MigrationCli.UsageOrEnvironment;
            }
        }

        if (connection is null || fromVersion is null || textPath is null || proposalPath is null || asUser is null || string.IsNullOrWhiteSpace(reason))
        {
            await error.WriteLineAsync("script-version потребує: --connection, --from <versionId>, --text <ecrs>, --golden <proposal.json>, --reason <текст>, --as-user <id> [--out <f>]")
                .ConfigureAwait(false);
            return MigrationCli.UsageOrEnvironment;
        }

        var database = new SqlConnectionStringBuilder(connection).InitialCatalog;
        if (!copy && !CopyHandlerCommands.LooksLikeCopy(database))
        {
            await error.WriteLineAsync($"script-version відмовлено: «{database}» не схоже на копію; на живій базі — лише API.").ConfigureAwait(false);
            return MigrationCli.UsageOrEnvironment;
        }

        var text = (await File.ReadAllTextAsync(textPath, new UTF8Encoding(false), ct).ConfigureAwait(false))
            .TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal);
        using var proposal = JsonDocument.Parse(await File.ReadAllTextAsync(proposalPath, new UTF8Encoding(false), ct).ConfigureAwait(false));
        var golden = proposal.RootElement.GetProperty("versions").EnumerateArray().Single(v => v.GetProperty("versionId").GetInt32() == fromVersion);

        await using var provider = ParityHost.Build(connection, asUser);
        await using var db = MigrationCli.CreateContext(connection, readOnly: true);
        var current = await db.MethodologyVersions.AsNoTracking().SingleAsync(v => v.Id == fromVersion, ct).ConfigureAwait(false);
        var numbers = await db.MethodologyVersions.AsNoTracking().Where(v => v.MethodologyId == current.MethodologyId)
            .Select(v => v.Version).ToListAsync(ct).ConfigureAwait(false);
        var number = GoldenVersionsCommand.NextVersionNumber(numbers);
        var effective = (current.EffectiveFrom ?? new DateOnly(2024, 1, 1)).AddDays(1);

        var cases = golden.GetProperty("cases").EnumerateArray().ToList();
        int? draftId = null;
        try
        {
            await using var scope = provider.CreateAsyncScope();
            var draft = await scope.ServiceProvider.GetRequiredService<CreateMethodologyVersionHandler>()
                .HandleAsync(current.MethodologyId, number, null, current.Level, MethodologyEngine.Script, ct).ConfigureAwait(false);
            draftId = draft.Id;
            await scope.ServiceProvider.GetRequiredService<SaveMethodologyScriptHandler>()
                .HandleAsync(draft.Id, text, null, ct).ConfigureAwait(false);

            // ⚠ Порожня чернетка-скрипт, а не клон: клон лишає рушій джерела (Formulas), а перемкнути рушій чернетки API не дає.
            // Виходи, речовини й таблиці читання матеріалізує збереження тексту, прив'язки «вихід → колонка» — публікація.
            var codes = cases.Select(c => c.GetProperty("key").GetString()!).ToList();
            for (var i = 0; i < cases.Count; i++)
            {
                var c = cases[i];
                var input = JsonSerializer.Serialize(new
                {
                    documentId = golden.GetProperty("documentId").GetInt64(),
                    periodKey = new { value = golden.GetProperty("periodKey").GetInt32() },
                    arguments = c.GetProperty("arguments").EnumerateArray().Select(a => new
                    {
                        argumentCode = a.GetProperty("argumentCode").GetString(),
                        value = a.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : (decimal?)null,
                        valueString = (string?)null,
                        unitId = (int?)null,
                        entryId = (long?)null,
                    }),
                });
                await scope.ServiceProvider.GetRequiredService<SaveMethodologyTestCaseHandler>()
                    .HandleAsync(draft.Id, codes[i], input, c.GetProperty("expected").GetRawText(), 0.000000001m, ct).ConfigureAwait(false);
            }

            await scope.ServiceProvider.GetRequiredService<PublishMethodologyHandler>()
                .HandleAsync(draft.Id, reason, effective, ct).ConfigureAwait(false);

            var result = new { fromVersionId = current.Id, versionId = draft.Id, version = number, effectiveFrom = effective, engine = "Script", goldenCases = cases.Count };
            await output.WriteLineAsync($"script-version: {current.Version} -> {number} (Script, з {effective:yyyy-MM-dd}), золотих випадків {cases.Count}, id {draft.Id}").ConfigureAwait(false);
            if (outPath is not null)
            {
                await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(result, Web) + "\n", new UTF8Encoding(false), ct).ConfigureAwait(false);
            }

            return MigrationCli.Ok;
        }
        catch (Exception ex) when (ex is EcrException or Ecr.Domain.Abstractions.DomainException)
        {
            if (draftId is { } orphan)
            {
                await using var cleanup = provider.CreateAsyncScope();
                await cleanup.ServiceProvider.GetRequiredService<DeleteMethodologyVersionHandler>()
                    .HandleAsync(current.MethodologyId, orphan, ct).ConfigureAwait(false);
            }

            await output.WriteLineAsync($"script-version: ВІДМОВА {ex.Message}").ConfigureAwait(false);
            if (ex is EcrException { Details: { Count: > 0 } details })
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(details, Web)).ConfigureAwait(false);
            }

            return MigrationCli.Failures;
        }
    }
}
