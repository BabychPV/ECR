using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions;

namespace Ecr.Calculations;

/// <summary>
/// Модуль рівня 3 для «транспонованих» методологій (HSE400): набір аргументів — КОЛОНКА МІСЯЦЯ, а не рядок.
/// </summary>
/// <remarks>
/// <para>
/// Таблиці «місяці в колонках» (<see cref="TableLayoutKind.MonthsInColumns"/>, колонки з
/// <see cref="ColumnDef.IsMonthColumn"/> і <see cref="ColumnDef.MonthNumber"/> 1..12) одного аркуша документа
/// читаються разом. Для кожного місяця комірки рядків-входів стають аргументами
/// <c>GCV.&lt;простір&gt;_&lt;таблиця&gt;_&lt;RowKey&gt;</c> (і те саме з <c>GCV_</c>), формули версії рахує
/// той самий <see cref="GenericCalculationModule"/> (рушій, константи, трейс, маскування — без другої
/// семантики), а результат формули <c>&lt;простір&gt;_&lt;таблиця&gt;_&lt;X&gt;</c> лягає в рядок
/// <c>F_&lt;X&gt;</c> тієї таблиці з кодом виходу = код колонки місяця.
/// </para>
/// <para>
/// ⛔ Рядок <c>F_…</c> ніколи не читається як аргумент, а в рядок-вхід ніколи не пишеться результат: код входу й
/// виходу в HSE400 збігаються (<c>Nitrogen</c>), і без префікса формула читала б власний результат.
/// </para>
/// <para>
/// ⛔ Місяць без жодної введеної комірки пропускається цілком — без результатів і без кроків-помилок.
/// Замасковане в нуль значення (<c>Legacy</c>, ділення на нуль) і все, що від нього залежить через
/// <c>!Формула</c>, у результат НЕ потрапляє: порожня комірка з кроком-причиною, а не нуль.
/// </para>
/// <para>
/// ⛔ Виняток — правило старої системи «нульова молекулярна вага»: якщо рядок <see cref="ZeroMolecularWeightRow"/>
/// таблиці в місяці дорівнює рівно нулю, УСІ рядки <c>F_…</c> цієї таблиці в цьому місяці — нуль (і замасковані теж),
/// без кроків-помилок. Стара система (<c>CalculationBaseAdv.Advanced</c>, CalculationBaseAdv.cs:292–301) так
/// обриває розрахунок частини HSE400, щойно результат з <c>MolecularWeight</c> у ключі дорівнює «0», і звіт
/// HSE400.Year показує нулі на весь місяць без вводу. Правило діє лише в цьому модулі (рівень <c>Module</c>), а не в
/// семантиці виразів.
/// </para>
/// <para>
/// У БД модуль не пише (D-69): результати повертаються оркестратору, запис робить
/// <see cref="CalculationOutputWriter"/> у <c>calc.CalculationResult</c>.
/// </para>
/// </remarks>
public sealed partial class MonthColumnArgumentsModule : ICalculationModule
{
    /// <summary>Префікс ключа обчислюваного рядка (контракт шаблону HSE400).</summary>
    public const string CalculatedRowPrefix = "F_";

    /// <summary>
    /// Рядок, чий нуль обнуляє всю таблицю місяця (правило старої системи, див. зауваження до класу).
    /// </summary>
    public const string ZeroMolecularWeightRow = CalculatedRowPrefix + "MolecularWeight";

    /// <summary>Код безрозмірної одиниці — запасна одиниця результату.</summary>
    private const string DimensionlessUnitCode = "one";

    private readonly GenericCalculationModule _inner;
    private readonly IUnitCatalog _unitCatalog;
    private readonly ICellStore _cells;
    private readonly IMetadataCache _metadata;
    private readonly IRowStore _rows;

    private readonly ConcurrentDictionary<(long DocumentId, int PeriodKey), Lazy<Task<IReadOnlyList<TableInstanceRef>>>> _instances = new();
    private readonly ConcurrentDictionary<(int VersionId, long DocumentId, int PeriodKey, long SheetCopy), Lazy<Task<SheetRun>>> _runs = new();

    /// <summary>Створює модуль; залежності рушія — ті самі, що в <see cref="GenericCalculationModule"/>.</summary>
    public MonthColumnArgumentsModule(
        IFormulaEngine formulaEngine,
        IMethodologyStore methodologies,
        ConstantResolver constants,
        CalendarContext calendar,
        IUnitCatalog unitCatalog,
        IPeriodStore periods,
        ICalculationBindingStore bindingStore,
        ICellStore cells,
        IMetadataCache metadata,
        IRowStore rows,
        IRegistryStore? registryStore = null,
        IRegistrySnapshotLoader? registryLoader = null)
    {
        ArgumentNullException.ThrowIfNull(unitCatalog);
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(rows);

        _inner = new GenericCalculationModule(
            formulaEngine, methodologies, constants, calendar, unitCatalog, periods, bindingStore, registryStore, registryLoader);
        _unitCatalog = unitCatalog;
        _cells = cells;
        _metadata = metadata;
        _rows = rows;
    }

