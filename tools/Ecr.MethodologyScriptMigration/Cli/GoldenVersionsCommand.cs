using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.Application.Calculations;
using Ecr.Application.Errors;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.MethodologyScriptMigration.Cli;

/// <summary>
/// ✎ D-571 (P-1123-4): <c>golden-versions</c> — НА КОПІЇ (репетиція): для чинних версій, чий золотий набір не має очікувань
/// (<c>ExpectedJson = {}</c>; PSS-23 не дає перемкнути їх на Script), — нова версія-клон, де кожен такий випадок отримує вхід і
/// очікування з рядка-еталона бази ECR (<c>golden-proposals.json</c> від <c>parity-cases</c>), і публікація з датою чинності
/// «чинна + 1 день» (клон — ті самі формули, тож числа документів не змінюються). Ті самі обробники, що й API
/// (<c>POST …/versions</c>, <c>PUT …/tests/{code}</c>, <c>POST …/publish</c>); на live — лише через API (стадія вікна).
/// </summary>
public static partial class GoldenVersionsCommand
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Наступний номер версії: найбільший <c>V&lt;n&gt;</c> методології + 1 (інші номери не враховуються).</summary>
    /// <param name="existing">Номери наявних версій.</param>
    public static string NextVersionNumber(IEnumerable<string> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var max = existing
            .Select(v => VersionPattern().Match(v))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0)
            .Max();
        return "V" + (max + 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Чи має золотий набір хоч один випадок без очікувань (порожній JSON-об'єкт).</summary>
    /// <param name="expectedJson">Очікування випадку.</param>
    public static bool IsEmptyExpectation(string? expectedJson)
        => string.IsNullOrWhiteSpace(expectedJson) || WhitespacePattern().Replace(expectedJson, string.Empty) == "{}";

    /// <summary>Виконує команду.</summary>
    /// <param name="args">Аргументи.</param>
    /// <param name="output">Вивід.</param>
    /// <param name="error">Помилки.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? connection = null, input = null, outPath = null, borrowedPath = null;
        int? asUser = null;
        var copy = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--connection" when i + 1 < args.Length: connection = args[++i]; break;
                case "--in" when i + 1 < args.Length: input = args[++i]; break;
                case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
                case "--borrowed" when i + 1 < args.Length: borrowedPath = args[++i]; break;
                case "--as-user" when i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var u):
                    asUser = u; i++; break;
                case MigrationCli.CopyFlag: copy = true; break;
                default:
                    await error.WriteLineAsync($"Невідомий або неповний параметр: {args[i]}").ConfigureAwait(false);
                    return MigrationCli.UsageOrEnvironment;
            }
        }

        if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(input) || asUser is null)
        {
            await error.WriteLineAsync("golden-versions потребує: --connection, --in <golden-proposals.json>, --as-user <id> [--borrowed <borrowed-rules.json>] [--out <result.json>]").ConfigureAwait(false);
            return MigrationCli.UsageOrEnvironment;
        }

        var database = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection).InitialCatalog;
        if (!copy && !CopyHandlerCommands.LooksLikeCopy(database))
        {
            await error.WriteLineAsync($"golden-versions відмовлено: «{database}» не схоже на копію. На живій базі — лише API (стадія GoldenVersions).").ConfigureAwait(false);
            return MigrationCli.UsageOrEnvironment;
        }

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(input, new UTF8Encoding(false), ct).ConfigureAwait(false));
        var proposals = doc.RootElement.GetProperty("versions").EnumerateArray()
            .ToDictionary(p => p.GetProperty("versionId").GetInt32(), p => p);
        // ✎ P-1123-4 «Подивися в БД ecr»: правила, які стара система застосовувала методології поза її власними (C11_10 на рядках
        // правил C11_01/02/04), — Build-MsBorrowedRules.ps1; MatchJson уже з Id колонок і записів цільової бази.
        var borrowed = new Dictionary<int, List<(string Code, string MatchJson, int Priority)>>();
        if (borrowedPath is not null)
        {
            using var b = JsonDocument.Parse(await File.ReadAllTextAsync(borrowedPath, new UTF8Encoding(false), ct).ConfigureAwait(false));
            foreach (var v in b.RootElement.GetProperty("versions").EnumerateArray())
            {
                var rules = v.GetProperty("rules").EnumerateArray()
                    .Select(r => (r.GetProperty("code").GetString()!, r.GetProperty("matchJson").GetString()!, r.GetProperty("priority").GetInt32()))
                    .ToList();
                if (rules.Count > 0)
                {
                    borrowed[v.GetProperty("versionId").GetInt32()] = rules;
                }
            }
        }

        await using var provider = ParityHost.Build(connection, asUser);
        var created = new List<object>();
        var failed = 0;
        foreach (var currentId in proposals.Keys.Union(borrowed.Keys).Order())
        {
            ct.ThrowIfCancellationRequested();
            var hasProposal = proposals.TryGetValue(currentId, out var proposal);
            var extraRules = borrowed.GetValueOrDefault(currentId) ?? [];
            await using var scope = provider.CreateAsyncScope();
            await using var db = MigrationCli.CreateContext(connection, readOnly: true);
            var current = await db.MethodologyVersions.AsNoTracking().SingleAsync(v => v.Id == currentId, ct).ConfigureAwait(false);
            var code = await db.Methodologies.AsNoTracking().Where(m => m.Id == current.MethodologyId).Select(m => m.Code).SingleAsync(ct).ConfigureAwait(false);
            var tests = await db.MethodologyTestCases.AsNoTracking()
                .Where(t => t.MethodologyVersionId == currentId).ToListAsync(ct).ConfigureAwait(false);
            // Випадок без очікувань АБО з документом, якого в базі немає (ворота відмовляють «тест не має періоду»).
            var documentIds = tests.Select(t => DocumentOf(t.InputJson)).Where(id => id > 0).Distinct().ToList();
            var existing = await db.Documents.AsNoTracking().Where(d => documentIds.Contains(d.Id)).Select(d => d.Id).ToListAsync(ct).ConfigureAwait(false);
            var empty = tests.Where(t => IsEmptyExpectation(t.ExpectedJson) || !existing.Contains(DocumentOf(t.InputJson))).Select(t => t.Code).ToList();
            if (empty.Count == 0 && extraRules.Count == 0)
            {
                continue;
            }

            if (empty.Count > 0 && !hasProposal)
            {
                failed++;
                await output.WriteLineAsync($"  BLOCKED {code}: golden set without expectations and no ECR reference row for it").ConfigureAwait(false);
                continue;
            }
            var numbers = await db.MethodologyVersions.AsNoTracking().Where(v => v.MethodologyId == current.MethodologyId)
                .Select(v => v.Version).ToListAsync(ct).ConfigureAwait(false);
            var number = NextVersionNumber(numbers);
            var effective = (current.EffectiveFrom ?? new DateOnly(2024, 1, 1)).AddDays(1);
            var inputJson = !hasProposal ? null : JsonSerializer.Serialize(new
            {
                documentId = proposal.GetProperty("documentId").GetInt64(),
                periodKey = new { value = proposal.GetProperty("periodKey").GetInt32() },
                arguments = proposal.GetProperty("arguments").EnumerateArray().Select(a => new
                {
                    argumentCode = a.GetProperty("argumentCode").GetString(),
                    value = a.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : (decimal?)null,
                    valueString = a.TryGetProperty("valueString", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null,
                    unitId = (int?)null,
                    entryId = a.TryGetProperty("entryId", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : (long?)null,
                }),
            });
            var expectedJson = hasProposal ? proposal.GetProperty("expected").GetRawText() : null;
            int? draftId = null;
            try
            {
                var draft = await scope.ServiceProvider.GetRequiredService<CreateMethodologyVersionHandler>()
                    .HandleAsync(current.MethodologyId, number, currentId, current.Level, MethodologyEngine.Formulas, ct).ConfigureAwait(false);
                draftId = draft.Id;
                foreach (var testCode in empty)
                {
                    await scope.ServiceProvider.GetRequiredService<SaveMethodologyTestCaseHandler>()
                        .HandleAsync(draft.Id, testCode, inputJson!, expectedJson!, 0.000000001m, ct).ConfigureAwait(false);
                }

                foreach (var (ruleCode, matchJson, priority) in extraRules)
                {
                    await scope.ServiceProvider.GetRequiredService<SaveMethodologyRuleHandler>()
                        .HandleAsync(draft.Id, ruleCode, matchJson, priority, true, ct).ConfigureAwait(false);
                }

                var why = "W15z D-571 (P-1123-4): "
                          + (empty.Count > 0 ? $"golden expectations from ECR reference row {proposal.GetProperty("caseKey").GetString()}" : "golden set unchanged")
                          + (extraRules.Count > 0 ? $"; {extraRules.Count} rule(s) the old system applied (ECR CalcResult)" : string.Empty);
                await scope.ServiceProvider.GetRequiredService<PublishMethodologyHandler>()
                    .HandleAsync(draft.Id, why, effective, ct)
                    .ConfigureAwait(false);
                created.Add(new { methodologyCode = code, fromVersionId = currentId, versionId = draft.Id, version = number, effectiveFrom = effective, tests = empty, rules = extraRules.Count });
                await output.WriteLineAsync($"  {code}: {current.Version} -> {number} (з {effective:yyyy-MM-dd}), випадків з еталоном {empty.Count}, позичених правил {extraRules.Count}").ConfigureAwait(false);
            }
            catch (EcrException ex)
            {
                // Чернетку, яку не вдалося опублікувати, прибираємо: повтор фази не має множити версії-сироти.
                if (draftId is { } orphan)
                {
                    await using var cleanup = provider.CreateAsyncScope();
                    await cleanup.ServiceProvider.GetRequiredService<DeleteMethodologyVersionHandler>()
                        .HandleAsync(current.MethodologyId, orphan, ct).ConfigureAwait(false);
                }

                failed++;
                await output.WriteLineAsync($"  FAIL {code}: {ex.ErrorCode} {ex.Message}").ConfigureAwait(false);
            }
        }

        if (outPath is not null)
        {
            await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(new { created }, Web) + "\n", new UTF8Encoding(false), ct).ConfigureAwait(false);
        }

        await output.WriteLineAsync($"golden-versions: створено й опубліковано {created.Count}, невдало {failed}").ConfigureAwait(false);
        return failed > 0 ? MigrationCli.Failures : MigrationCli.Ok;
    }

    /// <summary>Документ золотого входу (<c>documentId</c>); 0 — немає або не читається.</summary>
    /// <param name="inputJson">Вхід випадку.</param>
    public static long DocumentOf(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return 0;
        }

        try
        {
            using var d = JsonDocument.Parse(inputJson);
            return d.RootElement.TryGetProperty("documentId", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    [GeneratedRegex(@"^V(\d+)$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
