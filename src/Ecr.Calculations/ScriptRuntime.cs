using System.Collections.Concurrent;
using System.Globalization;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Scripting;

namespace Ecr.Calculations;

/// <summary>Служби, потрібні рантаймові завантажувачам скрипта (екземпляри таблиць, рядки-псевдоніми, результати).</summary>
internal sealed record ScriptDataAccess(
    IRowStore Rows,
    ICellStore Cells,
    IMetadataCache Metadata,
    CalculationInputBuilder InputBuilder,
    IMethodologyStore Methodologies,
    ICalculationResultStore Results,
    IScriptRuntimeStore? Templates,
    ScriptRunContext? Run,
    CalculationLimits Limits,
    IMonthColumnRouter? MonthRouter = null);

/// <summary>Екземпляр таблиці, який читає скрипт: структура, карти місячних колонок, результати інших методологій.</summary>
internal sealed class InstanceData(
    TableInstanceRef reference,
    string tableCode,
    IReadOnlySet<string> columnCodes,
    IReadOnlyDictionary<(string Name, int Month), string> monthColumns,
    IReadOnlyDictionary<string, IReadOnlyList<CalculationArgument>> calculated)
{
    /// <summary>Посилання на екземпляр (документ, період, копія аркуша).</summary>
    public TableInstanceRef Reference { get; } = reference;

    /// <summary>Код таблиці.</summary>
    public string TableCode { get; } = tableCode;

    /// <summary>Коди колонок таблиці (без регістру).</summary>
    public IReadOnlySet<string> ColumnCodes { get; } = columnCodes;

    /// <summary>
    /// Результати інших методологій, що ввійшли в рядки як значення колонок <c>Calculated</c> (§13.9), за ключем рядка.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CalculationArgument>> Calculated { get; } = calculated;

    /// <summary>Код колонки місячного входу <c>X</c> за місяць: спершу псевдонім, потім <c>X_Mmm</c>; <c>null</c> — немає.</summary>
    /// <param name="name">Вхід <c>X</c>.</param>
    /// <param name="month">Місяць 1…12.</param>
    public string? MonthColumn(string name, int month)
        => monthColumns.TryGetValue((name.ToUpperInvariant(), month), out var code) ? code : null;
}

/// <summary>Рядок таблиці-псевдоніма: аргументи за кодом колонки (без регістру).</summary>
internal sealed record AliasRow(string RowKey, IReadOnlyDictionary<string, CalculationArgument> Arguments, InstanceData Instance);

/// <summary>
/// Вантаж рушія скрипта на одну прив'язку (методологія × період): програма, довідник одиниць, Id записів
/// <c>entry()</c> і ліниві завантажувачі екземплярів таблиць та рядків-псевдонімів (MS, план §4.4, §13).
/// </summary>
/// <remarks>
/// ⛔ Таблиці-псевдоніми й результати інших методологій читаються ЛІНИВО, на першому рядку екземпляра, а не в
/// <c>PrepareAsync</c>: контракт підготовки не знає екземпляра таблиці, а правило копії аркуша (§13.1) залежить саме
/// від нього. Читання — раз на (екземпляр, псевдонім) і далі з пам'яті; сумарний обсяг комірок обмежено
/// <see cref="CalculationLimits.MaxInputCellsPerBinding"/> (<c>ECR-CALC-4222</c>).
/// </remarks>
public sealed class ScriptRuntime
{
    private readonly ScriptDataAccess _data;
    private readonly MethodologyDescriptor _descriptor;
    private readonly PeriodKey _periodKey;
    private readonly long _documentId;

    private readonly ConcurrentDictionary<long, Lazy<Task<InstanceData>>> _instances = new();
    private readonly ConcurrentDictionary<(CompiledScript Script, string Alias, long? SheetCopy), Lazy<Task<IReadOnlyList<AliasRow>>>> _aliases = new();
    private readonly ConcurrentDictionary<CompiledScript, HashSet<string>> _optionalFields = new();
    private readonly Lazy<Task<IReadOnlyList<TableInstanceRef>>> _documentInstances;
    private readonly Dictionary<string, long> _entries;
    private readonly HashSet<string> _wantedNames;
    private readonly List<string> _monthlyPrefixes;
    private long _aliasCells;
    private MethodologyPhaseReadCache? _phaseReads;
    private MethodologyResolver? _tables;

