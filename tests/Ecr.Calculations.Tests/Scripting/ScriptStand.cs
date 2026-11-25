using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Scripting;
using NSubstitute;

namespace Ecr.Calculations.Tests.Scripting;

/// <summary>Колонка таблиці стенда.</summary>
internal sealed record StandColumn(string Code, CellDataType Type = CellDataType.Decimal, byte? Month = null);

/// <summary>Посилання на запис довідника в комірці Lookup.</summary>
internal sealed record StandEntry(long Id);

/// <summary>Таблиця стенда: екземпляр, колонки й рядки (ключ → комірки за кодом колонки).</summary>
internal sealed class StandTable(long instanceId, int tableDefId, string code, long? sheetCopyId = null)
{
    public long InstanceId { get; } = instanceId;

    public int TableDefId { get; } = tableDefId;

    public string Code { get; } = code;

    public long? SheetCopyId { get; } = sheetCopyId;

    public List<StandColumn> Columns { get; } = [];

    public List<(string Key, Dictionary<string, object?> Cells)> Rows { get; } = [];

    public StandTable Column(string code, CellDataType type = CellDataType.Decimal, byte? month = null)
    {
        Columns.Add(new StandColumn(code, type, month));
        return this;
    }

    public StandTable Row(string key, params (string Column, object? Value)[] cells)
    {
        Rows.Add((key, cells.ToDictionary(c => c.Column, c => c.Value, StringComparer.OrdinalIgnoreCase)));
        return this;
    }
}

/// <summary>
/// Стенд скрипта без бази: справжні <see cref="ScriptCalculationModule"/>, <see cref="ScriptRowSelector"/>,
/// <see cref="CalculationInputBuilder"/> і <see cref="MethodologyResolver"/> над підставними сховищами. Таблиці, рядки,
/// колонки, довідники, бібліотеки й результати інших методологій задаються тестом.
/// </summary>
internal sealed class ScriptStand
{
    public const int VersionId = 7001;
    public const int MethodologyId = 70;
    public const long DocumentId = 209_901_000;
    public const int PeriodValue = 209901;
    public const int TemplateVersionId = 1;

