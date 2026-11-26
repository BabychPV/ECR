using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Scripting;

namespace Ecr.Calculations;

/// <summary>Параметри підготовки скрипта для живого перегляду.</summary>
/// <param name="TextOverride">
/// Текст скрипта замість збереженого (чернетка в редакторі); <c>null</c> — збережений. Бібліотеки беруться з
/// матеріалізованих імпортів версії, якщо не задано <paramref name="ImportResolver"/>.
/// </param>
/// <param name="AllowShadowLibraries">
/// Паритет (план §8.4): бібліотека може бути ще на <c>Engine = Formulas</c> з тіньовим текстом — береться її тіньовий
/// текст. Типово <c>false</c>: прогін версії-скрипта вимагає бібліотек на рушії <c>Script</c>.
/// </param>
/// <param name="ImportResolver">
/// Імпорти ТЕКСТУ (<paramref name="TextOverride"/>) за бізнес-датою: незбережений <c>import X;</c> теж працює.
/// Діє лише разом із <paramref name="TextOverride"/>.
/// </param>
public sealed record ScriptPrepareOptions(
    string? TextOverride = null,
    bool AllowShadowLibraries = false,
    Func<DateOnly, CancellationToken, Task<IReadOnlyList<MethodologyLibrary>>>? ImportResolver = null);