    [GeneratedRegex(@"!([A-Za-z][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex FormulaReferenceRegex();

    /// <inheritdoc />
    public string Code => "month-columns";

    /// <inheritdoc />
    public CalculationLevel Level => CalculationLevel.Module;

    /// <inheritdoc />
    public bool CanHandle(MethodologyDescriptor methodology)
    {
        ArgumentNullException.ThrowIfNull(methodology);
        return methodology.Level == CalculationLevel.Module;
    }

    /// <inheritdoc />
    public Task<CalculationBindingContext> PrepareAsync(
        MethodologyDescriptor methodology, long documentId, PeriodKey periodKey, CancellationToken ct)
        => _inner.PrepareAsync(methodology, documentId, periodKey, ct);

    /// <inheritdoc />
    public Task<CalculationBindingContext> PrepareAsync(
        MethodologyDescriptor methodology,
        long documentId,
        PeriodKey periodKey,
        RegistrySnapshotCache? registries,
        CancellationToken ct)
        => _inner.PrepareAsync(methodology, documentId, periodKey, registries, ct);

    /// <inheritdoc />
    public async Task<CalculationOutput> ExecuteAsync(CalculationInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var context = await PrepareAsync(input.Methodology, input.DocumentId, input.PeriodKey, ct).ConfigureAwait(false);
        return await ExecuteAsync(context, input, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Оркестратор кличе метод раз на РЯДОК прив'язаної таблиці. Аркуш рахується один раз (перший виклик), далі
    /// кожен рядок <c>F_…</c> отримує свої значення всіх місяців, а рядок-вхід — порожній результат.
    /// </remarks>
    public async Task<CalculationOutput> ExecuteAsync(
        CalculationBindingContext binding, CalculationInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(input);

        if (binding.Methodology.MethodologyVersionId != input.Methodology.MethodologyVersionId
            || binding.DocumentId != input.DocumentId
            || binding.PeriodKey != input.PeriodKey)
        {
            throw new ArgumentException(
                $"Контекст прив'язки (версія {binding.Methodology.MethodologyVersionId}, документ "
                + $"{binding.DocumentId}, період {binding.PeriodKey.Value}) не відповідає рядку "
                + $"(версія {input.Methodology.MethodologyVersionId}, документ {input.DocumentId}, "
                + $"період {input.PeriodKey.Value}).",
                nameof(binding));
        }

        var instances = await _instances
            .GetOrAdd(
                (input.DocumentId, input.PeriodKey.Value),
                _ => new Lazy<Task<IReadOnlyList<TableInstanceRef>>>(
                    () => _rows.GetTableInstancesAsync(input.DocumentId, input.PeriodKey, ct)))
            .Value.ConfigureAwait(false);

        var own = instances.FirstOrDefault(i => i.TableInstanceId == input.TableInstanceId)
            ?? throw NotMonthColumnTable(binding.Methodology, input.TableInstanceId);

        var run = await _runs
            .GetOrAdd(
                (binding.Methodology.MethodologyVersionId, input.DocumentId, input.PeriodKey.Value, own.SheetCopyId ?? 0),
                _ => new Lazy<Task<SheetRun>>(() => ComputeAsync(
                    binding, input, [.. instances.Where(i => i.SheetCopyId == own.SheetCopyId)], ct)))
            .Value.ConfigureAwait(false);

        if (!run.Tables.Contains(input.TableInstanceId))
        {
            throw NotMonthColumnTable(binding.Methodology, input.TableInstanceId);
        }

        return input.SourceRowKey is { } rowKey && run.Outputs.TryGetValue((input.TableInstanceId, rowKey), out var output)
            ? output
            : new CalculationOutput(input.DocumentId, input.SourceRowKey, [], [], null, input.TableInstanceId);
    }

    /// <summary>Чи це обчислюваний рядок (<c>F_…</c>): не вхід і єдине місце, куди лягає результат.</summary>
    /// <param name="rowKey">Ключ рядка.</param>
    public static bool IsCalculatedRow(string rowKey)
        => rowKey is not null && rowKey.StartsWith(CalculatedRowPrefix, StringComparison.OrdinalIgnoreCase);

    private static Domain.Abstractions.DomainException NotMonthColumnTable(MethodologyDescriptor methodology, long tableInstanceId)
        => new(
            "ECR-CALC-0422",
            $"Методологія {methodology.Code} рівня {methodology.Level} прив'язана до таблиці {tableInstanceId}, "
            + "яка не є таблицею «місяці в колонках».",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-CALC-0422.noModule",
                ["code"] = methodology.Code,
                ["level"] = $"{methodology.Level}",
            });

    private async Task<SheetRun> ComputeAsync(
        CalculationBindingContext binding,
        CalculationInput input,
        IReadOnlyList<TableInstanceRef> group,
        CancellationToken ct)
    {
        var tables = await LoadTablesAsync(group, input.PeriodKey, ct).ConfigureAwait(false);
        var targets = MapFormulas(binding.Formulas, tables);
        var namespaces = Namespaces(targets);
        var dimensionless = (await _unitCatalog.GetAsync(ct).ConfigureAwait(false))
            .Units.TryGetValue(DimensionlessUnitCode, out var one) ? one.Id : (int?)null;
        var dependencies = binding.Formulas.ToDictionary(
            f => f.Code,
            f => FormulaReferenceRegex().Matches(f.Expression).Select(m => m.Groups[1].Value).ToList(),
            StringComparer.OrdinalIgnoreCase);

        // ⚠ `Off` не пише кроків, а без кроку маскування не відрізнити замаскований нуль від справжнього.
        // Тому рушій іде на `ErrorsOnly`, а кроки назовні не віддаються — рівень версії дотримано.
        var silent = binding.Methodology.TraceLevel == TraceLevel.Off;
        var descriptor = silent ? binding.Methodology with { TraceLevel = TraceLevel.ErrorsOnly } : binding.Methodology;

        var rows = new Dictionary<(long TableInstanceId, string RowKey), RowResult>();

        foreach (var month in tables.SelectMany(t => t.MonthColumns.Keys).Distinct().Order())
        {
            var arguments = new List<CalculationArgument>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var filled = new HashSet<long>();
            foreach (var table in tables)
            {
                if (!table.Inputs.TryGetValue(month, out var cells))
                {
                    continue;
                }

                filled.Add(table.Instance.TableInstanceId);
                var column = table.MonthColumns[month];
                foreach (var (rowKey, value) in cells)
                {
                    foreach (var ns in namespaces(table))
                    {
                        foreach (var name in (string[])[$"GCV.{ns}_{table.Code}_{rowKey}", $"GCV_{ns}_{table.Code}_{rowKey}"])
                        {
                            if (names.Add(name))
                            {
                                arguments.Add(new CalculationArgument(
                                    name, value.ValueNumeric, value.ValueNumeric is null ? value.ValueString : null,
                                    value.ValueUnitId ?? column.UnitId));
                            }
                        }
                    }
                }
            }

            // ⛔ Порожній місяць: ні результатів, ні кроків-помилок (`#ARG` на кожну формулу був би шумом).
            if (filled.Count == 0)
            {
                continue;
            }

            var outputs = new List<MethodologyOutput>();
            var scales = new Dictionary<string, byte?>(StringComparer.OrdinalIgnoreCase);
            var noUnit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in targets)
            {
                if (!target.Table.MonthColumns.TryGetValue(month, out var column))
                {
                    continue;
                }

                if ((target.Formula.OutputUnitId ?? column.UnitId ?? dimensionless) is not { } unitId)
                {
                    noUnit.Add(target.Formula.Code);
                    continue;
                }

                outputs.Add(new MethodologyOutput(
                    binding.Methodology.MethodologyVersionId, EcrCode.Create(target.Formula.Code), unitId));
                scales[target.Formula.Code] = column.Scale;
            }

            var result = await _inner.ExecuteAsync(
                    binding with { Methodology = descriptor, Outputs = outputs, OutputScales = scales },
                    new CalculationInput(
                        descriptor, input.DocumentId, input.TableInstanceId, input.PeriodKey,
                        MonthKey(month), arguments),
                    ct)
                .ConfigureAwait(false);

            Collect(result, targets, month, filled, noUnit, dependencies, silent, outputs, rows);
        }

        return new SheetRun(
            [.. tables.Select(t => t.Instance.TableInstanceId)],
            rows.ToDictionary(
                r => r.Key,
                r => new CalculationOutput(
                    input.DocumentId,
                    r.Key.RowKey,
                    r.Value.Values,
                    [.. r.Value.Steps.Select((s, i) => s with { StepOrder = i + 1 })],
                    null,
                    r.Key.TableInstanceId)));
    }

    /// <summary>Розкладає результат одного місяця по рядках <c>F_…</c>.</summary>
    private static void Collect(
        CalculationOutput result,
        IReadOnlyList<FormulaTarget> targets,
        int month,
        HashSet<long> filled,
        HashSet<string> noUnit,
        Dictionary<string, List<string>> dependencies,
        bool silent,
        IReadOnlyList<MethodologyOutput> outputs,
        Dictionary<(long TableInstanceId, string RowKey), RowResult> rows)
    {
        var firstStep = new Dictionary<string, CalculationTraceStep>(StringComparer.OrdinalIgnoreCase);
        var masked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in result.Trace)
        {
            firstStep.TryAdd(step.StepCode, step);
            if (step.Masked != MaskedZeroReason.None)
            {
                masked.Add(step.StepCode);
            }
        }

        // ⛔ Нуль, що виріс із замаскованого ділення на нуль, — не результат: залежні формули теж порожні.
        var tainted = new HashSet<string>(masked, StringComparer.OrdinalIgnoreCase);
        for (var grown = true; grown;)
        {
            grown = false;
            foreach (var (code, references) in dependencies)
            {
                if (!tainted.Contains(code) && references.Any(tainted.Contains))
                {
                    grown = tainted.Add(code);
                }
            }
        }

        var zeroTables = ZeroMolecularWeightTables(result, targets, tainted);

        foreach (var target in targets)
        {
            if (!target.Table.MonthColumns.TryGetValue(month, out var column))
            {
                continue;
            }

            var key = (target.Table.Instance.TableInstanceId, target.RowKey);
            if (!rows.TryGetValue(key, out var row))
            {
                rows[key] = row = new RowResult([], []);
            }

            var code = target.Formula.Code;

            // ⛔ Нульова молекулярна вага: уся таблиця місяця — нуль, як у старій системі; кроків-помилок немає.
            if (zeroTables.Contains(target.Table.Instance.TableInstanceId))
            {
                if (outputs.FirstOrDefault(o => string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase))
                    is { } declared)
                {
                    row.Values.Add(new CalculationOutputValue(
                        declared.MethodologyVersionId, null, column.Code, 0m, declared.UnitId));
                }

                continue;
            }

            var broken = tainted.Contains(code) || noUnit.Contains(code);
            if (!broken)
            {
                row.Values.AddRange(result.Values
                    .Where(v => string.Equals(v.OutputCode, code, StringComparison.OrdinalIgnoreCase))
                    .Select(v => v with { OutputCode = column.Code }));
            }

            if (silent)
            {
                continue;
            }

            firstStep.TryGetValue(code, out var recorded);
            var failure = broken || recorded is { TraceJson: not null };

            // Таблиця без жодного входу цього місяця: її невдалі формули — шум порожнього місяця, а не помилка.
            if (failure && !filled.Contains(target.Table.Instance.TableInstanceId))
            {
                continue;
            }

            if (broken && recorded is not { Masked: not MaskedZeroReason.None } and not { TraceJson: not null })
            {
                recorded = new CalculationTraceStep(
                    0,
                    code,
                    target.Formula.Expression,
                    null,
                    noUnit.Contains(code) ? ExpressionErrors.BadUnit : ExpressionErrors.DivideByZero);
            }

            if (recorded is not null)
            {
                row.Steps.Add(recorded with { StepCode = column.Code });
            }
        }
    }

