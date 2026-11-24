using System.Globalization;
using System.Text;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Calculations.Scripting.Parity;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Scripting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.MethodologyScriptMigration.Cli;

/// <summary>
/// ✎ D-571 (P-1123-4): <c>parity-cases</c> — паритет «формули ↔ тіньовий скрипт» на рядках ЕТАЛОННИХ даних старої системи
/// (<c>cases.json</c> від <c>window/Build-MsReferenceCases.ps1</c>, база ECR лише читається там). Нічого не пише в базу.
/// </summary>
/// <remarks>
/// Версія ДОВЕДЕНА на еталоні, коли порівняно ≥ 1 значення і розбіжностей 0. Запис у <c>--eligible-out</c> несе SHA-256 тексту, що
/// порівнювався: вікно перемикає її лише тоді, коли в живій базі той самий текст (той самий <c>ScriptHash</c>) — пакет будується
/// детерміновано з того самого джерела (відбиток), тож текст копії й live однаковий. <c>--golden-out</c> — очікування золотого
/// набору для нових версій (D-40): перший рядок-еталон, де старий шлях дав значення, і ці значення.
/// </remarks>
public static class ReferenceParityCommand
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
        string? connection = null, input = null, outPath = null, eligibleOut = null, goldenOut = null, texts = null;
        var goldenAll = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--connection" when i + 1 < args.Length: connection = args[++i]; break;
                case "--in" when i + 1 < args.Length: input = args[++i]; break;
                case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
                case "--eligible-out" when i + 1 < args.Length: eligibleOut = args[++i]; break;
                case "--golden-out" when i + 1 < args.Length: goldenOut = args[++i]; break;
                case "--golden-all": goldenAll = true; break;
                // ✎ P-1124-2: текст версії — з теки convert (<Код>/<Версія>.ecrs), якщо файл є, замість тіньового з бази. Так
                // доводиться саме той текст, що піде в finalize (повний «--full-life» текст витісненої версії, чернетки без тіні).
                case "--texts" when i + 1 < args.Length: texts = args[++i]; break;
                default:
                    await error.WriteLineAsync($"Невідомий або неповний параметр: {args[i]}").ConfigureAwait(false);
                    return MigrationCli.UsageOrEnvironment;
            }
        }

        if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(outPath))
        {
            await error.WriteLineAsync("parity-cases потребує: --connection, --in <cases.json>, --out <report.json> [--eligible-out <ref-eligible.json>] [--golden-out <golden.json>]")
                .ConfigureAwait(false);
            return MigrationCli.UsageOrEnvironment;
        }

        if (!File.Exists(input))
        {
            await error.WriteLineAsync($"Файл не знайдено: {input}").ConfigureAwait(false);
            return MigrationCli.UsageOrEnvironment;
        }

        var cases = JsonSerializer.Deserialize<ReferenceCases>(await File.ReadAllTextAsync(input, new UTF8Encoding(false), ct).ConfigureAwait(false), Web)
                    ?? throw new InvalidOperationException("Порожній cases.json.");
        foreach (var path in new[] { eligibleOut, goldenOut })
        {
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }

        await using var provider = ParityHost.Build(connection);
        await using var scope = provider.CreateAsyncScope();
        var comparer = scope.ServiceProvider.GetRequiredService<ParityComparer>();
        await using var db = MigrationCli.CreateContext(connection, readOnly: true);

        var results = new List<ReferenceVersionResult>();
        var mismatches = new List<ParityMismatch>();
        var golden = new List<GoldenProposal>();
        foreach (var version in cases.Versions)
        {
            ct.ThrowIfCancellationRequested();
            var stored = await db.MethodologyVersions.AsNoTracking()
                .Where(v => v.Id == version.VersionId)
                .Select(v => new { v.Engine, v.ScriptText })
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (stored is not null && texts is not null
                && ScriptFiles.PathOf(texts, version.MethodologyCode, version.Version) is var textPath && File.Exists(textPath))
            {
                stored = stored with { ScriptText = (await File.ReadAllTextAsync(textPath, new UTF8Encoding(false), ct).ConfigureAwait(false)).Replace("\r\n", "\n", StringComparison.Ordinal) };
            }
            if (stored is null || stored.Engine != MethodologyEngine.Formulas)
            {
                results.Add(new ReferenceVersionResult(version.MethodologyCode, version.VersionId, version.Version, 0, 0, 0, null,
                    stored is null ? "version not found" : "already Script"));
                continue;
            }

            var rows = version.Cases
                .Select(c => (c.Key, (IReadOnlyList<CalculationArgument>)[.. c.Arguments.Select(a => new CalculationArgument(a.ArgumentCode, a.Value, a.ValueString, null, a.EntryId))]))
                .ToList();
            IReadOnlyDictionary<string, ParityRowOutcome> oldRows;
            if (string.IsNullOrWhiteSpace(stored.ScriptText))
            {
                // ✎ P-1123-6: версію без тіньового тексту (C11_15: правил немає, конвертер відмовляє) паритет не доводить, але
                // очікування золотого набору для НОВОЇ версії (з правилом) — це вихід старого рушія, і його можна порахувати.
                oldRows = await comparer
                    .ExecuteOldInputsAsync(version.VersionId, cases.DocumentId, new PeriodKey(cases.PeriodKey), rows, ct)
                    .ConfigureAwait(false);
                results.Add(new ReferenceVersionResult(version.MethodologyCode, version.VersionId, version.Version, rows.Count, 0, 0, null,
                    "no shadow text (old engine only: golden expectations)"));
            }
            else
            {
                var (report, compared) = await comparer
                    .CompareInputsAsync(version.VersionId, stored.ScriptText, cases.DocumentId, new PeriodKey(cases.PeriodKey), rows, ct)
                    .ConfigureAwait(false);
                oldRows = compared;
                mismatches.AddRange(report.Mismatches);
                var hash = Convert.ToHexStringLower(ScriptCompiler.Hash(stored.ScriptText));
                results.Add(new ReferenceVersionResult(
                    version.MethodologyCode, version.VersionId, version.Version, rows.Count, report.ValuesCompared, report.Mismatches.Count, hash,
                    report.Mismatches.Count > 0 ? "mismatches" : report.ValuesCompared == 0 ? "no values compared" : null));
            }

            var best = version.Cases
                .Select(c => (Case: c, Row: oldRows.GetValueOrDefault(c.Key)))
                .Where(x => x.Row is { Rejected: false, Error: null } row && row.Values.Any(v => v.Kind == CalculationResultKind.Output))
                .OrderByDescending(x => x.Row!.Values.Count(v => v.Kind == CalculationResultKind.Output))
                .FirstOrDefault();
            if (best.Row is not null)
            {
                var expected = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
                foreach (var value in best.Row.Values.Where(v => v.Kind == CalculationResultKind.Output))
                {
                    var key = value.SubstanceEntryId is { } s ? value.OutputCode + "@" + s.ToString(CultureInfo.InvariantCulture) : value.OutputCode;
                    expected.TryAdd(key, value.Value);
                }

                // ✎ P-1123-6: --golden-all — КОЖЕН рядок, де старий шлях дав виходи, стає випадком золотого набору (синтетичні еталони
                // з покриттям гілок); без прапорця — один найповніший рядок (еталон ECR).
                var all = !goldenAll
                    ? null
                    : version.Cases
                        .Select(c => (Case: c, Row: oldRows.GetValueOrDefault(c.Key)))
                        .Where(x => x.Row is { Rejected: false, Error: null } row && row.Values.Any(v => v.Kind == CalculationResultKind.Output))
                        .Take(MaxGoldenCases)
                        .Select(x => new GoldenCase(x.Case.Key, x.Case.Arguments, ExpectedOf(x.Row!)))
                        .ToList();
                golden.Add(new GoldenProposal(version.MethodologyCode, version.VersionId, version.Version, best.Case.Key, best.Case.Table,
                    cases.DocumentId, cases.PeriodKey, best.Case.Arguments, expected, all));
            }
        }

        var proven = results.Where(r => r.Reason is null).ToList();
        var full = new { cases.ReferenceDatabase, cases.DocumentId, cases.PeriodKey, versions = results, mismatches };
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(full, Web) + "\n", new UTF8Encoding(false), ct).ConfigureAwait(false);
        await output.WriteLineAsync(
            $"parity-cases: версій {results.Count}, рядків {results.Sum(r => r.Rows)}, значень {results.Sum(r => r.ValuesCompared)}, "
            + $"розбіжностей {mismatches.Count}, доведено на еталоні {proven.Count}").ConfigureAwait(false);
        foreach (var r in results.Where(r => r.Reason is not null))
        {
            await output.WriteLineAsync($"  НЕ ДОВЕДЕНО {r.MethodologyCode} {r.Version}: {r.Reason}").ConfigureAwait(false);
        }

        foreach (var m in mismatches.Take(20))
        {
            await output.WriteLineAsync($"  {m.MethodologyCode} {m.Version} row {m.RowKey ?? "-"}: {m.Aspect} old={m.Old ?? "-"} new={m.New ?? "-"}").ConfigureAwait(false);
        }

        if (goldenOut is not null && golden.Count > 0)
        {
            await File.WriteAllTextAsync(goldenOut, JsonSerializer.Serialize(new { format = "ecr-ms-golden-proposals", formatVersion = 1, cases.ReferenceDatabase, versions = golden }, Web) + "\n",
                new UTF8Encoding(false), ct).ConfigureAwait(false);
        }

        // ⛔ Будь-яка розбіжність — переліку немає (як у parity --eligible-out).
        if (mismatches.Count > 0)
        {
            return MigrationCli.Failures;
        }

        if (eligibleOut is not null && proven.Count > 0)
        {
            await File.WriteAllTextAsync(eligibleOut, JsonSerializer.Serialize(new
            {
                source = "reference:" + cases.ReferenceDatabase,
                versionIds = proven.Select(p => p.VersionId).ToList(),
                proven,
            }, Web) + "\n", new UTF8Encoding(false), ct).ConfigureAwait(false);
        }

        return proven.Count > 0 ? MigrationCli.Ok : MigrationCli.Failures;
    }

    /// <summary>Скільки випадків золотого набору щонайбільше дає --golden-all на версію.</summary>
    public const int MaxGoldenCases = 40;

    private static SortedDictionary<string, decimal> ExpectedOf(ParityRowOutcome row)
    {
        var expected = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var value in row.Values.Where(v => v.Kind == CalculationResultKind.Output))
        {
            var key = value.SubstanceEntryId is { } s ? value.OutputCode + "@" + s.ToString(CultureInfo.InvariantCulture) : value.OutputCode;
            expected.TryAdd(key, value.Value);
        }

        return expected;
    }

    /// <summary>Випадок золотого набору з рядка-еталона.</summary>
    public sealed record GoldenCase(string Key, IReadOnlyList<ReferenceArgument> Arguments, IReadOnlyDictionary<string, decimal> Expected);

    /// <summary>Підсумок версії на еталоні.</summary>
    public sealed record ReferenceVersionResult(
        string MethodologyCode, int VersionId, string Version, int Rows, int ValuesCompared, int MismatchCount, string? ScriptHash, string? Reason);

    /// <summary>Пропозиція золотого випадку з рядка-еталона.</summary>
    public sealed record GoldenProposal(
        string MethodologyCode, int VersionId, string Version, string CaseKey, string Table, long DocumentId, int PeriodKey,
        IReadOnlyList<ReferenceArgument> Arguments, IReadOnlyDictionary<string, decimal> Expected, IReadOnlyList<GoldenCase>? Cases = null);

    /// <summary>Файл cases.json.</summary>
    public sealed record ReferenceCases(string ReferenceDatabase, long DocumentId, int PeriodKey, IReadOnlyList<ReferenceVersion> Versions);

    /// <summary>Версія з рядками-еталонами.</summary>
    public sealed record ReferenceVersion(int VersionId, string MethodologyCode, string Version, IReadOnlyList<ReferenceCase> Cases);

    /// <summary>Рядок-еталон.</summary>
    public sealed record ReferenceCase(string Key, string Table, IReadOnlyList<ReferenceArgument> Arguments);

    /// <summary>Аргумент рядка-еталона.</summary>
    public sealed record ReferenceArgument(string ArgumentCode, decimal? Value, string? ValueString, long? EntryId);
}