    internal ScriptRuntime(
        LoadedProgram program,
        ScriptDataAccess data,
        MethodologyDescriptor descriptor,
        long documentId,
        PeriodKey periodKey,
        UnitTable units,
        IReadOnlyDictionary<int, string> unitCodes,
        IReadOnlyDictionary<string, int> unitIds,
        Dictionary<string, long> entries)
    {
        Program = program;
        _data = data;
        _descriptor = descriptor;
        _documentId = documentId;
        _periodKey = periodKey;
        Units = units;
        UnitCodes = unitCodes;
        UnitIds = unitIds;
        _entries = entries;

        // ✎ PSS-08: ліниво — до першої таблиці-псевдоніма оркестратор уже під'єднав кеш виклику (UsePhaseReads).
        _documentInstances = new Lazy<Task<IReadOnlyList<TableInstanceRef>>>(
            () => _phaseReads is { } reads
                ? reads.DocumentInstancesAsync(
                    _documentId, _periodKey, () => _data.Rows.GetTableInstancesAsync(_documentId, _periodKey, CancellationToken.None))
                : _data.Rows.GetTableInstancesAsync(_documentId, _periodKey, CancellationToken.None));

        _wantedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _monthlyPrefixes = [];
        foreach (var script in AllScripts(program))
        {
            foreach (var field in script.RowFields)
            {
                _wantedNames.Add(field.Name);
                if (field.IsMonthly)
                {
                    _monthlyPrefixes.Add(field.Name + "_M");
                }
            }

            foreach (var columns in script.TableColumns.Values)
            {
                foreach (var column in columns)
                {
                    _wantedNames.Add(column);
                }
            }
        }
    }

    /// <summary>
    /// PSS-08: екземпляри, рядки й зрізи таблиць читати з кешу фази методологій поточного виклику оркестратора.
    /// </summary>
    /// <param name="reads">Кеш виклику (документ × період).</param>
    /// <param name="tables">Резолвер гілки з тим самим кешем — зріз таблиці-псевдоніма з дефолтами колонок.</param>
    /// <remarks>
    /// ⚠ До першого рядка (одразу після <c>PrepareAsync</c>): завантажувачі ліниві й ще нічого не читали. Без виклику
    /// (перегляд, паритет, золоті тести) рантайм читає сховища сам, як до PSS-08. Результати інших методологій
    /// (<c>calc.*</c>) кеш не тримає — вони читаються як і раніше (MS §13.9).
    /// </remarks>
    internal void UsePhaseReads(MethodologyPhaseReadCache reads, MethodologyResolver tables)
    {
        _phaseReads = reads ?? throw new ArgumentNullException(nameof(reads));
        _tables = tables ?? throw new ArgumentNullException(nameof(tables));
    }

    /// <summary>Програма й бібліотеки.</summary>
    internal LoadedProgram Program { get; }

    /// <summary>Довідник одиниць для <c>CONVERT</c>.</summary>
    internal UnitTable Units { get; }

    /// <summary>Id одиниці → код.</summary>
    internal IReadOnlyDictionary<int, string> UnitCodes { get; }

    /// <summary>Код одиниці (без регістру) → Id.</summary>
    internal IReadOnlyDictionary<string, int> UnitIds { get; }

    /// <summary>Усі скрипти прив'язки: версія й її бібліотеки.</summary>
    internal static IEnumerable<CompiledScript> AllScripts(LoadedProgram program)
        => program.AllLibraries().Select(l => l.Script).Prepend(program.Main);

    /// <summary>Id запису довідника для <c>entry(довідник, код)</c>; <c>null</c> — не знайдено.</summary>
    /// <param name="registryCode">Код довідника.</param>
    /// <param name="entryCode">Код запису.</param>
    public long? EntryId(string registryCode, string entryCode)
        => _entries.TryGetValue(EntryKey(registryCode, entryCode), out var id) ? id : null;

    /// <summary>
    /// Текст Id запису для умов вибору рядків (<c>entry()</c> у <c>rows … where</c>); <c>null</c> — запису немає.
    /// </summary>
    /// <param name="registryCode">Код довідника.</param>
    /// <param name="entryCode">Код запису.</param>
    public string? EntryIdText(string registryCode, string entryCode)
        => EntryId(registryCode, entryCode)?.ToString(CultureInfo.InvariantCulture);

