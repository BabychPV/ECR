using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Scripting;

namespace Ecr.Calculations;

/// <summary>
/// Вибір рядків таблиці реченнями <c>rows from … where … active when …</c> версії-скрипта (MS, план §3.6, §4.5).
/// </summary>
/// <remarks>
/// ⛔ Таблиця читається РІВНО тим самим кодом, що й у старому зіставленні правил
/// (<c>MethodologyResolver.ReadTableAsync</c>: екземпляр, ключі рядків, зріз з дефолтами колонок), і результат несе той
/// самий <see cref="TableSliceRead"/>, щоб <see cref="CalculationInputBuilder"/> не читав зріз удруге.
/// <para>
/// ⚠ «Текст збігу» колонки — <see cref="MethodologyRuleMatcher.Text"/>, ключ — КОД колонки (не Id) зі знімка
/// метаданих версії шаблону екземпляра: скрипт переносний між версіями шаблону, правила за Id — ні.
/// </para>
/// </remarks>
public sealed class ScriptRowSelector(
    MethodologyResolver resolver,
    IMetadataCache metadata,
    IScriptRuntimeStore? templates = null)
{
    /// <summary>Підбирає рядки екземпляра таблиці.</summary>
    /// <param name="script">Скрипт версії.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці документа.</param>
    /// <param name="entryIdText">
    /// Текст Id запису довідника за (код довідника, код запису) — для <c>entry()</c> в умовах;
    /// <c>null</c> результат — «запису немає» (нічому не дорівнює). Зазвичай <see cref="ScriptRuntime.EntryIdText"/>.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Збіги (код правила — код таблиці), ключі всіх рядків, «неактивні» рядки й уже прочитаний зріз.
    /// Екземпляр таблиці, коду якої в скрипті немає, — <c>RowMatchOutcome([], null)</c> (як версія без правил).
    /// </returns>
    public async Task<RowMatchOutcome> MatchAsync(
        CompiledScript script,
        long tableInstanceId,
        Func<string, string, string?> entryIdText,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(entryIdText);

        var table = await resolver.ReadTableAsync(tableInstanceId, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(table.Instance.TemplateVersionId, ct).ConfigureAwait(false);
        var tableCode = ScriptRuntime.TableCodeOf(snapshot, table.Instance.TableDefId);

        string? templateCode = null;
        if (templates is not null && script.Rows.Any(c => c.Tables.Any(t => t.TemplateCode is not null)))
        {
            var codes = await templates.GetTemplateCodesAsync([table.Instance.TemplateVersionId], ct).ConfigureAwait(false);
            templateCode = codes.GetValueOrDefault(table.Instance.TemplateVersionId);
        }

        var clauses = script.Rows
            .Where(c => c.Tables.Any(t =>
                string.Equals(t.TableCode, tableCode, StringComparison.OrdinalIgnoreCase)
                && (t.TemplateCode is null
                    || string.Equals(t.TemplateCode, templateCode, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        if (clauses.Count == 0)
        {
            return new RowMatchOutcome([], null);
        }

        var codeById = snapshot.ColumnsById.ToDictionary(p => p.Key, p => p.Value.Code);
        var byRow = table.Slice
            .GroupBy(c => c.Address.TableRowId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var texts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var cell in g)
                    {
                        if (codeById.TryGetValue(cell.Address.ColumnDefId, out var code))
                        {
                            texts[code] = MethodologyRuleMatcher.Text(cell.Value);
                        }
                    }

                    return texts;
                });

        var matched = new List<RowRuleMatch>();
        var excluded = new List<string>();
        var empty = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rowKey, rowId) in table.RowIds)
        {
            var texts = byRow.GetValueOrDefault(rowId) ?? empty;
            string? ColumnText(string column) => texts.GetValueOrDefault(column);

            // Спершу «активність» (рядок, де вона хибна, — неактивний), потім «де». Речення без active when
            // нікого не виключає; без where — усі активні рядки.
            var isActive = true;
            var isMatch = false;
            foreach (var clause in clauses)
            {
                if (clause.Active is { } active && !MatchEvaluator.Evaluate(active, ColumnText, entryIdText))
                {
                    isActive = false;
                    continue;
                }

                isActive = true;
                if (clause.Where is null || MatchEvaluator.Evaluate(clause.Where, ColumnText, entryIdText))
                {
                    isMatch = true;
                    break;
                }
            }

            if (isMatch)
            {
                matched.Add(new RowRuleMatch(rowKey, tableCode));
            }
            else if (!isActive)
            {
                excluded.Add(rowKey);
            }
        }

        return new RowMatchOutcome(
            matched,
            [.. table.RowIds.OrderBy(r => r.Value).Select(r => r.Key)],
            excluded)
        {
            Read = new TableSliceRead(tableInstanceId, table.Period, table.Slice, table.RowIds),
        };
    }
}
