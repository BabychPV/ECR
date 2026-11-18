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
/// будує входи, <see cref="ScriptCalculationModule.ExecuteDetailedAsync"/> рахує. Вихідний запис (<c>CalculationOutputWriter</c>)
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
    /// <inheritdoc />
    public async Task<ScriptPreviewResult> RunAsync(ScriptPreviewRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var module = modules.OfType<ScriptCalculationModule>().FirstOrDefault()
            ?? throw new DomainException(
                "ECR-CALC-0422",
                "Модуль скриптів не зареєстровано.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CALC-0422.noModule" });

        var version = await drafts.FindVersionAsync(request.MethodologyVersionId, ct).ConfigureAwait(false)
            ?? throw new DomainException(
                "ECR-CALC-0404",
                $"Версії методології {request.MethodologyVersionId} не існує.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CALC-0404.version" });
        var methodology = await drafts.FindAsync(version.MethodologyId, ct).ConfigureAwait(false)
            ?? throw new DomainException(
                "ECR-CALC-0404",
                $"Методології {version.MethodologyId} не існує.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CALC-0404.methodology" });

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
                    (onDate, token) => sourceCompiler.ResolveTextImportsAsync(
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
                module, prepared, descriptor, instance.TableInstanceId, tableCode, outcome, allKeys, request, ct)
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

        var result = new List<ScriptPreviewRow>();
        foreach (var key in runKeys)
        {
            var position = number.GetValueOrDefault(key);
            if (!byKey.TryGetValue(key, out var input))
            {
                result.Add(new ScriptPreviewRow(key, position, "selected", null, null, []));
                continue;
            }

            try
            {
                var (output, values) = await module.ExecuteDetailedAsync(prepared, input, ct).ConfigureAwait(false);
                var mapped = values
                    .Select(v => new ScriptPreviewValue(
                        v.Name, v.SubstanceCode, v.Kind.ToString().ToLowerInvariant(), v.Text ?? string.Empty, v.ErrorCode))
                    .ToList();

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

                result.Add(new ScriptPreviewRow(key, position, "selected", issue, missing, mapped));
            }
            catch (DomainException error) when (GenericCalculationModule.IsCategoryRuleRowFailure(error))
            {
                result.Add(new ScriptPreviewRow(key, position, "rejected", "rejected", null, []));
            }
        }

        foreach (var key in rest)
        {
            result.Add(new ScriptPreviewRow(
                key, number.GetValueOrDefault(key), inactive.Contains(key) ? "inactive" : "notSelected", null, null, []));
        }

        return new ScriptPreviewTable(
            tableInstanceId,
            tableCode,
            allKeys.Count,
            selected.Count,
            inactive.Count,
            [.. result.OrderBy(r => r.RowNumber)]);
    }
}
