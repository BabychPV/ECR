// src/Ecr.Application/Documents/RequiredInputs/RequiredInputsScanner.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents.RequiredInputs;

/// <summary>
/// ✎ D-577 (L6, Q6): незаповнені обов'язкові входи методологій по ВСІХ рядках екземпляра таблиці — той самий обчислювач,
/// що ворота запису (<see cref="MethodologyRequiredInputs"/>), над уже прочитаним зрізом.
/// </summary>
/// <remarks>
/// ⛔ BUG-9 (UX-огляд): вимогу перевіряв лише PATCH і лише для рядка, який правили. Рядок, якого не торкались, проходив
/// подання мовчки, а позначка «!» зникала після перезавантаження. Тепер перелік є одразу при відкритті
/// (<c>GET …/tables/{id}/required-inputs</c>), а подання (Q6, рішення людини 26.11) блокується порожнім входом рівня Block.
/// <para>
/// ⚠ Ціна (Land — до 174 тис. рядків документа): план (методології таблиці) — ≤ 6 звернень на ТАБЛИЦЮ незалежно від рядків
/// і жодного, якщо до таблиці не прив'язано методології з вимогами; оцінка — у пам'яті, лінійно від комірок зрізу (один
/// прохід групування). Зріз читає викликач ОДНИМ запитом (як сітка й подання).
/// </para>
/// <para>
/// ⚠ Які рядки (Q4, рішення людини 26.11): рядок, де введено хоч одне значення, або наперед заданий рядок шаблону
/// (<see cref="TableDef.Rows"/>); порожні «запасні» динамічні рядки — без позначок і не блокують подання.
/// </para>
/// </remarks>
public sealed class RequiredInputsScanner(
    IMethodologyStore methodologies,
    IPeriodStore periods,
    IColumnPathMapper? columnMapper = null,
    IRowActivityFilterSource? rowActivity = null)
{
    /// <summary>Чинні методології таблиці з вимогами на період документа; <c>null</c> — перевіряти нічого (зріз не потрібен).</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період екземпляра.</param>
    /// <param name="templateVersionId">Версія шаблону екземпляра.</param>
    /// <param name="table">Таблиця версії екземпляра (виведені вимоги — за кодами її колонок, D-578).</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<RequiredInputPlan?> PlanAsync(
        long documentId, int periodKey, int templateVersionId, TableDef table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        var methodologyIds = await methodologies.GetMethodologyIdsBoundToTableAsync(table.Id, ct).ConfigureAwait(false);
        if (methodologyIds.Count == 0)
        {
            return null;
        }

        var bounds = await periods.FindPeriodBoundsAsync(documentId, periodKey, ct).ConfigureAwait(false);
        if (bounds is null)
        {
            return null;
        }

        var applicable = await new MethodologyRequiredInputs(methodologies, columnMapper, rowActivity)
            .ResolveAsync(methodologyIds, bounds.PeriodEnd, templateVersionId, table.Id, ct, derivedFor: table)
            .ConfigureAwait(false);
        return applicable.Count == 0 ? null : new RequiredInputPlan(applicable, MethodologyRequiredInputs.ColumnIds(applicable));
    }

    /// <summary>Незаповнені входи всіх рядків зрізу (порядок — рядки за <c>Id</c>, колонки — перша поява).</summary>
    /// <param name="plan">План (<see cref="PlanAsync"/>).</param>
    /// <param name="table">Таблиця.</param>
    /// <param name="columnsById">Колонки версії (заголовки для тексту).</param>
    /// <param name="cells">Зріз екземпляра (логічний — з колонками власників, як у сітці).</param>
    /// <param name="rowIds">Рядки екземпляра: ключ → <c>Id</c>.</param>
    /// <param name="language">Мова тексту.</param>
    /// <returns>Повідомлення <c>ECR-CALC-0437</c> з колонкою (вимога без колонки у версії — C1 — пропускається).</returns>
    public static List<Validation.ValidationMessage> Scan(
        RequiredInputPlan plan,
        TableDef table,
        IReadOnlyDictionary<int, ColumnDef> columnsById,
        IReadOnlyList<CellRecord> cells,
        IReadOnlyDictionary<string, long> rowIds,
        string? language)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(rowIds);

        var byRow = new Dictionary<long, Dictionary<int, CellValueData>>();
        var started = new HashSet<long>();
        foreach (var cell in cells)
        {
            var rowId = cell.Address.TableRowId;
            if (!byRow.TryGetValue(rowId, out var row))
            {
                byRow[rowId] = row = [];
            }

            row[cell.Address.ColumnDefId] = cell.Value;
            if (!cell.Value.IsEmpty && !cell.Value.IsCalculated)
            {
                started.Add(rowId);
            }
        }

        var predefined = table.Rows.Select(r => r.RowKeyValue).ToHashSet(StringComparer.Ordinal);
        var rendered = new Dictionary<(int Column, string Codes, bool Error, bool Blocks, LocalizedText? Hint), Validation.ValidationMessage>();
        var messages = new List<Validation.ValidationMessage>();
        foreach (var (rowKey, rowId) in rowIds.OrderBy(kv => kv.Value))
        {
            if (!started.Contains(rowId) && !predefined.Contains(rowKey))
            {
                continue;
            }

            var row = byRow.GetValueOrDefault(rowId);
            string? ValueOf(int columnId)
                => row is not null && row.TryGetValue(columnId, out var value) && !value.IsEmpty
                    ? MethodologyRequiredInputs.Text(value)
                    : null;

            // Як у воротах запису (C-5, B175): немає запису комірки — дефолт колонки заповнює вимогу; явна порожнеча — ні.
            bool IsFilled(int columnId)
                => ValueOf(columnId) is not null
                   || ((row is null || !row.ContainsKey(columnId))
                       && Recalculation.ColumnDefaultValues.HasEffectiveDefault(table, rowKey, columnId));

            var missing = MethodologyRequiredInputs.EvaluateRow(plan.Applicable, plan.ColumnIds, ValueOf, IsFilled, humanBatch: true);
            messages.AddRange(missing.Messages(table.Id, columnsById, rowKey, language, rendered).Where(m => m.ColumnCode is not null));
        }

        return messages;
    }
}

/// <summary>Чинні методології таблиці з вимогами і колонки, які треба прочитати.</summary>
/// <param name="Applicable">Методології.</param>
/// <param name="ColumnIds">Колонки зіставлення правил і вимог.</param>
public sealed record RequiredInputPlan(IReadOnlyList<ApplicableMethodology> Applicable, IReadOnlyList<int> ColumnIds);