/// <summary>
/// Модуль розрахунку версій методологій на рушії <see cref="MethodologyEngine.Script"/> (MS, план §4.4).
/// </summary>
/// <remarks>
/// ⛔ Числа — лише <see cref="Evaluator"/> і арифметика режиму версії; кроки, маскування <c>Legacy</c>, масштаби й
/// округлення виходів — <see cref="CalculationSteps"/> (той самий код, що в <see cref="GenericCalculationModule"/>).
/// Модуль нічого не пише в базу: результат повертається, запис робить <c>CalculationOutputWriter</c>.
/// <para>
/// ⚠ Модуль розв'язується зі scope гілки пакета й живе рівно стільки, скільки гілка; усе, що не залежить від
/// рядка, лежить у <see cref="ScriptRuntime"/> (на прив'язку), який несе <see cref="CalculationBindingContext"/>.
/// </para>
/// </remarks>
public sealed class ScriptCalculationModule(
    Evaluator evaluator,
    IMethodologyStore methodologies,
    ScriptProgramCache programs,
    CalendarContext calendar,
    IUnitCatalog unitCatalog,
    IPeriodStore periods,
    ICalculationBindingStore bindingStore,
    IRowStore rows,
    ICellStore cells,
    IMetadataCache metadata,
    CalculationInputBuilder inputBuilder,
    ICalculationResultStore results,
    ScriptRunContext? runContext = null,
    IRegistryStore? registryStore = null,
    IRegistrySnapshotLoader? registryLoader = null,
    IScriptRuntimeStore? scriptStore = null,
    CalculationLimits? limits = null,
    IMonthColumnRouter? monthRouter = null) : ICalculationModule
{
    private readonly ScriptProgramLoader _loader = new(methodologies, programs);
    private readonly Evaluator _strict = evaluator.WithArithmetic(EvaluationArithmetics.For(NumericMode.Strict));
    private readonly Evaluator _legacy = evaluator.WithArithmetic(EvaluationArithmetics.For(NumericMode.Legacy));
    private readonly CalculationLimits _limits = limits ?? new CalculationLimits();
    private UnitCatalogTables? _units;

    /// <summary>
    /// Бюджет кроків ОДНОГО рядка на всі його оператори (PSS-21) — з конфігурації,
    /// <see cref="CalculationLimits.MaxRowEvaluationSteps"/> (типово <see cref="CalculationLimits.DefaultMaxRowEvaluationSteps"/>).
    /// </summary>
    /// <remarks>
    /// ⛔ Діє і в перерахунку, і в перегляді; детермінований (рахується робота, а не час). Рядок, що вичерпав бюджет,
    /// отримує <c>#BUDGET</c> на кожен наступний оператор — та сама помилка кроку, що й за стелю одного оператора.
    /// </remarks>
    public int MaxRowEvaluationSteps => _limits.MaxRowEvaluationSteps;

    /// <inheritdoc />
    public string Code => "script";

    /// <inheritdoc />
    public CalculationLevel Level => CalculationLevel.Configuration;

    /// <inheritdoc />
    public bool CanHandle(MethodologyDescriptor methodology)
    {
        ArgumentNullException.ThrowIfNull(methodology);

        return methodology.Level == CalculationLevel.Configuration && methodology.Engine == MethodologyEngine.Script;
    }

    /// <inheritdoc />
    public Task<CalculationBindingContext> PrepareAsync(
        MethodologyDescriptor methodology, long documentId, PeriodKey periodKey, CancellationToken ct)
        => PrepareAsync(methodology, documentId, periodKey, registries: null, ct);

    /// <inheritdoc />
    public Task<CalculationBindingContext> PrepareAsync(
        MethodologyDescriptor methodology,
        long documentId,
        PeriodKey periodKey,
        RegistrySnapshotCache? registries,
        CancellationToken ct)
        => PrepareAsync(methodology, documentId, periodKey, registries, options: null, ct);

    /// <summary>
    /// Читає все, що не залежить від рядка: програму з бібліотеками, склад версії, період, довідники, одиниці,
    /// Id записів <c>entry()</c> (план §4.4).
    /// </summary>
    /// <param name="methodology">Версія методології.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="registries">Кеш знімків довідників прогону; <c>null</c> — власний на цю прив'язку.</param>
    /// <param name="options">Параметри живого перегляду (текст замість збереженого); <c>null</c> — немає.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Domain.Abstractions.DomainException">
    /// <c>ECR-CALC-0422</c>: скрипта немає, він не компілюється або бібліотека недоступна.
    /// </exception>
    public async Task<CalculationBindingContext> PrepareAsync(
        MethodologyDescriptor methodology,
        long documentId,
        PeriodKey periodKey,
        RegistrySnapshotCache? registries,
        ScriptPrepareOptions? options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(methodology);

        var period = await CalculationSteps.PeriodAsync(periods, calendar, methodology, documentId, periodKey, ct).ConfigureAwait(false);
        var program = await _loader.LoadAsync(methodology, period.End, options?.TextOverride, options?.ImportResolver, ct, options?.AllowShadowLibraries ?? false).ConfigureAwait(false);
        var script = program.Main;

        var substances = await methodologies.GetSubstancesAsync(methodology.MethodologyVersionId, ct).ConfigureAwait(false);
        var outputs = await methodologies.GetOutputsAsync(methodology.MethodologyVersionId, ct).ConfigureAwait(false);
        var scales = await bindingStore.ListOutputScalesAsync(methodology.MethodologyId, ct).ConfigureAwait(false);

        // Код речовини → Id запису: лише коли скрипт має foreach substance.
        IReadOnlyDictionary<long, string>? substanceCodes = null;
        IReadOnlyDictionary<string, long>? substanceIds = null;
        if (script.Substances.Count > 0)
        {
            substanceCodes = await methodologies.GetSubstanceCodesAsync(methodology.MethodologyVersionId, ct).ConfigureAwait(false);
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, code) in substanceCodes)
            {
                map.TryAdd(code, id);
            }

            substanceIds = map;
        }

        var (snapshot, dated) = await RegistriesAsync(methodology.MethodologyId, program, period, registries, ct).ConfigureAwait(false);
        var units = await UnitsAsync(ct).ConfigureAwait(false);
        var entries = await EntriesAsync(program, ct).ConfigureAwait(false);

        var runtime = new ScriptRuntime(
            program,
            new ScriptDataAccess(rows, cells, metadata, inputBuilder, methodologies, results, scriptStore, runContext, _limits, monthRouter),
            methodology,
            documentId,
            periodKey,
            units.Table,
            units.Codes,
            units.Ids,
            entries)
        {
            DatedRegistries = dated,
        };

        return new CalculationBindingContext(
            methodology,
            documentId,
            periodKey,
            [],
            substances,
            outputs,
            period,
            scales ?? EmptyScales,
            EmptyConstants,
            snapshot,
            Libraries: null,
            CategoryRule: null,
            substanceCodes,
            KnownCategories: null,
            script,
            substanceIds,
            runtime);
    }

    /// <inheritdoc />
    public async Task<CalculationOutput> ExecuteAsync(CalculationInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var context = await PrepareAsync(input.Methodology, input.DocumentId, input.PeriodKey, ct).ConfigureAwait(false);
        return await ExecuteAsync(context, input, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CalculationOutput> ExecuteAsync(
        CalculationBindingContext binding, CalculationInput input, CancellationToken ct)
        => (await ExecuteDetailedAsync(binding, input, ct).ConfigureAwait(false)).Output;

    /// <summary>
    /// Те саме, що <see cref="ExecuteAsync(CalculationBindingContext, CalculationInput, CancellationToken)"/>, і ще
    /// перелік усіх величин рядка (<c>var</c>, <c>visible var</c>, <c>output</c>, по речовинах) — для живого перегляду.
    /// </summary>
    /// <param name="binding">Контекст прив'язки з <see cref="PrepareAsync(MethodologyDescriptor, long, PeriodKey, RegistrySnapshotCache?, ScriptPrepareOptions?, CancellationToken)"/>.</param>
    /// <param name="input">Вхід рядка.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<(CalculationOutput Output, IReadOnlyList<ScriptValue> Values)> ExecuteDetailedAsync(
        CalculationBindingContext binding, CalculationInput input, CancellationToken ct)
        => ExecuteDetailedAsync(binding, input, ScriptRunLimits.Default, ct);

    /// <summary>
    /// Те саме, що <see cref="ExecuteDetailedAsync(CalculationBindingContext, CalculationInput, CancellationToken)"/>, з
    /// межами запиту понад бюджет рядка (PSS-21): спільний бюджет кроків і стеля величин живого перегляду.
    /// </summary>
    /// <param name="binding">Контекст прив'язки з <see cref="PrepareAsync(MethodologyDescriptor, long, PeriodKey, RegistrySnapshotCache?, ScriptPrepareOptions?, CancellationToken)"/>.</param>
    /// <param name="input">Вхід рядка.</param>
    /// <param name="limits">Межі прогону.</param>
    /// <param name="ct">Токен скасування: перевіряється й між операторами скрипта.</param>
    public async Task<(CalculationOutput Output, IReadOnlyList<ScriptValue> Values)> ExecuteDetailedAsync(
        CalculationBindingContext binding, CalculationInput input, ScriptRunLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);

        // ⛔ Контекст і рядок мусять бути з ОДНІЄЇ прив'язки (дефект D-112: число лишилось би правдоподібним).
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

        if (binding.Script is not { } script || binding.ScriptRuntime is not ScriptRuntime runtime)
        {
            throw new ArgumentException(
                "Контекст прив'язки не підготовлено модулем скрипта (немає Script або ScriptRuntime).", nameof(binding));
        }

        var version = input.Methodology;
        var numeric = new NumericPolicy(version.NumericMode);
        var trace = new TraceRecorder(version.TraceLevel);
        var instance = await runtime.InstanceAsync(input.TableInstanceId, ct).ConfigureAwait(false);

        // Вхід рядка + результати інших методологій як значення колонок Calculated (§13.9).
        var arguments = ScriptRuntime.ArgumentsOf(
            input.Arguments, instance.Calculated.GetValueOrDefault(input.SourceRowKey ?? string.Empty));

        var values = new List<CalculationOutputValue>();
        var detail = new List<ScriptValue>();
        var context = new ScriptEvaluationContext(
            runtime,
            version.NumericMode == NumericMode.Legacy ? _legacy : _strict,
            numeric,
            trace,
            binding,
            input,
            arguments,
            instance);

        // Таблиці-псевдоніми — до обчислення: хостова функція синхронна.
        foreach (var owner in ScriptRuntime.AllScripts(runtime.Program))
        {
            foreach (var alias in owner.Tables.Keys)
            {
                context.SetAliasRows(owner, alias, await runtime.AliasRowsAsync(owner, alias, instance, ct).ConfigureAwait(false));
            }
        }

        new ScriptRowRun(context, runtime, binding, input, numeric, trace, values, detail, limits, MaxRowEvaluationSteps, ct).Run();

        var output = new CalculationOutput(
            input.DocumentId,
            input.SourceRowKey,
            values,
            [.. trace.Steps.Select(s => new CalculationTraceStep(
                s.Order, s.Code, s.Expression, s.Value, s.Error, s.Masked, s.ToJson(), s.SubstanceEntryId))],
            ReadArguments(input, trace),
            input.TableInstanceId > 0 ? input.TableInstanceId : (long?)null)
        {
            // Рядок без жодного значення — назвати обов'язкові поля, яких бракує (інакше тихий нуль).
            // ✎ D-578 (Q2, рішення людини 26.11: «бракує даних — не 0»): і рядок, що дав ЧАСТИНУ виходів, — коли хоч одного
            // виходу немає, а поле, яке читає скрипт, порожнє, — рядок теж отримує названу проблему MissingInputs.
            MissingArguments = script.Outputs.Count > 0 && AnyOutputAbsent(script.Outputs.Select(o => o.Code), values)
                ? MissingArgumentsOf(runtime.Program, arguments)
                : null,
            StepError = trace.FirstFailure is { } failure
                ? new CalculationStepError(failure.Code, failure.Error, trace.FailedStepCount)
                : null,
        };

        return (output, detail);
    }

    /// <summary>
    /// Методології, чиї РЕЗУЛЬТАТИ читає скрипт: колонки <c>Calculated</c> власної таблиці й таблиць-псевдонімів, прив'язані
    /// до виходів інших методологій (MS, план §13.9). Версійний зріз <see cref="ReadsBindingsAsync"/>.
    /// </summary>
    /// <param name="methodology">Версія-скрипт.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці прив'язки.</param>
    /// <param name="onDate">Бізнес-дата: кінець періоду.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатори методологій (без самої себе).</returns>
    public async Task<IReadOnlySet<int>> ReadsMethodologyIdsAsync(
        MethodologyDescriptor methodology, long tableInstanceId, DateOnly onDate, CancellationToken ct)
        => (await ReadsBindingsAsync(methodology, tableInstanceId, onDate, ct).ConfigureAwait(false))
            .Select(r => r.MethodologyId)
            .ToHashSet();

    /// <summary>
    /// Точні читання результатів: пари (методологія-джерело, екземпляр таблиці), чиї числа прив'язка скрипта читає
    /// колонками <c>Calculated</c> — власної таблиці й таблиць-псевдонімів (MS, план §13.9). Число колонки, прив'язаної
    /// до виходу методології M, лежить у результатах прив'язки (M, екземпляр цієї таблиці), тож порядок розрахунку й
    /// цикли будуються на цих парах, а не на цілих версіях.
    /// </summary>
    /// <param name="methodology">Версія-скрипт.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці прив'язки.</param>
    /// <param name="onDate">Бізнес-дата: кінець періоду.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Пари (методологія, екземпляр), без власної методології.</returns>
    public async Task<IReadOnlySet<(int MethodologyId, long TableInstanceId)>> ReadsBindingsAsync(
        MethodologyDescriptor methodology, long tableInstanceId, DateOnly onDate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(methodology);

        var program = await _loader.LoadAsync(methodology, onDate, textOverride: null, importResolver: null, ct).ConfigureAwait(false);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var monthly = new List<string>();
        foreach (var script in ScriptRuntime.AllScripts(program))
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

        var own = await rows.ResolveTableInstanceAsync(tableInstanceId, ct).ConfigureAwait(false);
        var wanted = new HashSet<(int TableDefId, int ColumnDefId, long InstanceId)>();

        async Task AddAsync(TableInstanceRef instance, IEnumerable<string> columnNames)
        {
            var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
            var set = new HashSet<string>(columnNames, StringComparer.OrdinalIgnoreCase);
            foreach (var column in snapshot.ColumnsById.Values)
            {
                if (column.TableDefId == instance.TableDefId && !column.IsDeleted && column.DataType == CellDataType.Calculated
                    && (set.Contains(column.Code)
                        || monthly.Any(p => column.Code.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
                {
                    wanted.Add((column.TableDefId, column.Id, instance.TableInstanceId));
                }
            }
        }

        await AddAsync(own, names).ConfigureAwait(false);

        var aliasScripts = ScriptRuntime.AllScripts(program).Where(s => s.Tables.Count > 0).ToList();
        if (aliasScripts.Count > 0)
        {
            var instances = await rows
                .GetTableInstancesAsync(own.DocumentId, new PeriodKey(own.PeriodKey), ct)
                .ConfigureAwait(false);
            foreach (var instance in instances)
            {
                var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
                var code = ScriptRuntime.TableCodeOf(snapshot, instance.TableDefId);
                foreach (var script in aliasScripts)
                {
                    foreach (var (alias, tableRef) in script.Tables)
                    {
                        if (string.Equals(tableRef.TableCode, code, StringComparison.OrdinalIgnoreCase)
                            && script.TableColumns.TryGetValue(alias, out var columns))
                        {
                            await AddAsync(instance, columns).ConfigureAwait(false);
                        }
                    }
                }
            }
        }

        if (wanted.Count == 0)
        {
            return new HashSet<(int, long)>();
        }

        var bindings = await methodologies
            .GetColumnResultBindingsAsync([.. wanted.Select(w => w.TableDefId).Distinct()], ct)
            .ConfigureAwait(false);

        var reads = new HashSet<(int MethodologyId, long TableInstanceId)>();
        foreach (var binding in bindings.Where(b => b.MethodologyId != methodology.MethodologyId))
        {
            foreach (var read in wanted.Where(w => w.TableDefId == binding.TableDefId && w.ColumnDefId == binding.ColumnDefId))
            {
                reads.Add((binding.MethodologyId, read.InstanceId));
            }
        }

        return reads;
    }

    /// <summary>Код таблиці екземпляра — для повідомлень оркестратора (цикл читання результатів).</summary>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<string> TableCodeOfInstanceAsync(long tableInstanceId, CancellationToken ct)
    {
        var instance = await rows.ResolveTableInstanceAsync(tableInstanceId, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
        return ScriptRuntime.TableCodeOf(snapshot, instance.TableDefId);
    }

    // ───── допоміжне ─────

    private static readonly IReadOnlyDictionary<string, byte?> EmptyScales =
        new Dictionary<string, byte?>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<MethodologyConstant>> EmptyConstants =
        new Dictionary<string, IReadOnlyList<MethodologyConstant>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>✎ D-578 (Q2): хоч один оголошений вихід рядок не дав (усі — і нуль значень — теж «відсутні»).</summary>
    internal static bool AnyOutputAbsent(IEnumerable<string> outputCodes, IReadOnlyCollection<CalculationOutputValue> values)
    {
        var produced = values.Select(v => v.OutputCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return outputCodes.Any(code => !produced.Contains(code));
    }

    private static List<string>? MissingArgumentsOf(LoadedProgram program, Dictionary<string, CalculationArgument> arguments)
    {
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var script in ScriptRuntime.AllScripts(program))
        {
            foreach (var field in script.RowFields)
            {
                // Місячне звернення розв'язується в рантаймі (§13.2): статично його не перевірити.
                if (field.IsOptional || field.IsMonthly || !seen.Add(field.Name))
                {
                    continue;
                }

                if (!arguments.TryGetValue(field.Name, out var argument)
                    || (argument.Value is null && string.IsNullOrWhiteSpace(argument.ValueString) && argument.EntryId is null
                        && (argument.ValueDate is null || program.Main.RegistryAsOf is null)))
                {
                    missing.Add(field.Name);
                }
            }
        }

        return missing.Count == 0 ? null : missing;
    }

    private static List<CalculationArgument> ReadArguments(CalculationInput input, TraceRecorder trace)
    {
        var read = trace.Steps
            .SelectMany(s => s.Detail?.Inputs ?? [])
            .Where(i => i.Kind == TraceInputKind.Argument)
            .Select(i => i.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return read.Count == 0
            ? []
            : [.. input.Arguments
                .Where(a => read.Contains(a.ArgumentCode))
                .DistinctBy(a => a.ArgumentCode, StringComparer.OrdinalIgnoreCase)];
    }

    private async Task<UnitCatalogTables> UnitsAsync(CancellationToken ct)
        => _units ??= UnitCatalogTables.Build(await unitCatalog.GetAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Id записів <c>entry()</c> версії й бібліотек: код довідника → код запису → Id. Нерозв'язаний запис у таблицю
    /// не потрапляє, і вираз дає <c>#REF</c>, а умова вибору рядків — «тексту немає».
    /// </summary>
    private async Task<Dictionary<string, long>> EntriesAsync(LoadedProgram program, CancellationToken ct)
    {
        var entries = new Dictionary<string, long>(StringComparer.Ordinal);
        if (registryStore is null)
        {
            return entries;
        }

        var refs = ScriptRuntime.AllScripts(program)
            .SelectMany(s => s.RegistryEntries)
            .DistinctBy(e => ScriptRuntime.EntryKey(e.RegistryCode, e.EntryCode), StringComparer.Ordinal)
            .ToList();
        var definitions = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in refs)
        {
            if (!definitions.TryGetValue(entry.RegistryCode, out var definitionId))
            {
                definitionId = (await registryStore.FindDefinitionAsync(entry.RegistryCode, ct).ConfigureAwait(false))?.Id;
                definitions[entry.RegistryCode] = definitionId;
            }

            if (definitionId is { } id
                && await registryStore.FindEntryByCodeAsync(id, entry.EntryCode, ct).ConfigureAwait(false) is { } found)
            {
                entries[ScriptRuntime.EntryKey(entry.RegistryCode, entry.EntryCode)] = found.Id;
            }
        }

        return entries;
    }

    /// <summary>Знімок довідників скрипта й бібліотек — раз на прив'язку (шлях <c>GenericCalculationModule.RegistriesAsync</c>).</summary>
    /// <remarks>
    /// ✎ DESIGN-sg-flaring §5.2a (E4): скрипт із <c>registry as of</c> бере ДЖЕРЕЛО знімків (одне читання БД на набір
    /// довідників за прогін, спільне для документів через <see cref="RegistrySnapshotCache"/>); типовий знімок прив'язки —
    /// з того самого джерела на кінець періоду, знімок рядка — на дату рядка, раз на інтервал чинності.
    /// </remarks>
    private async Task<(IRegistrySnapshot? Snapshot, Ecr.Application.Registries.DatedRegistrySnapshots? Dated)> RegistriesAsync(
        int methodologyId,
        LoadedProgram program,
        Expressions.PeriodContext period,
        RegistrySnapshotCache? cache,
        CancellationToken ct)
    {
        var scripts = ScriptRuntime.AllScripts(program).ToList();
        if (!scripts.Any(s => s.NeedsRegistries) || registryStore is null || registryLoader is null)
        {
            return (null, null);
        }

        var codes = scripts
            .SelectMany(s => s.RegistryCodes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        async Task<IReadOnlyList<int>> ResolveAsync()
        {
            var definitions = codes.Count == 0
                ? []
                : await registryStore.FindDefinitionsAsync(codes, ct).ConfigureAwait(false);

            // REGFIELD(row.Lookup, 'NAME') читає запис, що прийшов Lookup-колонкою, — довідники цих колонок
            // ідуть у той самий знімок (аудит L7-06).
            var lookups = await bindingStore.ListLookupRegistryIdsAsync(methodologyId, ct).ConfigureAwait(false) ?? [];
            return [.. definitions.Select(d => d.Id).Concat(lookups).Distinct().Order()];
        }

        var ids = cache is null
            ? await ResolveAsync().ConfigureAwait(false)
            : await cache.GetOrResolveRegistryIdsAsync(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{methodologyId}|script|{string.Join(',', codes.Select(c => c.ToUpperInvariant()))}"),
                    ResolveAsync)
                .ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return (null, null);
        }

        var runCache = cache ?? new RegistrySnapshotCache(registryAsOfUtc: null);
        if (program.Main.RegistryAsOf is not null)
        {
            var dated = await runCache.GetOrLoadSourceAsync(ids, registryLoader, ct).ConfigureAwait(false);
            return (dated.Get(period.End), dated);
        }

        // Бізнес-дата — останній день ПЕРІОДУ, а не «сьогодні» (§5.7).
        return (await runCache.GetOrLoadAsync(ids, period.End, registryLoader, ct).ConfigureAwait(false), null);
    }
}