    private readonly List<StandTable> _tables = [];
    private readonly List<(string Code, int VersionId, string Text, string[] Imports)> _libraries = [];
    private readonly List<(string Registry, string Entry, long Id)> _entries = [];
    private readonly List<(string Code, int Id)> _substances = [];
    private readonly Dictionary<string, int> _unitIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kg"] = 1,
        ["t"] = 2,
        ["g_per_s"] = 3,
        ["one"] = 4,
    };

    private readonly IMethodologyStore _store = Substitute.For<IMethodologyStore>();
    private readonly Dictionary<string, byte?> _outputScales = new(StringComparer.OrdinalIgnoreCase);
    private readonly ICalculationResultStore _results = Substitute.For<ICalculationResultStore>();
    private readonly ScriptRunContext _run = new();
    private readonly ScriptProgramCache _cache = new();
    private readonly List<ColumnResultBinding> _bindings = [];
    private ScriptCalculationModule? _module;
    private IRowStore? _rows;
    private IMetadataCache? _metadata;
    private CalculationInputBuilder? _inputBuilder;
    private int _nextColumnId = 100;

    public ScriptStand(string text, NumericMode mode = NumericMode.Strict)
    {
        Text = text;
        Mode = mode;
        Methodology = Descriptor(mode);
    }

    public string Text { get; private set; }

    public NumericMode Mode { get; }

    public TraceLevel Trace { get; set; } = TraceLevel.Full;

    public MethodologyDescriptor Methodology { get; private set; }

    public ScriptRunContext Run => _run;

    /// <summary>Ліміти прогону (бюджет комірок таблиць-псевдонімів); <c>null</c> — типові.</summary>
    public CalculationLimits? Limits { get; set; }

    /// <summary>Межі періоду документа стенда (типово січень 2099); річний документ — увесь рік.</summary>
    public (DateOnly Start, DateOnly End) Bounds { get; set; } = (new DateOnly(2099, 1, 1), new DateOnly(2099, 1, 31));

    /// <summary>
    /// Завантажувач знімків довідників (SGF registry-as-of); <c>null</c> — як досі, без довідників. Разом із ним — описи
    /// довідників <see cref="RegistryDefinitions"/>, які віддає <c>FindDefinitionsAsync</c>.
    /// </summary>
    public IRegistrySnapshotLoader? RegistryLoader { get; set; }

    /// <summary>Описи довідників для <c>IRegistryStore.FindDefinitionsAsync</c> (лише з <see cref="RegistryLoader"/>).</summary>
    public List<RegistryDef> RegistryDefinitions { get; } = [];

    /// <summary>Кеш знімків прогону, спільний для прив'язок (<c>null</c> — власний на кожну).</summary>
    public RegistrySnapshotCache? RegistryCache { get; set; }

    public IReadOnlyList<StandTable> Tables => _tables;

    public StandTable Main => _tables[0];

    public ICalculationResultStore Results => _results;

    public ScriptProgramCache Cache => _cache;

    public IMethodologyStore Store => _store;

    public static MethodologyDescriptor Descriptor(NumericMode mode, TraceLevel trace = TraceLevel.Full)
        => new(
            MethodologyId, VersionId, "MS_TEST", "1.0.0.0", CalculationLevel.Configuration, mode,
            CalendarMode.Actual, trace, MethodologyEngine.Script);

    public StandTable AddTable(string code, long? instanceId = null, int? tableDefId = null, long? sheetCopyId = null)
    {
        var index = _tables.Count;
        var table = new StandTable(instanceId ?? 500 + index, tableDefId ?? 10 + index, code, sheetCopyId);
        _tables.Add(table);
        return table;
    }

    public ScriptStand WithSubstances(params (string Code, int Id)[] substances)
    {
        _substances.AddRange(substances);
        return this;
    }

    public ScriptStand WithLibrary(string code, int versionId, string text, params string[] imports)
    {
        _libraries.Add((code, versionId, text, imports));
        return this;
    }

    /// <summary>Масштаб колонки-приймача виходу (<c>cfg.CalculationBinding</c> → <c>ListOutputScalesAsync</c>); <c>null</c> — «усі знаки».</summary>
    public ScriptStand WithOutputScale(string outputCode, byte? scale)
    {
        _outputScales[outputCode] = scale;
        return this;
    }

    public ScriptStand WithEntry(string registry, string entry, long id)
    {
        _entries.Add((registry, entry, id));
        return this;
    }

    public ScriptStand WithBinding(ColumnResultBinding binding)
    {
        _bindings.Add(binding);
        return this;
    }

    public int ColumnId(string table, string column)
        => 100 + ((_tables.FindIndex(t => t.Code == table) + 1) * 100) + _tables.First(t => t.Code == table).Columns.FindIndex(c => c.Code == column);

    public ScriptStand WithText(string text)
    {
        Text = text;
        return this;
    }

    /// <summary>Будує модуль і налаштовує підставні сховища під поточний стан стенда.</summary>
    public ScriptCalculationModule Module => _module ??= Build();

    public ScriptRowSelector Selector { get; private set; } = null!;

    public ScriptCalculationModule Rebuild()
    {
        _module = null;
        return Module;
    }

    private ScriptCalculationModule Build()
    {
        var compiledMain = ScriptCompiler.Compile(Text, new ScriptCompileOptions(Mode, IsLibrary: false, ResolveLibrary));
        var outputs = new List<MethodologyOutput>();
        if (compiledMain.Script is { } script)
        {
            foreach (var output in script.Outputs)
            {
                var item = new MethodologyOutput(
                    VersionId, EcrCode.Create(output.Code), _unitIds.GetValueOrDefault(output.UnitCode, 1), output.Ordinal);
                item.SetPerSubstance(output.IsPerSubstance);
                outputs.Add(item);
            }
        }

        _store.GetScriptAsync(VersionId, Arg.Any<CancellationToken>())
            .Returns(new MethodologyScript(Text, null, MethodologyEngine.Script, false, Mode));
        _store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(outputs);
        _store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>())
            .Returns([.. _substances.Select((s, i) => new MethodologySubstance(VersionId, s.Id, i + 1))]);
        _store.GetSubstanceCodesAsync(VersionId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<long, string>)_substances.ToDictionary(s => (long)s.Id, s => s.Code));
        _store.ResolveImportsAsync(VersionId, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(compiledMain.Script?.Imports.Select(Library).ToList() ?? []);
        foreach (var (code, versionId, text, imports) in _libraries)
        {
            _store.GetScriptAsync(versionId, Arg.Any<CancellationToken>())
                .Returns(new MethodologyScript(text, null, MethodologyEngine.Script, true, Mode));
            _store.ResolveImportsAsync(versionId, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
                .Returns(imports.Select(Library).ToList());
        }

        _store.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyList<ColumnResultBinding>)[.. _bindings.Where(b => ((IReadOnlyCollection<int>)ci[0]).Contains(b.TableDefId))]);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, PeriodValue, Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(Bounds.Start, Bounds.End));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            _unitIds.ToDictionary(p => p.Key, p => new UnitRef(p.Value, p.Key, (byte)p.Value, 1m), StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var period = new PeriodKey(PeriodValue);
        var rows = Substitute.For<IRowStore>();
        var cells = Substitute.For<ICellStore>();
        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(Snapshot());

        var refs = _tables
            .Select(t => new TableInstanceRef(t.InstanceId, DocumentId, t.TableDefId, TemplateVersionId, PeriodValue, t.SheetCopyId))
            .ToList();
        rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(refs);
        foreach (var table in _tables)
        {
            var reference = refs.First(r => r.TableInstanceId == table.InstanceId);
            rows.ResolveTableInstanceAsync(table.InstanceId, Arg.Any<CancellationToken>()).Returns(reference);
            rows.GetRowIdsAsync(table.InstanceId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                .Returns((IReadOnlyDictionary<string, long>)table.Rows
                    .Select((r, i) => (r.Key, Id: RowId(table, i)))
                    .ToDictionary(p => p.Key, p => p.Id, StringComparer.Ordinal));
            cells.ReadSliceAsync(table.InstanceId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                .Returns((IReadOnlyList<CellRecord>)Slice(table, period));
        }

        var inputBuilder = new CalculationInputBuilder(cells, metadata, rows);
        (_rows, _metadata, _inputBuilder) = (rows, metadata, inputBuilder);
        var resolver = new MethodologyResolver(_store, cells, rows);
        Selector = new ScriptRowSelector(resolver, metadata);

        var registries = Substitute.For<IRegistryStore>();
        registries.FindDefinitionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var definition = new RegistryDef(
                    EcrCode.Create((string)ci[0]), new LocalizedText(new Dictionary<string, string> { ["en"] = (string)ci[0] }), false);
                SetId(definition, Math.Abs(((string)ci[0]).GetHashCode(StringComparison.Ordinal)) % 1000 + 1);
                return definition;
            });
        registries.FindEntryByCodeAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var code = (string)ci[1];
                var known = _entries.FirstOrDefault(e => string.Equals(e.Entry, code, StringComparison.OrdinalIgnoreCase));
                if (known.Entry is null)
                {
                    return (RegistryEntry?)null;
                }

                var entry = new RegistryEntry(
                    (int)ci[0], EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }));
                SetId(entry, known.Id);
                return entry;
            });

        var bindingStore = Substitute.For<ICalculationBindingStore>();
        bindingStore.ListOutputScalesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, byte?>)new Dictionary<string, byte?>(_outputScales, StringComparer.OrdinalIgnoreCase));

        if (RegistryLoader is not null)
        {
            registries.FindDefinitionsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                .Returns(ci => (IReadOnlyList<RegistryDef>)[.. RegistryDefinitions.Where(d =>
                    ((IReadOnlyCollection<string>)ci[0]).Contains(d.Code, StringComparer.OrdinalIgnoreCase))]);
            bindingStore.ListLookupRegistryIdsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns((IReadOnlyList<int>)[]);
        }

        return new ScriptCalculationModule(
            new Evaluator(new FunctionRegistry()),
            _store,
            _cache,
            new CalendarContext(),
            units,
            periods,
            bindingStore,
            rows,
            cells,
            metadata,
            inputBuilder,
            _results,
            _run,
            registries,
            registryLoader: RegistryLoader,
            scriptStore: null,
            limits: Limits);
    }

    /// <summary>
    /// Справжній <see cref="ScriptPreviewRunner"/> над тими самими підставними сховищами, що й модуль: версія стенда — чернетка
    /// скрипта з режимом стенда.
    /// </summary>
    /// <param name="previewSteps">Спільний бюджет кроків перегляду; <c>null</c> — типовий.</param>
    public ScriptPreviewRunner PreviewRunner(int? previewSteps = null)
    {
        var module = Module;
        var methodology = new Methodology(EcrCode.Create(Methodology.Code), Text1(Methodology.Code));
        SetId(methodology, MethodologyId);
        var version = new MethodologyVersion(
            MethodologyId, Methodology.VersionNumber, CalculationLevel.Configuration, 1,
            new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc), MethodologyEngine.Script);
        version.SetModes(Mode, CalendarMode.Actual, Trace);
        SetId(version, VersionId);
        methodology.AddVersion(version);

        var drafts = Substitute.For<IMethodologyDraftStore>();
        drafts.FindVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(version);
        drafts.FindAsync(MethodologyId, Arg.Any<CancellationToken>()).Returns(methodology);

        return new ScriptPreviewRunner(
            drafts, [module], Selector, _inputBuilder!, _rows!, _metadata!,
            new Ecr.Application.Calculations.Scripting.ScriptSourceCompiler(drafts, _store))
        {
            PreviewEvaluationSteps = previewSteps ?? ScriptPreviewRunner.MaxPreviewEvaluationSteps,
        };
    }

    public async Task<CalculationInput> InputAsync(string rowKey, StandTable? table = null)
    {
        _ = Module;
        table ??= Main;
        var builder = new CalculationInputBuilder(
            CellsOf(table), MetadataOf(), RowsOf(table));
        var inputs = await builder.BuildAsync(table.InstanceId, [rowKey], new PeriodKey(PeriodValue), Methodology, CancellationToken.None);
        return inputs.Single();
    }

    public async Task<(CalculationOutput Output, IReadOnlyList<ScriptValue> Values)> RunAsync(
        string rowKey = "R1", StandTable? table = null)
    {
        var input = await InputAsync(rowKey, table);
        var context = await Module.PrepareAsync(Methodology, DocumentId, new PeriodKey(PeriodValue), RegistryCache, CancellationToken.None);
        return await Module.ExecuteDetailedAsync(context, input, CancellationToken.None);
    }

    /// <summary>Рядок без комірок: аргументи задаються напряму (парність із формулами).</summary>
    public async Task<CalculationOutput> RunArgumentsAsync(params CalculationArgument[] arguments)
    {
        var input = new CalculationInput(
            Methodology, DocumentId, Main.InstanceId, new PeriodKey(PeriodValue), "R1", arguments);
        var context = await Module.PrepareAsync(Methodology, DocumentId, new PeriodKey(PeriodValue), CancellationToken.None);
        return await Module.ExecuteAsync(context, input, CancellationToken.None);
    }

    public async Task<RowMatchOutcome> SelectAsync(StandTable? table = null)
    {
        var context = await Module.PrepareAsync(Methodology, DocumentId, new PeriodKey(PeriodValue), CancellationToken.None);
        var runtime = (ScriptRuntime)context.ScriptRuntime!;
        return await Selector.MatchAsync(
            context.Script!, (table ?? Main).InstanceId, runtime.EntryIdText, CancellationToken.None);
    }

    // ───── внутрішнє ─────

    private ICellStore CellsOf(StandTable table)
    {
        var cells = Substitute.For<ICellStore>();
        foreach (var t in _tables)
        {
            cells.ReadSliceAsync(t.InstanceId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                .Returns((IReadOnlyList<CellRecord>)Slice(t, new PeriodKey(PeriodValue)));
        }

        return cells;
    }

    private IRowStore RowsOf(StandTable table)
    {
        var rows = Substitute.For<IRowStore>();
        foreach (var t in _tables)
        {
            rows.ResolveTableInstanceAsync(t.InstanceId, Arg.Any<CancellationToken>())
                .Returns(new TableInstanceRef(t.InstanceId, DocumentId, t.TableDefId, TemplateVersionId, PeriodValue, t.SheetCopyId));
            rows.GetRowIdsAsync(t.InstanceId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                .Returns((IReadOnlyDictionary<string, long>)t.Rows
                    .Select((r, i) => (r.Key, Id: RowId(t, i)))
                    .ToDictionary(p => p.Key, p => p.Id, StringComparer.Ordinal));
        }

        return rows;
    }

    private IMetadataCache MetadataOf()
    {
        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(Snapshot());
        return metadata;
    }

    private static long RowId(StandTable table, int index) => (table.InstanceId * 1000) + index + 1;

    private CompiledScript? ResolveLibrary(string code)
    {
        var library = _libraries.FirstOrDefault(l => l.Code == code);
        if (library.Code is null)
        {
            return null;
        }

        return ScriptCompiler.Compile(
            library.Text, new ScriptCompileOptions(Mode, IsLibrary: true, ResolveLibrary)).Script;
    }

    private MethodologyLibrary Library(string code)
    {
        var library = _libraries.First(l => l.Code == code);
        return new MethodologyLibrary(900 + library.VersionId, code, library.VersionId, []);
    }

    private TemplateVersionSnapshot Snapshot()
    {
        var sheet = new SheetDef(TemplateVersionId, EcrCode.Create("S1"), Text1("Sheet"), 1);
        SetId(sheet, 1);
        var columnsById = new Dictionary<int, ColumnDef>();
        var aliases = new Dictionary<(int TableDefId, int MonthNumber), IReadOnlyList<MonthInputAlias>>();

        var seenDefs = new HashSet<int>();
        foreach (var table in _tables)
        {
            // Копія аркуша має той самий опис таблиці, що й оригінал.
            if (!seenDefs.Add(table.TableDefId))
            {
                continue;
            }

            var def = new TableDef(
                sheet.Id, EcrCode.Create(table.Code), Text1(table.Code), table.TableDefId,
                TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
            SetId(def, table.TableDefId);
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var spec = table.Columns[i];
                var column = new ColumnDef(table.TableDefId, EcrCode.Create(spec.Code), Text1(spec.Code), i + 1, spec.Type);
                SetId(column, ColumnId(table.Code, spec.Code));
                if (spec.Month is { } month)
                {
                    column.SetMonth(true, month);
                }

                def.AddColumn(column);
                columnsById[column.Id] = column;
            }

            sheet.AddTable(def);
        }

        return new TemplateVersionSnapshot(
            TemplateVersionId, 1, [sheet], columnsById, new Dictionary<(int, string), RowDef>())
        {
            MonthInputAliases = Aliases,
        };
    }

    /// <summary>Псевдоніми місячних входів (<c>cfg.MonthInputAlias</c>): (таблиця, місяць) → псевдоніми.</summary>
    public Dictionary<(int TableDefId, int MonthNumber), IReadOnlyList<MonthInputAlias>> Aliases { get; } = [];

    public ScriptStand WithMonthAlias(string table, string alias, byte month, string column)
    {
        var def = _tables.First(t => t.Code == table).TableDefId;
        var item = new MonthInputAlias(def, alias, month, ColumnId(table, column));
        Aliases[(def, month)] = [.. Aliases.GetValueOrDefault((def, month)) ?? [], item];
        return this;
    }

    private List<CellRecord> Slice(StandTable table, PeriodKey period)
    {
        var list = new List<CellRecord>();
        for (var i = 0; i < table.Rows.Count; i++)
        {
            foreach (var (code, value) in table.Rows[i].Cells)
            {
                if (value is null)
                {
                    continue;
                }

                var data = value switch
                {
                    string text => new CellValueData { ValueString = text },
                    StandEntry entry => new CellValueData { ValueRegistryEntryId = entry.Id },
                    bool flag => new CellValueData { ValueBool = flag },
                    DateTime moment => new CellValueData { ValueDate = moment },
                    _ => new CellValueData { ValueNumeric = System.Convert.ToDecimal(value, CultureInfo.InvariantCulture) },
                };
                list.Add(new CellRecord(
                    new CellAddress(period, RowId(table, i), ColumnId(table.Code, code)), table.TableDefId, data));
            }
        }

        return list;
    }

    private static LocalizedText Text1(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static void SetId(RegistryEntry entry, long id)
        => typeof(Entity<long>).GetProperty("Id")!.SetValue(entry, id);

    /// <summary>Значення виходу за кодом і речовиною.</summary>
    public static decimal? Value(CalculationOutput output, string code, long? substance = null)
        => output.Values.SingleOrDefault(v => v.OutputCode == code && v.SubstanceEntryId == substance)?.Value;
}
