using Ecr.Application.Calculations.Scripting;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Calculations;

/// <summary>
/// Живий перегляд скрипта на документі (MS, план §5.4): що вибрано, що пораховано; НІЧОГО не пишеться.
/// </summary>
/// <remarks>
/// ⛔ Той самий шлях, що й у розрахунку: <see cref="ScriptRowSelector"/> вибирає рядки, <see cref="CalculationInputBuilder"/>
/// будує входи, <see cref="ScriptCalculationModule.ExecuteDetailedAsync(CalculationBindingContext, CalculationInput, ScriptRunLimits, CancellationToken)"/> рахує. Вихідний запис (<c>CalculationOutputWriter</c>)
/// не викликається ніколи.
/// <para>
/// ⚠ Імпорти беруться з ТЕКСТУ, який переглядають (<see cref="ScriptSourceCompiler.ResolveTextImportsAsync"/>), а не з
/// матеріалізованих рядків <c>calc.MethodologyImport</c>: незбережений <c>import X;</c> теж працює.
/// </para>
/// </remarks>
public sealed class ScriptPreviewRunner(
    IMethodologyDraftStore drafts,
    IEnumerable<ICalculationModule> modules,
    ScriptRowSelector selector,
    CalculationInputBuilder inputBuilder,
    IRowStore rows,
    IMetadataCache metadata,
    ScriptSourceCompiler sourceCompiler) : IScriptPreviewRunner
{
    /// <summary>
    /// Спільний бюджет кроків ОДНОГО перегляду на всі рядки всіх таблиць (PSS-21): 10 бюджетів рядка, 20 000 000 кроків
    /// (кілька секунд процесора).
    /// </summary>
    /// <remarks>
    /// ⛔ Без нього стеля перегляду була б «рядки × екземпляри × бюджет рядка». Рядки після вичерпання дають
    /// <c>#BUDGET</c> на кожен оператор (проблема рядка <c>stepError</c>), а не тишу. Детерміновано, як і бюджет рядка;
    /// годинник (<c>PreviewMethodologyScriptHandler.RunTimeout</c>) — страховка понад нього.
    /// </remarks>
    public const int MaxPreviewEvaluationSteps = 10 * ScriptCalculationModule.MaxRowEvaluationSteps;

    /// <summary>Найбільше величин в одній відповіді перегляду (PSS-21); решта рядків — з <c>valuesTruncated</c>.</summary>
    public const int MaxPreviewValues = 50_000;

    /// <inheritdoc />
    public async Task<ScriptPreviewResult> RunAsync(ScriptPreviewRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var budget = new PreviewBudget(new Expressions.Evaluation.EvaluationBudget(MaxPreviewEvaluationSteps), MaxPreviewValues);

        var module = modules.OfType<ScriptCalculationModule>().FirstOrDefault()
            ?? throw new DomainException(
                "ECR-CALC-0422",
                "Модуль скриптів не зареєстровано.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.noModule",
                    ["code"] = "script",
                    ["level"] = "Configuration",
                });

        var version = await drafts.FindVersionAsync(request.MethodologyVersionId, ct).ConfigureAwait(false)
            ?? throw new DomainException(
                "ECR-CALC-0404",
                $"Версії методології {request.MethodologyVersionId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0404.version",
                    ["methodologyVersionId"] = request.MethodologyVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        var methodology = await drafts.FindAsync(version.MethodologyId, ct).ConfigureAwait(false)
            ?? throw new DomainException(
                "ECR-CALC-0404",
                $"Методології {version.MethodologyId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0404.methodology",
                    ["methodologyId"] = version.MethodologyId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });

        var descriptor = new MethodologyDescriptor(
            methodology.Id,
            version.Id,
            methodology.Code,
            version.Version,
            version.Level,
            version.NumericMode,
            version.CalendarMode,
            version.TraceLevel,
            MethodologyEngine.Script);
        var periodKey = new PeriodKey(request.PeriodKey);
        var isLibrary = methodology.Kind == MethodologyKind.Library;

        var prepared = await module
            .PrepareAsync(
                descriptor,
                request.DocumentId,
                periodKey,
                registries: null,
                new ScriptPrepareOptions(
                    request.Text,
                    ImportResolver: (onDate, token) => sourceCompiler.ResolveTextImportsAsync(
                        request.Text, version.NumericMode, isLibrary, onDate, token)),
                ct)
            .ConfigureAwait(false);
        var script = prepared.Script!;
        var runtime = prepared.ScriptRuntime as ScriptRuntime;

        var wanted = script.Rows
            .SelectMany(c => c.Tables)
            .Select(t => t.TableCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tables = new List<ScriptPreviewTable>();
        var instances = await rows.GetTableInstancesAsync(request.DocumentId, periodKey, ct).ConfigureAwait(false);
        foreach (var instance in instances.OrderBy(i => i.TableInstanceId))
        {
            if (request.TableInstanceId is { } only && only != instance.TableInstanceId)
            {
                continue;
            }

            var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
            var tableCode = ScriptRuntime.TableCodeOf(snapshot, instance.TableDefId);
            if (!wanted.Contains(tableCode))
            {
                continue;
            }

            var outcome = await selector
                .MatchAsync(script, instance.TableInstanceId, (registry, entry) => runtime?.EntryIdText(registry, entry), ct)
                .ConfigureAwait(false);
            if (outcome.AllRowKeys is not { } allKeys)
            {
                // Речення rows не стосується цього екземпляра (інший шаблон).
                continue;
            }

            tables.Add(await PreviewTableAsync(
                module, prepared, descriptor, instance.TableInstanceId, tableCode, outcome, allKeys, request, budget, ct)
                .ConfigureAwait(false));
        }

        return new ScriptPreviewResult(tables);
    }

    private async Task<ScriptPreviewTable> PreviewTableAsync(
        ScriptCalculationModule module,
        CalculationBindingContext prepared,
        MethodologyDescriptor descriptor,
        long tableInstanceId,
        string tableCode,
        RowMatchOutcome outcome,
        IReadOnlyList<string> allKeys,
        ScriptPreviewRequest request,
        PreviewBudget budget,
        CancellationToken ct)
    {
        var selected = outcome.Matches.Select(m => m.RowKey).ToList();
        var selectedSet = selected.ToHashSet(StringComparer.Ordinal);
        var inactive = (outcome.Excluded ?? []).ToHashSet(StringComparer.Ordinal);

        var number = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < allKeys.Count; i++)
        {
            number.TryAdd(allKeys[i], i + 1);
        }

        // Спершу вибрані (їх і рахуємо, не більше MaxRows), решту місця — невибрані й неактивні за порядком таблиці.
        var runKeys = selected.Take(request.MaxRows).ToList();
        var rest = allKeys.Where(k => !selectedSet.Contains(k)).Take(Math.Max(0, request.MaxRows - runKeys.Count)).ToList();

        var inputs = await inputBuilder
            .BuildAsync(tableInstanceId, runKeys, prepared.PeriodKey, descriptor, ct, outcome.Read)
            .ConfigureAwait(false);
        var byKey = inputs
            .GroupBy(i => i.SourceRowKey ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Поля, які читає скрипт (і бібліотеки); місячні — за префіксом `<ім'я>_M`.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var monthly = new List<string>();
        foreach (var script in ScriptRuntime.AllScripts(((ScriptRuntime)prepared.ScriptRuntime!).Program))
        {
            foreach (var field in script.RowFields)
            {
                names.Add(field.Name);
                if (field.IsMonthly)
                {
                    monthly.Add(field.Name + "_M");
                }
            }
        }

        IReadOnlyList<ScriptPreviewInput> InputsOf(CalculationInput input)
            => [.. input.Arguments
                .Where(a => names.Contains(a.ArgumentCode)
                            || monthly.Any(p => a.ArgumentCode.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                .DistinctBy(a => a.ArgumentCode, StringComparer.OrdinalIgnoreCase)
                .Select(a => new ScriptPreviewInput(a.ArgumentCode, a.Value, a.ValueString, a.UnitId))];

        var result = new List<ScriptPreviewRow>();
        foreach (var key in runKeys)
        {
            var position = number.GetValueOrDefault(key);
            if (!byKey.TryGetValue(key, out var input))
            {
                result.Add(new ScriptPreviewRow(key, position, "selected", null, null, [], []));
                continue;
            }

            try
            {
                // Стеля величин: на одну більше за залишок — так видно, що рядок урізано, без окремого прапорця рушія.
                var room = budget.ValuesLeft;
                var (output, values) = await module
                    .ExecuteDetailedAsync(prepared, input, new ScriptRunLimits(budget.Steps, room + 1), ct)
                    .ConfigureAwait(false);
                var truncated = values.Count > room;
                var mapped = values
                    .Take(room)
                    .Select(v => new ScriptPreviewValue(
                        v.Name, v.SubstanceCode, v.Kind.ToString().ToLowerInvariant(), v.Text ?? string.Empty, v.ErrorCode))
                    .ToList();
                budget.ValuesLeft -= mapped.Count;

                string? issue = null;
                IReadOnlyList<string>? missing = null;
                if (output.Values.Count == 0 && output.MissingArguments is { Count: > 0 } arguments)
                {
                    issue = "missingInputs";
                    missing = arguments;
                }
                else if (output.StepError is not null)
                {
                    issue = "stepError";
                }

                result.Add(new ScriptPreviewRow(key, position, "selected", issue, missing, mapped, InputsOf(input))
                {
                    ValuesTruncated = truncated,
                });
            }
            catch (DomainException error) when (GenericCalculationModule.IsCategoryRuleRowFailure(error))
            {
                result.Add(new ScriptPreviewRow(key, position, "rejected", "rejected", null, [], InputsOf(input)));
            }
        }

        foreach (var key in rest)
        {
            result.Add(new ScriptPreviewRow(
                key, number.GetValueOrDefault(key), inactive.Contains(key) ? "inactive" : "notSelected", null, null, [], []));
        }

        return new ScriptPreviewTable(
            tableInstanceId,
            tableCode,
            allKeys.Count,
            selected.Count,
            inactive.Count,
            [.. result.OrderBy(r => r.RowNumber)]);
    }

    /// <summary>Спільні межі одного перегляду: бюджет кроків і залишок величин у відповіді.</summary>
    private sealed class PreviewBudget(Expressions.Evaluation.EvaluationBudget steps, int values)
    {
        public Expressions.Evaluation.EvaluationBudget Steps { get; } = steps;

        public int ValuesLeft { get; set; } = values;
    }
}
