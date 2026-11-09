// src/Ecr.Application/Recalculation/RowLocalPreview.cs
using System.Runtime.CompilerServices;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Синхронний «перегляд» рядково-локальних формул для щойно відредагованих рядків (HSE401): оператор обрав код забруднювача -
/// Компонент/Фаза мають з'явитися одразу, а не після фонового перерахунку.
/// </summary>
public interface IRowLocalPreview
{
    /// <summary>Максимум відредагованих рядків, для яких рахується перегляд; більше - перегляду немає (<c>null</c>).</summary>
    public const int MaxRows = 20;

    /// <summary>Обчислює значення рядково-локальних формул колонок у відредагованих рядках.</summary>
    /// <param name="instance">Екземпляр таблиці.</param>
    /// <param name="editedRows">Ключ рядка → ідентифікатор для відредагованих рядків.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Комірки, значення яких відрізняється від збереженого; <c>null</c> - перегляд не застосовний.</returns>
    public Task<IReadOnlyList<ComputedCellDto>?> PreviewAsync(
        TableInstanceRef instance, IReadOnlyDictionary<string, long> editedRows, CancellationToken ct);
}

/// <summary>
/// Реалізація <see cref="IRowLocalPreview"/>: читає ЛИШЕ комірки відредагованих рядків (а не увесь зріз таблиці) і рахує ті
/// формули колонок, що рядково-локальні (<see cref="RowLocalFormulaClassifier"/>) разом із усім їхнім замиканням по колонках.
/// </summary>
/// <remarks>
/// ⚠ Це ПЕРЕГЛЯД, а не джерело істини: фоновий перерахунок (<c>FormulaRecalculationJob</c>) лишається єдиним, що пише
/// обчислені комірки. Тому тут свідомо консервативно - формула, яку не вдалося довести локальною й замкненою (HDR, інші рядки й
/// таблиці, форми <c>REG*</c>, формули рядка), просто не рахується.
/// <para>
/// ✎ D-461 (P1 06.11, HSE401 H_COMPONENT): формула з дозволом ручного значення рахується, а пропускаються лише КОМІРКИ з
/// позначкою (їхнє значення лишається в контексті для залежних). Раніше пропускалась уся формула — після «дозволити ручне»
/// Компонент не з'являвся одразу ні на зміну коду, ні на повернення формули, а чекав фонового перерахунку (на живому 38 с).
/// Без сховища позначок формула з дозволом не рахується (консервативно, як було).
/// </para>
/// </remarks>
public sealed class RowLocalPreview(
    IMetadataCache metadata,
    ICellStore cellStore,
    IFormulaEngine formulaEngine,
    IPeriodStore periods,
    IUnitCatalog unitCatalog,
    IRegistryStore registryStore,
    IFormulaOverrideStore? formulaOverrides = null) : IRowLocalPreview
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ComputedCellDto>?> PreviewAsync(
        TableInstanceRef instance, IReadOnlyDictionary<string, long> editedRows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(editedRows);

        if (editedRows.Count is 0 or > IRowLocalPreview.MaxRows)
        {
            return null;
        }

        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
        var index = RecalculationPlanBuilder.SnapshotIndex.For(snapshot);
        if (!index.Tables.TryGetValue(instance.TableDefId, out var table))
        {
            return null;
        }

        var previewable = Previewable(table, index);
        if (previewable.Count == 0)
        {
            return null;
        }

        var period = new PeriodKey(instance.PeriodKey);
        var columns = table.Columns.Where(c => !c.IsDeleted).ToList();
        var rowKeyById = editedRows.ToDictionary(pair => pair.Value, pair => pair.Key);

        var addresses = editedRows.Values
            .SelectMany(rowId => columns.Select(c => new CellAddress(period, rowId, c.Id)))
            .ToList();
        var stored = await cellStore.ReadCellsAsync(addresses, ct).ConfigureAwait(false);

        // ✎ D-461: позначки ручних значень — лише коли серед формул перегляду є формула з дозволом (одне читання).
        var manualAddresses = previewable.Where(p => p.Formula.AllowManualOverride)
            .SelectMany(p => editedRows.Values.Select(rowId => new CellAddress(period, rowId, p.Column.Id)))
            .ToList();
        HashSet<CellAddress> manual = manualAddresses.Count == 0 || formulaOverrides is null
            ? new HashSet<CellAddress>()
            : (await formulaOverrides.ReadAsync(manualAddresses, ct).ConfigureAwait(false)).Keys.ToHashSet();

        var values = new Dictionary<CellKey, ExpressionValue>();
        foreach (var (address, cell) in stored)
        {
            values[new CellKey(0, table.Id, rowKeyById[address.TableRowId], address.ColumnDefId)]
                = CellValueMapping.ToExpressionValue(cell, ExpressionValue.Null);
        }

        var registryFields = await RegistryFieldsAsync(table, previewable, editedRows, values, ct)
            .ConfigureAwait(false);

        var bounds = await periods.FindPeriodBoundsAsync(instance.DocumentId, period.Value, ct).ConfigureAwait(false);
        if (bounds is null)
        {
            return null;
        }

        var context = new SliceEvaluationContext(
            snapshot,
            values,
            new Dictionary<string, ExpressionValue>(),
            new Ecr.Expressions.PeriodContext(
                bounds.PeriodStart, bounds.PeriodEnd, CalendarMode.Actual, period.Year, (byte)period.Sequence),
            await unitCatalog.GetAsync(ct).ConfigureAwait(false),
            new Dictionary<int, IReadOnlyDictionary<string, long>> { [table.Id] = editedRows },
            registryFields);

        var result = new List<ComputedCellDto>();

        foreach (var (formula, parsed, column) in previewable)
        {
            context.UseFormula(formula, parsed.Root);

            foreach (var (rowKey, rowId) in editedRows)
            {
                context.CurrentTableDefId = table.Id;
                context.CurrentRowKey = rowKey;
                context.CurrentColumnDefId = column.Id;

                var evaluated = formulaEngine.Evaluate(parsed, context);
                var key = new CellKey(0, table.Id, rowKey, column.Id);
                var address = new CellAddress(period, rowId, column.Id);

                // ✎ D-461: ручне значення формула не перезаписує й у перегляді; залежні читають його (уже в `values`).
                if (manual.Contains(address))
                {
                    continue;
                }

                stored.TryGetValue(address, out var old);

                if (evaluated.Value.IsError)
                {
                    // Помилка не пишеться як значення; залежні формули прочитають порожнє - їхній перегляд може бути
                    // неповним, але фонова задача все виправить.
                    values.Remove(key);
                    continue;
                }

                if (evaluated.Value.IsNull)
                {
                    values[key] = ExpressionValue.Null;
                    if (old is { IsCalculated: true, IsEmpty: false })
                    {
                        result.Add(new ComputedCellDto(rowKey, column.Code, null));
                    }

                    continue;
                }

                var value = RecalculationService.AsStoredScale(evaluated.Value);
                values[key] = value;
                var data = RecalculationService.ToStoredCell(value);
                if (data is null || CellValueComparison.AreEqual(old, data))
                {
                    continue;
                }

                result.Add(new ComputedCellDto(rowKey, column.Code, CellValueMapping.ToRuleValue(data)));
            }
        }

        return result;
    }

    /// <summary>Формули колонок таблиці, які безпечно порахувати за одним рядком, у порядку обчислення.</summary>
    private List<(FormulaDef Formula, Ecr.Expressions.Parsing.ParsedExpression Parsed, ColumnDef Column)> Previewable(
        TableDef table, RecalculationPlanBuilder.SnapshotIndex index)
    {
        var own = index.Formulas.Values.Where(f => f.Table.Id == table.Id).Select(f => f.Formula).ToList();

        // Формула рядка (без явної колонки) може писати в будь-яку колонку - замикання тоді недоказове.
        if (own.Any(f => f.Scope != FormulaScope.Column || f.ColumnDefId is null))
        {
            return [];
        }

        var candidates = new Dictionary<int, (FormulaDef Formula, Ecr.Expressions.Parsing.ParsedExpression Parsed, ColumnDef Column, HashSet<int> Reads)>();
        var columnsByCode = table.Columns
            .Where(c => !c.IsDeleted)
            .GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var formula in own)
        {
            var column = table.Columns.FirstOrDefault(c => c.Id == formula.ColumnDefId && !c.IsDeleted);
            if (column is null
                || formula.IsSnapshot
                || (formula.AllowManualOverride && formulaOverrides is null))
            {
                continue;
            }

            var parsed = formulaEngine.Parse(formula.Expression, formula.Dialect).Expression;
            if (parsed is null
                || !RowLocalFormulaClassifier.IsRowLocal(formula, parsed.Root)
                || TemplateRegistryUse.Scan(parsed.Root, null)

                // SHEETNAME(): назви копії аркуша перегляд не читає — значення пише фоновий перерахунок.
                || SheetDisplayNames.MayUse([formula.Expression]))
            {
                continue;
            }

            var reads = new HashSet<int>();
            if (!CollectReads(parsed.Root, columnsByCode, reads))
            {
                continue;
            }

            candidates[formula.Id] = (formula, parsed, column, reads);
        }

        // Замикання: формула лишається, лише якщо кожна обчислювана колонка, яку вона читає, теж у переліку.
        var formulaTargets = own.Where(f => f.ColumnDefId is not null).Select(f => f.ColumnDefId!.Value).ToHashSet();
        bool changed;
        do
        {
            changed = false;
            var kept = candidates.Values.Select(c => c.Column.Id).ToHashSet();
            foreach (var (id, candidate) in candidates.ToList())
            {
                if (candidate.Reads.Any(read => formulaTargets.Contains(read) && !kept.Contains(read)))
                {
                    candidates.Remove(id);
                    changed = true;
                }
            }
        }
        while (changed);

        return [.. candidates.Values
            .OrderBy(c => c.Formula.EvaluationOrder)
            .ThenBy(c => c.Formula.Id)
            .Select(c => (c.Formula, c.Parsed, c.Column))];
    }

    /// <summary>Збирає колонки, які читає вираз; <c>false</c> - у виразі є невідома колонка чи вузол поза доведеною множиною.</summary>
    private static bool CollectReads(AstNode node, Dictionary<string, ColumnDef> columnsByCode, HashSet<int> reads)
    {
        // ⛔ L7-01: рекурсія за довжиною ланцюга виразу.
        RuntimeHelpers.EnsureSufficientExecutionStack();

        return node switch
        {
            LiteralNode or PeriodPropertyNode => true,
            UnaryNode unary => CollectReads(unary.Operand, columnsByCode, reads),
            BinaryNode binary => CollectReads(binary.Left, columnsByCode, reads) && CollectReads(binary.Right, columnsByCode, reads),
            ConditionalNode c => CollectReads(c.Condition, columnsByCode, reads)
                                 && CollectReads(c.WhenTrue, columnsByCode, reads)
                                 && CollectReads(c.WhenFalse, columnsByCode, reads),
            FunctionNode f => f.Arguments.All(a => CollectReads(a, columnsByCode, reads)),
            CellReferenceNode r when columnsByCode.TryGetValue(r.ColumnSelector, out var column) => AddRead(reads, column.Id),
            _ => false,
        };
    }

    private static bool AddRead(HashSet<int> reads, int columnId)
    {
        reads.Add(columnId);
        return true;
    }

    /// <summary>Поля довідника для <c>REGFIELD([Lookup], 'F')</c> у відредагованих рядках.</summary>
    private async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, ExpressionValue>>?> RegistryFieldsAsync(
        TableDef table,
        List<(FormulaDef Formula, Ecr.Expressions.Parsing.ParsedExpression Parsed, ColumnDef Column)> previewable,
        IReadOnlyDictionary<string, long> editedRows,
        Dictionary<CellKey, ExpressionValue> values,
        CancellationToken ct)
    {
        var requests = new HashSet<RegistryFieldRequest>();

        foreach (var (_, parsed, _) in previewable)
        {
            foreach (var call in RegFieldCalls(parsed.Root))
            {
                var source = table.Columns.FirstOrDefault(c =>
                    !c.IsDeleted && string.Equals(c.Code, call.ColumnSelector, StringComparison.OrdinalIgnoreCase));
                if (source?.LookupRegistryDefId is not { } registryDefId)
                {
                    continue;
                }

                foreach (var rowKey in editedRows.Keys)
                {
                    if (values.TryGetValue(new CellKey(0, table.Id, rowKey, source.Id), out var cell)
                        && cell.AsNumber() is { } entryId)
                    {
                        requests.Add(new RegistryFieldRequest((long)entryId, registryDefId, call.Field));
                    }
                }
            }
        }

        if (requests.Count == 0)
        {
            return null;
        }

        var snapshot = await unitCatalog.GetAsync(ct).ConfigureAwait(false);
        return await RegistryFieldSnapshotLoader.LoadAsync(registryStore, requests, ct, snapshot).ConfigureAwait(false);
    }

    /// <summary>Виклики <c>REGFIELD([Col], 'Field')</c> у виразі.</summary>
    private static IEnumerable<(string ColumnSelector, string Field)> RegFieldCalls(AstNode node)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();

        switch (node)
        {
            case FunctionNode f:
                if (string.Equals(f.Name, "REGFIELD", StringComparison.OrdinalIgnoreCase)
                    && f.Arguments is [CellReferenceNode r, LiteralNode { Value: string field }])
                {
                    yield return (r.ColumnSelector, field);
                }

                foreach (var inner in f.Arguments.SelectMany(RegFieldCalls))
                {
                    yield return inner;
                }

                break;
            case UnaryNode u:
                foreach (var inner in RegFieldCalls(u.Operand))
                {
                    yield return inner;
                }

                break;
            case BinaryNode b:
                foreach (var inner in RegFieldCalls(b.Left).Concat(RegFieldCalls(b.Right)))
                {
                    yield return inner;
                }

                break;
            case ConditionalNode c:
                foreach (var inner in RegFieldCalls(c.Condition).Concat(RegFieldCalls(c.WhenTrue)).Concat(RegFieldCalls(c.WhenFalse)))
                {
                    yield return inner;
                }

                break;
        }
    }
}
