using Ecr.Application.Ports;
using Ecr.Calculations.Scripting.Parity;
using Ecr.Domain.ValueObjects;

namespace Ecr.Calculations;

/// <summary>
/// Паритет версії на документі (MS, план 8.4) для кінцевої точки авторингу: усі оригінальні екземпляри таблиць
/// документа за період, до яких методологія прив'язана активно, порівнює <see cref="ParityComparer"/>.
/// </summary>
/// <remarks>⛔ Нічого не пише: <see cref="ParityComparer"/> не викликає запис виходів.</remarks>
public sealed class ScriptParityRunner(
    IMethodologyDraftStore drafts,
    ICalculationBindingStore bindings,
    IRowStore rows,
    ParityComparer comparer) : IScriptParityRunner
{
    /// <inheritdoc />
    public async Task<ScriptParityResult> RunAsync(ScriptParityRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var version = await drafts.FindVersionAsync(request.MethodologyVersionId, ct).ConfigureAwait(false)
            ?? throw new ArgumentException($"Версії {request.MethodologyVersionId} немає.", nameof(request));
        var periodKey = new PeriodKey(request.PeriodKey);

        var bound = (await bindings.ListAsync(version.MethodologyId, ct).ConfigureAwait(false))
            .Where(b => b.IsActive)
            .Select(b => b.TableDefId)
            .ToHashSet();
        var instances = await rows.GetTableInstancesAsync(request.DocumentId, periodKey, ct).ConfigureAwait(false);

        var mismatches = new List<ScriptParityMismatch>();
        var targets = 0;
        var rowCount = 0;
        var valueCount = 0;
        foreach (var instance in instances
                     .Where(i => i.SheetCopyId is null && bound.Contains(i.TableDefId))
                     .OrderBy(i => i.TableInstanceId))
        {
            var report = await comparer
                .CompareAsync(request.MethodologyVersionId, request.Text, request.DocumentId, periodKey, instance.TableInstanceId, ct)
                .ConfigureAwait(false);
            targets++;
            rowCount += report.RowsCompared;
            valueCount += report.ValuesCompared;
            mismatches.AddRange(report.Mismatches.Select(m =>
                new ScriptParityMismatch(m.TableInstanceId, m.RowKey, m.Aspect, m.Old, m.New)));
        }

        return new ScriptParityResult(targets, rowCount, valueCount, mismatches);
    }
}