    internal static string EntryKey(string registryCode, string entryCode)
        => registryCode.ToUpperInvariant() + "\u0001" + entryCode.ToUpperInvariant();

    /// <summary>
    /// Дані екземпляра рядка. ✎ D-4: тест золотого набору не має екземпляра (<c>tableInstanceId = 0</c>, документ і період
    /// задано) — як і формульний модуль, скрипт рахує його на ПОРОЖНЬОМУ власному екземплярі: таблиці-псевдоніми
    /// (<c>rows from</c>) шукаються за документом і періодом (<see cref="AliasRowsAsync"/>), а не за власним екземпляром.
    /// Без цього <c>ResolveTableInstanceAsync(0)</c> давав <c>404 ECR-DOC-0404 tableInstance</c> і скрипт із річною
    /// розкладкою (<c>YearWithMonthColumns</c>) не публікувався.
    /// </summary>
    internal Task<InstanceData> InstanceAsync(long tableInstanceId, CancellationToken ct)
        => tableInstanceId > 0 ? ResolvedInstanceAsync(tableInstanceId, ct) : Task.FromResult(VirtualInstance());

    private InstanceData VirtualInstance()
        => new(
            new TableInstanceRef(0, _documentId, 0, 0, _periodKey.Value),
            string.Empty,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<(string Name, int Month), string>(),
            EmptyCalculated);

    private Task<InstanceData> ResolvedInstanceAsync(long tableInstanceId, CancellationToken ct)
        => _instances.GetOrAdd(
            tableInstanceId,
            id => new Lazy<Task<InstanceData>>(() => LoadInstanceAsync(id, ct))).Value;

    /// <summary>Рядки таблиці-псевдоніма для поточного екземпляра (правило копії аркуша, §13.1).</summary>
    internal Task<IReadOnlyList<AliasRow>> AliasRowsAsync(
        CompiledScript script, string alias, InstanceData current, CancellationToken ct)
        => _aliases.GetOrAdd(
            (script, alias, current.Reference.SheetCopyId),
            key => new Lazy<Task<IReadOnlyList<AliasRow>>>(() => LoadAliasAsync(key.Script, key.Alias, current, ct))).Value;

    /// <summary>Поле рядка необов'язкове в цьому скрипті: жодне звернення до нього не без «?».</summary>
    /// <param name="script">Скрипт (версія або бібліотека).</param>
    /// <param name="name">Ім'я поля.</param>
    internal bool IsOptionalField(CompiledScript script, string name)
        => _optionalFields
            .GetOrAdd(
                script,
                s => new HashSet<string>(s.RowFields.Where(f => f.IsOptional).Select(f => f.Name), StringComparer.OrdinalIgnoreCase))
            .Contains(name);

    private async Task<InstanceData> LoadInstanceAsync(long tableInstanceId, CancellationToken ct)
    {
        var reference = _phaseReads is { } reads
            ? await reads.InstanceAsync(tableInstanceId, () => _data.Rows.ResolveTableInstanceAsync(tableInstanceId, ct)).ConfigureAwait(false)
            : await _data.Rows.ResolveTableInstanceAsync(tableInstanceId, ct).ConfigureAwait(false);
        var snapshot = await _data.Metadata.GetAsync(reference.TemplateVersionId, ct).ConfigureAwait(false);

        var tableCode = TableCodeOf(snapshot, reference.TableDefId);
        var columns = snapshot.ColumnsById.Values
            .Where(c => c.TableDefId == reference.TableDefId && !c.IsDeleted)
            .ToList();

        var codes = new HashSet<string>(columns.Select(c => c.Code), StringComparer.OrdinalIgnoreCase);

        // §13.2: спершу псевдонім cfg.MonthInputAlias, потім колонка `<X>_M<mm>`; псевдонім має перевагу.
        var months = new Dictionary<(string Name, int Month), string>();
        for (var month = 1; month <= 12; month++)
        {
            foreach (var alias in snapshot.MonthInputAliasesFor(reference.TableDefId, month))
            {
                if (snapshot.ColumnsById.TryGetValue(alias.ColumnDefId, out var aliased))
                {
                    months.TryAdd((alias.Alias.ToUpperInvariant(), month), aliased.Code);
                }
            }
        }

        foreach (var column in columns.Where(c => c is { IsMonthColumn: true, MonthNumber: not null }))
        {
            var month = column.MonthNumber!.Value;
            var suffix = "_M" + month.ToString("00", CultureInfo.InvariantCulture);
            if (column.Code.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && column.Code.Length > suffix.Length)
            {
                months.TryAdd((column.Code[..^suffix.Length].ToUpperInvariant(), month), column.Code);
            }
        }

        var table = snapshot.Sheets.SelectMany(sheet => sheet.Tables).FirstOrDefault(t => t.Id == reference.TableDefId);
        var calculated = await LoadCalculatedAsync(reference, table, columns, ct).ConfigureAwait(false);
        return new InstanceData(reference, tableCode, codes, months, calculated);
    }

