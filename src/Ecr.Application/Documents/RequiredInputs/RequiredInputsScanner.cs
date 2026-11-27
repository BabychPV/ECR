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

    /// <summary>
    /// ✎ Огляд M1: плани для КІЛЬКОХ таблиць (аркуш на поданні, документ на «Перевірити») — прив'язки одним зверненням, межі
    /// періоду один раз, читання методологій спільні для таблиць з тими самими методологіями (пам'ять на це звернення).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="templateVersionId">Версія шаблону екземплярів.</param>
    /// <param name="tables">Таблиці.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>План за <c>TableDefId</c> лише для таблиць, де є що перевіряти.</returns>
    public async Task<IReadOnlyDictionary<int, RequiredInputPlan>> PlanManyAsync(
        long documentId, int periodKey, int templateVersionId, IReadOnlyCollection<TableDef> tables, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var result = new Dictionary<int, RequiredInputPlan>();
        if (tables.Count == 0)
        {
            return result;
        }

        var bound = await methodologies
            .GetMethodologyIdsBoundPerTableAsync([.. tables.Select(t => t.Id)], ct).ConfigureAwait(false);
        if (!bound.Values.Any(ids => ids.Count > 0))
        {
            return result;
        }

        var bounds = await periods.FindPeriodBoundsAsync(documentId, periodKey, ct).ConfigureAwait(false);
        if (bounds is null)
        {
            return result;
        }

        var resolver = new MethodologyRequiredInputs(methodologies, columnMapper, rowActivity) { Memo = [] };
        await resolver
            .PrefetchAsync([.. bound.Values.SelectMany(ids => ids).Distinct()], bounds.PeriodEnd, ct)
            .ConfigureAwait(false);
        foreach (var table in tables)
        {
            if (bound.GetValueOrDefault(table.Id) is not { Count: > 0 } methodologyIds)
            {
                continue;
            }

            var applicable = await resolver
                .ResolveAsync(methodologyIds, bounds.PeriodEnd, templateVersionId, table.Id, ct, derivedFor: table)
                .ConfigureAwait(false);
            if (applicable.Count > 0)
            {
                result[table.Id] = new RequiredInputPlan(applicable, MethodologyRequiredInputs.ColumnIds(applicable));
            }
        }

        return result;
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
        => Scan(plan, table, columnsById, cells, rowIds, language, int.MaxValue, _ => true, out _);

    /// <summary>
    /// ✎ Огляд H4: те саме зі стелею — у список потрапляє щонайбільше <paramref name="limit"/> повідомлень, що проходять
    /// <paramref name="include"/>, а <paramref name="total"/> рахує всі такі (подання й «Перевірити» не будують мегабайтних
    /// переліків під винятковим замком).
    /// </summary>
    public static List<Validation.ValidationMessage> Scan(
        RequiredInputPlan plan,
        TableDef table,
        IReadOnlyDictionary<int, ColumnDef> columnsById,
        IReadOnlyList<CellRecord> cells,
        IReadOnlyDictionary<string, long> rowIds,
        string? language,
        int limit,
        Func<Validation.ValidationMessage, bool> include,
        out int total)
    {
        ArgumentNullException.ThrowIfNull(include);
        total = 0;
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
            foreach (var message in missing.Messages(table.Id, columnsById, rowKey, language, rendered))
            {
                if (message.ColumnCode is null || !include(message))
                {
                    continue;
                }

                total++;
                if (messages.Count < limit)
                {
                    messages.Add(message);
                }
            }
        }

        return messages;
    }
}

/// <summary>Чинні методології таблиці з вимогами і колонки, які треба прочитати.</summary>
/// <param name="Applicable">Методології.</param>
/// <param name="ColumnIds">Колонки зіставлення правил і вимог.</param>
public sealed record RequiredInputPlan(IReadOnlyList<ApplicableMethodology> Applicable, IReadOnlyList<int> ColumnIds);