    /// <summary>
    /// Таблиці, у яких рядок <see cref="ZeroMolecularWeightRow"/> цього місяця пораховано рівно в нуль (не замаскований).
    /// </summary>
    private static HashSet<long> ZeroMolecularWeightTables(
        CalculationOutput result, IReadOnlyList<FormulaTarget> targets, HashSet<string> tainted)
    {
        var zero = new HashSet<long>();
        foreach (var target in targets)
        {
            if (!string.Equals(target.RowKey, ZeroMolecularWeightRow, StringComparison.OrdinalIgnoreCase)
                || tainted.Contains(target.Formula.Code))
            {
                continue;
            }

            var values = result.Values
                .Where(v => string.Equals(v.OutputCode, target.Formula.Code, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (values.Count > 0 && values.All(v => v.Value == 0m))
            {
                zero.Add(target.Table.Instance.TableInstanceId);
            }
        }

        return zero;
    }

    private async Task<List<MonthTable>> LoadTablesAsync(
        IReadOnlyList<TableInstanceRef> group, PeriodKey periodKey, CancellationToken ct)
    {
        var tables = new List<MonthTable>();
        foreach (var instance in group)
        {
            var snapshot = await _metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
            var definition = snapshot.Sheets.SelectMany(s => s.Tables).FirstOrDefault(t => t.Id == instance.TableDefId);
            if (definition is null || definition.IsDeleted || definition.LayoutKind != TableLayoutKind.MonthsInColumns)
            {
                continue;
            }

            var monthColumns = new Dictionary<int, ColumnDef>();
            foreach (var column in definition.Columns)
            {
                if (!column.IsDeleted && column.IsMonthColumn && column.MonthNumber is >= 1 and <= 12)
                {
                    monthColumns.TryAdd(column.MonthNumber.Value, column);
                }
            }

            if (monthColumns.Count == 0)
            {
                continue;
            }

            var rowIds = await _rows.GetRowIdsAsync(instance.TableInstanceId, periodKey, ct).ConfigureAwait(false);
            var slice = await _cells.ReadSliceAsync(instance.TableInstanceId, periodKey, ct).ConfigureAwait(false);

            var calculated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var inputRows = new Dictionary<long, string>();
            foreach (var (rowKey, rowId) in rowIds)
            {
                if (IsCalculatedRow(rowKey))
                {
                    calculated.TryAdd(rowKey, rowKey);
                }
                else
                {
                    inputRows[rowId] = rowKey;
                }
            }

            var monthByColumn = monthColumns.ToDictionary(c => c.Value.Id, c => c.Key);
            var inputs = new Dictionary<int, List<(string RowKey, CellValueData Value)>>();
            foreach (var cell in slice)
            {
                if (!monthByColumn.TryGetValue(cell.Address.ColumnDefId, out var month)
                    || !inputRows.TryGetValue(cell.Address.TableRowId, out var rowKey)
                    || (cell.Value.ValueNumeric is null && string.IsNullOrWhiteSpace(cell.Value.ValueString)))
                {
                    continue;
                }

                if (!inputs.TryGetValue(month, out var list))
                {
                    inputs[month] = list = [];
                }

                list.Add((rowKey, cell.Value));
            }

            tables.Add(new MonthTable(instance, definition.Code, monthColumns, calculated, inputs));
        }

        return tables;
    }

    /// <summary>
    /// Формула <c>&lt;простір&gt;_&lt;таблиця&gt;_&lt;X&gt;</c> → рядок <c>F_&lt;X&gt;</c> таблиці; формула без такого
    /// рядка — проміжна (<c>Common_LHV0_*</c>).
    /// </summary>
    private static List<FormulaTarget> MapFormulas(IReadOnlyList<MethodologyFormula> formulas, List<MonthTable> tables)
    {
        var targets = new List<FormulaTarget>();
        var byLength = tables.OrderByDescending(t => t.Code.Length).ToList();
        foreach (var formula in formulas)
        {
            foreach (var table in byLength)
            {
                var index = formula.Code.IndexOf($"_{table.Code}_", StringComparison.OrdinalIgnoreCase);
                if (index <= 0)
                {
                    continue;
                }

                var rest = formula.Code[(index + table.Code.Length + 2)..];
                if (rest.Length > 0 && table.CalculatedRows.TryGetValue(CalculatedRowPrefix + rest, out var rowKey))
                {
                    targets.Add(new FormulaTarget(formula, table, rowKey, formula.Code[..index]));
                }

                break;
            }
        }

        return targets;
    }

    /// <summary>Простори імен аргументів таблиці: з її формул, а якщо їх немає — з формул сусідніх таблиць.</summary>
    private static Func<MonthTable, IReadOnlyList<string>> Namespaces(List<FormulaTarget> targets)
    {
        var all = targets.Select(t => t.Namespace).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var own = targets
            .GroupBy(t => t.Table.Instance.TableInstanceId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(t => t.Namespace).Distinct(StringComparer.OrdinalIgnoreCase)]);

        return table => own.TryGetValue(table.Instance.TableInstanceId, out var list) ? list : all;
    }

    private static string MonthKey(int month) => "M" + month.ToString("00", CultureInfo.InvariantCulture);

    private sealed record MonthTable(
        TableInstanceRef Instance,
        string Code,
        Dictionary<int, ColumnDef> MonthColumns,
        Dictionary<string, string> CalculatedRows,
        Dictionary<int, List<(string RowKey, CellValueData Value)>> Inputs);

    private sealed record FormulaTarget(MethodologyFormula Formula, MonthTable Table, string RowKey, string Namespace);

    private sealed record RowResult(List<CalculationOutputValue> Values, List<CalculationTraceStep> Steps);

    private sealed record SheetRun(
        HashSet<long> Tables,
        Dictionary<(long TableInstanceId, string RowKey), CalculationOutput> Outputs);
}