    internal static string TableCodeOf(TemplateVersionSnapshot snapshot, int tableDefId)
        => snapshot.Sheets
               .SelectMany(s => s.Tables)
               .FirstOrDefault(t => t.Id == tableDefId)
               ?.Code
           ?? string.Empty;

    private bool IsWanted(string columnCode)
        => _wantedNames.Contains(columnCode)
           || _monthlyPrefixes.Any(p => columnCode.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Результати ІНШИХ методологій для колонок <c>Calculated</c>, які читає скрипт (§13.9): значення за правилом
    /// накладення сітки (<see cref="CalculatedResultMatching"/>), але з результатів поточного прогону.
    /// </summary>
    /// <remarks>
    /// ✎ LAND-YEAR L2b: колонка з ЧУЖИМ власником (місячна m версії YearWithMonthColumns → період m) читається з періоду-власника
    /// — тим самим правилом, що в сітці (<see cref="CalculatedOwnerRouting"/>, <see cref="CalculatedOwnerResults"/>): числа
    /// актуального прогону періоду-власника й його екземпляра цієї таблиці, а не числа поточного періоду. Період-власник —
    /// інший прогін, тож поточний прогін його не рахує: «в прогоні» для такої колонки не буває.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<CalculationArgument>>> LoadCalculatedAsync(
        TableInstanceRef reference, TableDef? table, IReadOnlyList<ColumnDef> columns, CancellationToken ct)
    {
        var wanted = columns
            .Where(c => c.DataType == CellDataType.Calculated && IsWanted(c.Code))
            .ToDictionary(c => c.Id);
        if (wanted.Count == 0)
        {
            return EmptyCalculated;
        }

        var bindings = (await _data.Methodologies
                .GetColumnResultBindingsAsync([reference.TableDefId], ct)
                .ConfigureAwait(false))
            .Where(b => b.TableDefId == reference.TableDefId
                        && wanted.ContainsKey(b.ColumnDefId)
                        && b.MethodologyId != _descriptor.MethodologyId)
            .ToList();
        if (bindings.Count == 0)
        {
            return EmptyCalculated;
        }

        // Колонки з чужим власником — окремо: їхні числа лежать в іншому періоді (те саме правило, що в сітці).
        var owners = table is null
            ? null
            : await CalculatedOwnerRouting
                .OwnerPeriodsAsync(_data.MonthRouter, reference.DocumentId, reference, table, wanted.Keys.ToHashSet(), ct)
                .ConfigureAwait(false);
        bool IsForeign(ColumnResultBinding binding) => owners?.ContainsKey(binding.ColumnDefId) == true;
        var ownBindings = bindings.Where(b => !IsForeign(b)).ToList();
        var foreignBindings = bindings.Where(IsForeign).ToList();

        IReadOnlyList<CalculationResultRow> inRun = [];
        IReadOnlyList<CalculationResultRow> current = [];
        HashSet<int> runVersions = [];
        if (ownBindings.Count > 0)
        {
            var filter = new CurrentResultFilter(
                [reference.TableInstanceId],
                [.. ownBindings.Select(b => b.OutputCode).Distinct(StringComparer.OrdinalIgnoreCase)],
                [.. ownBindings.SelectMany(b => b.VersionIds).Distinct()]);

            inRun = _data.Run?.CalculationRunId is { } run
                ? await _data.Results.ReadRunAsync(run, reference.DocumentId, reference.PeriodKey, filter, ct).ConfigureAwait(false)
                : [];

            // Методологія-джерело, якої цей прогін не рахував (аркуш затверджено, прогін частковий), — її актуальні
            // або перенесені результати.
            // ⛔ «В прогоні» — те, що прогін РАХУЄ для цього екземпляра, а не лише те, що дало рядки: джерело без жодного
            // результату все одно порожнє, а не «попередній актуальний прогін».
            runVersions = inRun.Select(r => r.MethodologyVersionId).ToHashSet();
            if (_data.Run?.RunBindings is { } runBindings)
            {
                runVersions.UnionWith(ownBindings
                    .SelectMany(b => b.VersionIds)
                    .Where(v => runBindings.Contains((v, reference.TableInstanceId))));
            }

            var needCurrent = ownBindings.Any(b => !b.VersionIds.Any(runVersions.Contains));
            if (needCurrent)
            {
                current = _data.Results is ICurrentResultFilterReader narrow
                    ? await narrow.ReadCurrentMatchingAsync(reference.DocumentId, reference.PeriodKey, filter, ct).ConfigureAwait(false)
                    : [.. (await _data.Results.ReadCurrentAsync(reference.DocumentId, reference.PeriodKey, ct).ConfigureAwait(false))
                        .Where(r => r.TableInstanceId is null || r.TableInstanceId == reference.TableInstanceId)];
            }
        }

        var ownerResults = CalculatedOwnerResults.Empty;
        if (foreignBindings.Count > 0)
        {
            // Читач залежить від числа ІНШОГО періоду: прогін мусить знати, що його результат може застаріти (RecalculationJob).
            var ownerPeriods = foreignBindings.Select(b => owners![b.ColumnDefId]).Distinct().Order().ToList();
            _data.Run?.NoteForeignOwnerRead(ownerPeriods);
            ownerResults = await CalculatedOwnerResults
                .ReadAsync(_data.Results, reference.DocumentId, [(reference.TableDefId, ownerPeriods)], ct)
                .ConfigureAwait(false);
        }

        var runByKey = inRun.Where(r => r.SourceRowKey is not null)
            .ToLookup(r => (Row: r.SourceRowKey!, Output: r.OutputCode.ToUpperInvariant()));
        var currentByKey = current.Where(r => r.SourceRowKey is not null)
            .ToLookup(r => (Row: r.SourceRowKey!, Output: r.OutputCode.ToUpperInvariant()));
        var ownerByRow = ownerResults.Results
            .Where(r => r.SourceRowKey is not null)
            .ToLookup(r => (Period: r.PeriodKey, Row: r.SourceRowKey!, Output: r.OutputCode.ToUpperInvariant()));

        var rowKeys = inRun.Concat(current)
            .Where(r => r.SourceRowKey is not null)
            .Select(r => r.SourceRowKey!)
            .Concat(ownerResults.Results.Where(r => r.SourceRowKey is not null).Select(r => r.SourceRowKey!))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // Текст комірок рядка потрібен лише предикатам прив'язок, які не «уся таблиця».
        Dictionary<string, Dictionary<string, string?>>? textsByRow = null;
        if (bindings.Any(b => !CalculatedResultMatching.IsCatchAll(b.MatchJson)))
        {
            // ✎ PSS-08: СИРИЙ зріз (без дефолтів колонок, як і було) — з кешу виклику, якщо він є.
            var instancePeriod = new PeriodKey(reference.PeriodKey);
            var slice = await SliceAsync(reference.TableInstanceId, instancePeriod, ct).ConfigureAwait(false);
            var rowIds = await RowIdsAsync(reference.TableInstanceId, instancePeriod, ct).ConfigureAwait(false);
            var byRowId = slice.GroupBy(c => c.Address.TableRowId).ToDictionary(
                g => g.Key,
                g => g.ToDictionary(
                    c => c.Address.ColumnDefId.ToString(CultureInfo.InvariantCulture),
                    c => MethodologyRuleMatcher.Text(c.Value),
                    StringComparer.Ordinal));
            textsByRow = rowIds.ToDictionary(
                p => p.Key,
                p => byRowId.GetValueOrDefault(p.Value) ?? new Dictionary<string, string?>(StringComparer.Ordinal),
                StringComparer.Ordinal);
        }

        var result = new Dictionary<string, IReadOnlyList<CalculationArgument>>(StringComparer.Ordinal);
        foreach (var rowKey in rowKeys)
        {
            var filled = new HashSet<int>();
            var arguments = new List<CalculationArgument>();
            IReadOnlyDictionary<string, string?> Texts()
                => textsByRow?.GetValueOrDefault(rowKey)
                   ?? (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>(StringComparer.Ordinal);

            // Та сама черга, що в накладенні сітки: прив'язка з НАЙБІЛЬШИМ Id, яка дала значення, виграє.
            foreach (var binding in Enumerable.Reverse(bindings))
            {
                if (filled.Contains(binding.ColumnDefId))
                {
                    continue;
                }

                var output = binding.OutputCode.ToUpperInvariant();
                List<CalculationResultRow> candidates;
                long? ownInstance = reference.TableInstanceId;
                if (owners is not null && owners.TryGetValue(binding.ColumnDefId, out var ownerPeriod))
                {
                    // Колонка з чужим власником: екземпляр-власник цієї таблиці й числа його періоду.
                    ownInstance = ownerResults.InstanceOf(ownerPeriod, reference.TableDefId);
                    candidates = [.. ownerByRow[(ownerPeriod, rowKey, output)]
                        .Where(r => binding.VersionIds.Contains(r.MethodologyVersionId))
                        .Select(r => new CalculationResultRow(
                            r.MethodologyVersionId, r.SourceRowKey, r.OutputCode, r.Value, r.UnitId, r.SubstanceEntryId,
                            r.TableInstanceId))];
                }
                else
                {
                    var source = binding.VersionIds.Any(runVersions.Contains) ? runByKey : currentByKey;
                    candidates = [.. source[(rowKey, output)].Where(r => binding.VersionIds.Contains(r.MethodologyVersionId))];
                }

                var matched = CalculatedResultMatching.Resolve(binding.MatchJson, candidates, ownInstance, Texts);
                if (matched.Kind == CalculatedMatchKind.NoValue)
                {
                    continue;
                }

                filled.Add(binding.ColumnDefId);
                if (matched.Kind == CalculatedMatchKind.Value)
                {
                    arguments.Add(new CalculationArgument(wanted[binding.ColumnDefId].Code, matched.Total, null, matched.UnitId));
                }
            }

            if (arguments.Count > 0)
            {
                result[rowKey] = arguments;
            }
        }

        return result;
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<CalculationArgument>> EmptyCalculated =
        new Dictionary<string, IReadOnlyList<CalculationArgument>>(StringComparer.Ordinal);

    private async Task<IReadOnlyList<AliasRow>> LoadAliasAsync(
        CompiledScript script, string alias, InstanceData current, CancellationToken ct)
    {
        if (!script.Tables.TryGetValue(alias, out var tableRef))
        {
            return [];
        }

        var instances = await _documentInstances.Value.ConfigureAwait(false);

        // Яка таблиця: код (і код шаблону, якщо його вказано) екземпляра збігається з оголошенням `table`.
        var templateCodes = tableRef.TemplateCode is null || _data.Templates is null
            ? null
            : await _data.Templates
                .GetTemplateCodesAsync([.. instances.Select(i => i.TemplateVersionId).Distinct()], ct)
                .ConfigureAwait(false);

        var matching = new List<TableInstanceRef>();
        foreach (var instance in instances)
        {
            var snapshot = await _data.Metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
            if (!string.Equals(TableCodeOf(snapshot, instance.TableDefId), tableRef.TableCode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (tableRef.TemplateCode is not null
                && !(templateCodes is not null
                     && templateCodes.TryGetValue(instance.TemplateVersionId, out var code)
                     && string.Equals(code, tableRef.TemplateCode, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            matching.Add(instance);
        }

        // ⛔ §13.1: рядок копії аркуша бачить екземпляри ТІЄЇ САМОЇ копії; немає таких — оригінал.
        var chosen = current.Reference.SheetCopyId is { } copy
            ? matching.Where(i => i.SheetCopyId == copy).ToList()
            : [];
        if (chosen.Count == 0)
        {
            chosen = [.. matching.Where(i => i.SheetCopyId is null)];
        }

        var result = new List<AliasRow>();
        foreach (var instance in chosen.OrderBy(i => i.TableInstanceId))
        {
            var data = await InstanceAsync(instance.TableInstanceId, ct).ConfigureAwait(false);

            // ✎ PSS-08: екземпляр того самого періоду, що й прогін, — зріз з дефолтами й рядки з кешу виклику: рівно те, що
            // `BuildAsync` прочитав би сам (зріз за періодом екземпляра, рядки за періодом прогону — тут це той самий
            // період, і доповнення дефолтами те саме). Інший період — власне читання, як було.
            TableSliceRead? read = null;
            IReadOnlyDictionary<string, long> rowIds;
            if (_tables?.PhaseReads is not null && data.Reference.PeriodKey == _periodKey.Value)
            {
                var table = await _tables.ReadTableAsync(instance.TableInstanceId, ct).ConfigureAwait(false);
                rowIds = table.RowIds;
                read = new TableSliceRead(instance.TableInstanceId, table.Period, table.Slice, table.RowIds);
            }
            else
            {
                rowIds = await RowIdsAsync(instance.TableInstanceId, _periodKey, ct).ConfigureAwait(false);
            }

            IReadOnlyList<string> keys = [.. rowIds.OrderBy(r => r.Value).Select(r => r.Key)];
            var inputs = await _data.InputBuilder
                .BuildAsync(instance.TableInstanceId, keys, _periodKey, _descriptor, ct, read)
                .ConfigureAwait(false);

            foreach (var input in inputs)
            {
                var cells = Interlocked.Add(ref _aliasCells, input.Arguments.Count);
                if (cells > _data.Limits.MaxInputCellsPerBinding)
                {
                    throw new DomainException(
                        Domain.Errors.ErrorCodes.CalculationInputTooLarge,
                        $"Таблиці-псевдоніми методології {_descriptor.Code} мають {cells} комірок — більше за бюджет "
                        + $"{_data.Limits.MaxInputCellsPerBinding} "
                        + $"({CalculationLimits.SectionName}:{nameof(CalculationLimits.MaxInputCellsPerBinding)}).",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-CALC-4222.inputCellsOverBudget",
                            ["code"] = _descriptor.Code,
                            ["tableInstanceId"] = instance.TableInstanceId.ToString(CultureInfo.InvariantCulture),
                            ["cells"] = cells.ToString(CultureInfo.InvariantCulture),
                            ["limit"] = _data.Limits.MaxInputCellsPerBinding.ToString(CultureInfo.InvariantCulture),
                        });
                }

                var rowKey = input.SourceRowKey ?? string.Empty;
                result.Add(new AliasRow(rowKey, ArgumentsOf(input.Arguments, data.Calculated.GetValueOrDefault(rowKey)), data));
            }
        }

        return result;
    }

    private Task<IReadOnlyList<CellRecord>> SliceAsync(long tableInstanceId, PeriodKey period, CancellationToken ct)
        => _phaseReads is { } reads
            ? reads.SliceAsync(tableInstanceId, period, () => _data.Cells.ReadSliceAsync(tableInstanceId, period, ct))
            : _data.Cells.ReadSliceAsync(tableInstanceId, period, ct);

    private Task<IReadOnlyDictionary<string, long>> RowIdsAsync(long tableInstanceId, PeriodKey period, CancellationToken ct)
        => _phaseReads is { } reads
            ? reads.RowIdsAsync(tableInstanceId, period, () => _data.Rows.GetRowIdsAsync(tableInstanceId, period, ct))
            : _data.Rows.GetRowIdsAsync(tableInstanceId, period, ct);

    /// <summary>Аргументи рядка за кодом колонки (без регістру); результати інших методологій додаються й перекривають.</summary>
    internal static Dictionary<string, CalculationArgument> ArgumentsOf(
        IReadOnlyList<CalculationArgument> arguments, IReadOnlyList<CalculationArgument>? calculated)
    {
        var map = new Dictionary<string, CalculationArgument>(arguments.Count + (calculated?.Count ?? 0), StringComparer.OrdinalIgnoreCase);
        foreach (var argument in arguments)
        {
            map.TryAdd(argument.ArgumentCode, argument);
        }

        if (calculated is not null)
        {
            foreach (var argument in calculated)
            {
                map[argument.ArgumentCode] = argument;
            }
        }

        return map;
    }
}
