using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Parsing;

namespace Ecr.Calculations;

/// <summary>
/// Generic-модуль: виконує методологію, задану **даними** — формулами,
/// константами і речовинами (ФВ-9.1).
/// </summary>
/// <remarks>
/// Мета — «generic плюс явний список винятків», а не доведення універсальності
/// (ФВ-9.3). Якщо після класифікації 44 методологій рівень 2 потрібен більш ніж
/// для 5 — проблема не в методологіях, а в граматиці рівня 1: дешевше
/// розширити граматику, ніж плодити скрипти.
/// </remarks>
/// <param name="formulaEngine">Рушій виразів.</param>
/// <param name="methodologies">Склад версій методологій.</param>
/// <param name="constants">Вибір константи серед кандидатів.</param>
/// <param name="calendar">Календарний контекст періоду.</param>
/// <param name="unitCatalog">Довідник одиниць.</param>
/// <param name="periods">Межі періодів.</param>
/// <param name="bindingStore">Масштаби колонок-приймачів.</param>
/// <param name="registryStore">
/// Коди довідників → ідентифікатори (RT-23a). ⚠ Необов'язковий, як і
/// <paramref name="registryLoader"/>: методологія без функцій довідників їх не потребує,
/// і стенди, що збирають модуль вручну, не мусять їх знати.
/// </param>
/// <param name="registryLoader">Завантажувач знімка довідників (RT-22).</param>
public sealed class GenericCalculationModule(
    IFormulaEngine formulaEngine,
    IMethodologyStore methodologies,
    ConstantResolver constants,
    CalendarContext calendar,
    IUnitCatalog unitCatalog,
    IPeriodStore periods,
    ICalculationBindingStore bindingStore,
    IRegistryStore? registryStore = null,
    IRegistrySnapshotLoader? registryLoader = null) : ICalculationModule
{
    private UnitTable? _units;

    /// <summary>Id одиниці → код: трейс називає одиниці кодами (§7.2), а рядок несе Id.</summary>
    private Dictionary<int, string> _unitCodes = [];

    /// <inheritdoc />
    public string Code => "generic";

    /// <inheritdoc />
    public CalculationLevel Level => CalculationLevel.Configuration;

    /// <inheritdoc />
    /// <remarks>
    /// Рівень перевіряється тут, а склад формул — при публікації: розбирати
    /// вирази на кожен рядок означало б платити парсером мільйони разів за
    /// відповідь, яка не змінюється в межах версії.
    /// </remarks>
    public bool CanHandle(MethodologyDescriptor methodology)
    {
        ArgumentNullException.ThrowIfNull(methodology);
        return methodology.Level == CalculationLevel.Configuration;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Без кешу прогону (одиночний виклик — публікація, симуляція) знімок довідників
    /// читається на ПОТОЧНИЙ момент: прогону, чий <c>RegistryAsOfUtc</c> треба
    /// відтворити, тут немає.
    /// </remarks>
    public Task<CalculationBindingContext> PrepareAsync(
        MethodologyDescriptor methodology, long documentId, PeriodKey periodKey, CancellationToken ct)
        => PrepareAsync(methodology, documentId, periodKey, registries: null, ct);

    /// <inheritdoc />
    public async Task<CalculationBindingContext> PrepareAsync(
        MethodologyDescriptor methodology,
        long documentId,
        PeriodKey periodKey,
        RegistrySnapshotCache? registries,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(methodology);

        var formulas = await methodologies
            .GetFormulasAsync(methodology.MethodologyVersionId, ct).ConfigureAwait(false);
        var substances = await methodologies
            .GetSubstancesAsync(methodology.MethodologyVersionId, ct).ConfigureAwait(false);
        var outputs = await methodologies
            .GetOutputsAsync(methodology.MethodologyVersionId, ct).ConfigureAwait(false);

        // ⛔ Константи версії — ОДНИМ запитом тут, а не запитом на рядок ×
        // речовину × код у `ExecuteAsync` (аудит P1). Вибір кандидата лишається
        // тим самим правилом `ConstantResolver`, лише в пам'яті. Регістр коду —
        // без різниці, як і в запиті за кодом, що стояв тут раніше (колація БД).
        var constants = await methodologies
            .GetConstantsAsync(methodology.MethodologyVersionId, ct).ConfigureAwait(false);
        var constantsByCode = constants
            .GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<MethodologyConstant>)g.OrderBy(c => c.Id).ToList(),
                StringComparer.OrdinalIgnoreCase);

        // ⚠ Порядок беремо з EvaluationOrder — він топологічний із Publish
        // (ФВ-9.4). Сортувати граф тут заборонено: порядок мусить бути тим
        // самим, за яким версію перевірили тестами, а не тим, який вийде
        // сьогодні.
        var ordered = formulas.OrderBy(f => f.EvaluationOrder).ThenBy(f => f.Id).ToList();

        var period = await PeriodAsync(methodology, documentId, periodKey, ct).ConfigureAwait(false);

        // ⚠ Масштаб колонок-приймачів читається ТУТ, разом зі складом версії:
        // він однаковий для всієї прив'язки, а `ExecuteAsync` кличуть на кожен
        // рядок (`CAL-06`). Питання йде за `MethodologyId`, а не за версією:
        // `cfg.CalculationBinding` належить шаблону і переживає всі версії
        // методології одразу.
        var scales = await bindingStore
            .ListOutputScalesAsync(methodology.MethodologyId, ct)
            .ConfigureAwait(false);

        // ⛔ RT-23a (`D-162`): знімок довідників — ТУТ, раз на прив'язку, і довідник
        // одиниць теж. Після підготовки обчислення рядків не звертається до сховищ
        // жодного разу: ні `REGSUM` на 34 рядках складу, ні `CONVERT`.
        var snapshot = await RegistriesAsync(ordered, period, registries, ct).ConfigureAwait(false);
        await UnitsAsync(ct).ConfigureAwait(false);

        return new CalculationBindingContext(
            methodology, documentId, periodKey, ordered, substances, outputs, period,
            scales ?? EmptyScales, constantsByCode, snapshot);
    }

    /// <summary>
    /// Знімок довідників, які читають формули версії, на кінець періоду.
    /// </summary>
    /// <remarks>
    /// ⚠ Перелік довідників — з тексту формул (перший літерал <c>REGFIND</c>/<c>REGONE</c>/
    /// агрегатів), цілі <c>Lookup</c>-полів (<c>ROW.COMPONENT.MW</c>) дочитує сам
    /// завантажувач. <c>cfg.RegistryUse</c> пише публікація лише з кроку RT-23b; коли він
    /// є, перелік варто брати звідти — відповідь та сама, але без розбору.
    ///
    /// ⚠ Формули без функцій довідників — жодного звернення до сховищ і <c>null</c>: так
    /// поводився модуль до кроку, і саме так лишаються побітно незмінними версії
    /// <c>Legacy</c>, де ці функції не публікуються взагалі (<c>ECR-CALC-0433</c>).
    ///
    /// ⚠ Невідомий код довідника (опис видалили після публікації) просто не
    /// потрапляє в перелік: формула отримає <c>#REF</c> на рядку, а не виняток на
    /// всю прив'язку.
    /// </remarks>
    private async Task<IRegistrySnapshot?> RegistriesAsync(
        IReadOnlyList<MethodologyFormula> formulas,
        Expressions.PeriodContext period,
        RegistrySnapshotCache? cache,
        CancellationToken ct)
    {
        var codes = RegistryCodes(formulas);
        if (codes.Count == 0 || registryStore is null || registryLoader is null)
        {
            return null;
        }

        var definitions = await registryStore.FindDefinitionsAsync(codes, ct).ConfigureAwait(false);
        var ids = definitions.Select(d => d.Id).Distinct().Order().ToList();

        // ⛔ Бізнес-дата — останній день ПЕРІОДУ, а не «сьогодні»: перерахунок
        // минулого року бачить склад, чинний тоді (§5.7, та сама вісь, що в констант).
        return await (cache ?? new RegistrySnapshotCache(registryAsOfUtc: null))
            .GetOrLoadAsync(ids, period.End, registryLoader, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Коди довідників, які формули версії називають літералом.</summary>
    private HashSet<string> RegistryCodes(IReadOnlyList<MethodologyFormula> formulas)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var formula in formulas)
        {
            var parsed = formulaEngine.Parse(formula.Expression, ExpressionDialect.Methodology);
            if (!parsed.IsSuccess || parsed.Expression is null)
            {
                continue;
            }

            var pending = new Stack<Expressions.Ast.AstNode>();
            pending.Push(parsed.Expression.Root);
            while (pending.TryPop(out var node))
            {
                switch (node)
                {
                    case Expressions.Ast.FunctionNode function:
                        if (function.Arguments.Count > 0
                            && (RegistryForms.RowScopeNames.Contains(function.Name)
                                || string.Equals(function.Name, RegistryForms.Find, StringComparison.OrdinalIgnoreCase))
                            && Expressions.Binding.ReferenceResolver.RegistryCodeLiteral(function.Arguments[0]) is { } code)
                        {
                            codes.Add(code);
                        }

                        foreach (var argument in function.Arguments)
                        {
                            pending.Push(argument);
                        }

                        break;

                    case Expressions.Ast.BinaryNode binary:
                        pending.Push(binary.Left);
                        pending.Push(binary.Right);
                        break;

                    case Expressions.Ast.UnaryNode unary:
                        pending.Push(unary.Operand);
                        break;

                    case Expressions.Ast.ConditionalNode conditional:
                        pending.Push(conditional.Condition);
                        pending.Push(conditional.WhenTrue);
                        pending.Push(conditional.WhenFalse);
                        break;

                    default:
                        break;
                }
            }
        }

        return codes;
    }

    /// <summary>Порожній словник масштабів — усі виходи беруть замовчування.</summary>
    private static readonly IReadOnlyDictionary<string, byte?> EmptyScales =
        new Dictionary<string, byte?>(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<CalculationOutput> ExecuteAsync(CalculationInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var context = await PrepareAsync(input.Methodology, input.DocumentId, input.PeriodKey, ct)
            .ConfigureAwait(false);

        return await ExecuteAsync(context, input, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CalculationOutput> ExecuteAsync(
        CalculationBindingContext binding, CalculationInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(input);

        // ⛔ Контекст і рядок мусять бути з ОДНІЄЇ прив'язки. Без цієї
        // перевірки помилка викликача (контекст, підготовлений для іншої
        // версії, іншого документа чи іншого періоду) не проявилася б ніяк:
        // рядок порахувався б чужими формулами або поділився б на дні чужого
        // періоду, і число лишилося б правдоподібним. Саме такий клас дефекту
        // `D-112` вже коштував утричі завищених `г/с`.
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

        var version = input.Methodology;
        var numeric = new NumericPolicy(version.NumericMode);
        var trace = new TraceRecorder(version.TraceLevel);

        var ordered = binding.Formulas;
        var substances = binding.Substances;
        var outputs = binding.Outputs;
        var period = binding.Period;

        var arguments = input.Arguments.ToDictionary(
            a => a.ArgumentCode, ToValue, StringComparer.OrdinalIgnoreCase);

        var values = new List<CalculationOutputValue>();
        var units = await UnitsAsync(ct).ConfigureAwait(false);

        // ⛔ HSE301 A3a (D-176, V-7): Row-формули — ОДИН раз на рядок, до циклу
        // речовин; у циклі вони видимі як `!Code`. Порядок усередині кожної групи —
        // `EvaluationOrder`, а Row-формула за перевіркою публікації не посилається на
        // Substance-формулу (`rowScopeReferencesSubstance`), тож обчислити всі Row
        // першими — це той самий топологічний порядок.
        // ⚠ Типова область — `Substance`: для версії без жодної Row-формули група
        // порожня, і цикл нижче робить побітно те саме, що робив до кроку.
        var rowFormulas = ordered.Where(f => f.Scope == MethodologyFormulaScope.Row).ToList();
        var substanceFormulas = ordered.Where(f => f.Scope != MethodologyFormulaScope.Row).ToList();
        var outputCodes = outputs
            .Select(o => o.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Формули версії за кодом — трейс бере з них одиницю входу `!Formula` (§7.2).
        var formulasByCode = new Dictionary<string, MethodologyFormula>(StringComparer.OrdinalIgnoreCase);
        foreach (var formula in ordered)
        {
            formulasByCode.TryAdd(formula.Code, formula);
        }

        // ⛔ Константи Row-формул резолвляться БЕЗ речовини: константа, задана по
        // речовинах, у Row-формулі — відмова публікації, а не «коефіцієнт першої».
        var rowConstantUnits = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        var rowContext = new MethodologyEvaluationContext(
            period,
            arguments,
            ResolveConstants(version, rowFormulas, substanceEntryId: null, period, binding.Constants, rowConstantUnits),
            units,
            binding.Registries);
        var rowScope = new TraceScope(input, formulasByCode, rowConstantUnits, SubstanceEntryId: null);

        foreach (var formula in rowFormulas)
        {
            rowContext.SetFormulaResult(formula.Code, Evaluate(formula, rowContext, numeric, trace, rowScope));
        }

        // Рівень рядка: виходи «раз на рядок» і проміжні значення видимих Row-формул.
        foreach (var output in outputs.Where(o => !o.IsPerSubstance))
        {
            Emit(values, version, binding, numeric, trace, output.Code, rowContext.GetFormulaResult(output.Code),
                 output.UnitId, substance: null, CalculationResultKind.Output);
        }

        foreach (var formula in VisibleIntermediates(rowFormulas, outputCodes))
        {
            Emit(values, version, binding, numeric, trace, formula.Code, rowContext.GetFormulaResult(formula.Code),
                 formula.OutputUnitId!.Value, substance: null, CalculationResultKind.Intermediate);
        }

        // ⛔ Для КОЖНОЇ речовини — власний прогін. Константи резолвляться за
        // речовиною, тому спільний контекст дав би всім речовинам коефіцієнт
        // тієї, яку порахували першою (ФВ-9.1).
        // ⚠ Фільтра «активних» немає: речовина або входить у версію, або ні
        // (`calc`-частина `Q-027`). «Вимкнена» речовина означала б, що версія
        // рахує не те, що в ній записано.
        var targets = substances
            .OrderBy(s => s.Ordinal)
            .Cast<MethodologySubstance?>()
            .ToList();

        // Методологія без речовин теж рахується: не всі виходи прив'язані до
        // речовини (об'єм, витрата). Один прогін із substance = null.
        if (targets.Count == 0)
        {
            targets.Add(null);
        }

        var perSubstanceOutputs = outputs.Where(o => o.IsPerSubstance).ToList();
        var substanceIntermediates = VisibleIntermediates(substanceFormulas, outputCodes);

        foreach (var substance in targets)
        {
            var constantUnits = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            var resolved = ResolveConstants(
                version, substanceFormulas, substance?.SubstanceEntryId, period, binding.Constants, constantUnits);

            var context = new MethodologyEvaluationContext(period, arguments, resolved, units, binding.Registries);
            var scope = new TraceScope(input, formulasByCode, constantUnits, substance?.SubstanceEntryId);

            // Row-результати — готові, не перераховуються: саме заради цього D-176.
            foreach (var formula in rowFormulas)
            {
                context.SetFormulaResult(formula.Code, rowContext.GetFormulaResult(formula.Code));
            }

            foreach (var formula in substanceFormulas)
            {
                var value = Evaluate(formula, context, numeric, trace, scope);
                context.SetFormulaResult(formula.Code, value);
            }

            foreach (var output in perSubstanceOutputs)
            {
                Emit(values, version, binding, numeric, trace, output.Code, context.GetFormulaResult(output.Code),
                     output.UnitId, substance, CalculationResultKind.Output);
            }

            foreach (var formula in substanceIntermediates)
            {
                Emit(values, version, binding, numeric, trace, formula.Code, context.GetFormulaResult(formula.Code),
                     formula.OutputUnitId!.Value, substance, CalculationResultKind.Intermediate);
            }
        }

        // ⛔ У БД не пишемо (D-69): результат повертається, запис робить
        // CalculationOutputWriter. Інакше нічний перерахунок писав би десятки
        // мільйонів рядків у партиції документів.
        return new CalculationOutput(
            input.DocumentId,
            input.SourceRowKey,
            values,
            trace.Steps
                .Select(s => new CalculationTraceStep(
                    s.Order, s.Code, s.Expression, s.Value, s.Error, s.Masked, Detail: s.ToJson()))
                .ToList());
    }

    /// <summary>
    /// Видимі формули групи, які пишуться як проміжні значення (<c>D-175</c>, V-6).
    /// </summary>
    /// <remarks>
    /// ⛔ Формула, чий код — оголошений вихід, проміжним НЕ пишеться: вихід уже лягає
    /// рядком з тим самим <c>OutputCode</c>, і другий рядок (<c>Intermediate</c>)
    /// подвоїв би число в сітці, яка сумує рядки за кодом (<c>CalculatedCellOverlay</c>).
    /// Так <c>M_t</c> — «видимий і вихід» (§6.3) — лягає рівно один раз.
    ///
    /// ⚠ Формула без одиниці не пишеться: результат без одиниці заборонений (ФВ-16.6),
    /// а видиму формулу без одиниці не пропускає публікація (<c>visibleFormulaNoUnit</c>).
    /// Сюди така доходить лише з чернетки — у симуляції.
    /// </remarks>
    private static List<MethodologyFormula> VisibleIntermediates(
        IReadOnlyList<MethodologyFormula> formulas, HashSet<string> outputCodes)
        => [.. formulas.Where(f => f.IsVisible && f.OutputUnitId is not null && !outputCodes.Contains(f.Code))];

    /// <summary>Пише одне значення — вихід або проміжне — у результати рядка.</summary>
    /// <param name="values">Куди пишеться.</param>
    /// <param name="version">Версія рядка — режим арифметики й ідентифікатор.</param>
    /// <param name="binding">Контекст прив'язки — масштаби колонок-приймачів.</param>
    /// <param name="numeric">Числова політика версії.</param>
    /// <param name="trace">Трейс рядка.</param>
    /// <param name="code">Код виходу або видимої формули.</param>
    /// <param name="value">Значення з контексту обчислення.</param>
    /// <param name="unitId">Одиниця результату.</param>
    /// <param name="substance">Речовина; <c>null</c> — значення рівня рядка.</param>
    /// <param name="kind">Вихід чи проміжне.</param>
    private static void Emit(
        List<CalculationOutputValue> values,
        MethodologyDescriptor version,
        CalculationBindingContext binding,
        NumericPolicy numeric,
        TraceRecorder trace,
        string code,
        ExpressionValue value,
        int unitId,
        MethodologySubstance? substance,
        CalculationResultKind kind)
    {
        var masked = MaskedZero.Prepare(value, version.NumericMode);

        if (masked.Value is not { } number)
        {
            // ⚠ У `Strict` сюди потрапляє і замаскований випадок:
            // значення там `null`, а не нуль (`ФВ-9.14`). Причина
            // однаково має бути названа, тому запис іде окремим
            // кроком, а не загальним «#NULL».
            if (masked.Reason != MaskedZeroReason.None)
            {
                trace.Masked(code, null, null, masked.Reason);
                return;
            }

            // ⚠ Проміжне без числа — лише пропуск: крок формули з причиною вже
            // записав `Evaluate`, а другий такий самий запис нічого б не додав.
            if (kind == CalculationResultKind.Intermediate)
            {
                return;
            }

            // Вихід без числа не пишеться: нуль тут виглядав би як
            // порахований результат. Причина вже в трейсі.
            //
            // ⚠ Аудит A2: ЧИСЛО без `decimal`-подання — це `Legacy`
            // `double` за межею ≈7.9e28 (`Pow(10, 30)`). Колонка
            // результату його не вмістить, а «#NULL» збрехав би, що
            // значення не було; тому `#VALUE`, як у `Strict`.
            trace.Failed(
                code,
                null,
                value.ErrorCode
                    ?? (value.Type == Expressions.Ast.ExpressionValueType.Number
                        ? Expressions.ExpressionErrors.BadValue
                        : "#NULL"));
            return;
        }

        // ⛔ Замаскований нуль пишеться в трейс ЗАВЖДИ, коли він
        // стався. Число при цьому те саме, що дала б чинна система, —
        // саме тому знайти ці випадки можна лише за записом, і саме
        // вони обіцяні як найцінніший побічний результат міграції.
        if (masked.Reason != MaskedZeroReason.None)
        {
            trace.Masked(code, null, number, masked.Reason);
        }

        // ⛔ Скільки знаків несе результат — КОНФІГУРАЦІЯ КОЛОНКИ, у
        // яку він потрапляє (рішення людини 2026-09-20), а не спільна
        // константа рушія. Колонка мовчить — беруться всі шістнадцять
        // (`NumericPolicy.DefaultOutputScale`), бо саме стільки несе
        // конвеєр чинної системи; обрізати до шести «на всяк випадок»
        // означало б змінити число у звіті там, де ніхто про це не
        // просив. Проміжне, прив'язане до колонки (`M_t` у сітці), бере
        // масштаб тієї самої колонки тим самим кодом.
        var scale = binding.OutputScales.TryGetValue(code, out var declared)
            ? declared
            : null;

        values.Add(new CalculationOutputValue(
            version.MethodologyVersionId,
            substance is null ? null : checked((int)substance.SubstanceEntryId),
            code,
            numeric.RoundOutput(number, scale),
            unitId,
            kind));
    }

    /// <summary>Довідник одиниць, прочитаний раз на прогін.</summary>
    /// <remarks>
    /// ⚠ Кешується в екземплярі модуля, який живе один прогін. Похід у базу на
    /// кожну конверсію дав би мільйони запитів на річний перерахунок і сам
    /// собою вибрав би бюджет 10 хвилин.
    /// </remarks>
    private async Task<UnitTable> UnitsAsync(CancellationToken ct)
    {
        if (_units is not null)
        {
            return _units;
        }

        var snapshot = await unitCatalog.GetAsync(ct).ConfigureAwait(false);
        var table = new UnitTable();

        var codes = new Dictionary<int, string>();
        foreach (var unit in snapshot.Units.Values)
        {
            table.Add(unit.Code, unit.DimensionId, unit.FactorToBase, unit.OffsetToBase);
            codes.TryAdd(unit.Id, unit.Code);
        }

        _unitCodes = codes;
        _units = table;
        return table;
    }

    /// <summary>Обчислює одну формулу і фіксує крок у трейсі.</summary>
    private ExpressionValue Evaluate(
        MethodologyFormula formula,
        MethodologyEvaluationContext context,
        NumericPolicy numeric,
        TraceRecorder trace,
        TraceScope scope)
    {
        var parsed = formulaEngine.Parse(formula.Expression, ExpressionDialect.Methodology);
        if (!parsed.IsSuccess || parsed.Expression is null)
        {
            // Нерозібрана формула в опублікованій версії — дефект публікації,
            // але тут це помилка-ЗНАЧЕННЯ: один зламаний рядок не має валити
            // прогін на мільйон рядків.
            trace.Failed(formula.Code, formula.Expression, "#VALUE");
            return ExpressionValue.Error("#VALUE");
        }

        // ⛔ Режим версії передається явно (`I.7`): без нього рушій рахував
        // `Legacy` у `decimal`, де ані `NaN`, ані нескінченності не існує, —
        // тобто маскувати ніже було б нічого.
        var result = formulaEngine
            .Evaluate(parsed.Expression, context, numeric.Mode)
            .Value;

        if (result.IsError)
        {
            trace.Failed(
                formula.Code,
                formula.Expression,
                result.ErrorCode!,
                trace.RecordsFailures ? Describe(formula, parsed.Expression, context, numeric, scope) : null);
            return result;
        }

        // ⚠ Входи збираються лише для кроку, який запишеться: на `ErrorsOnly` невидима
        // формула не платить за обхід і повторне читання посилань нічим.
        var detail = trace.Records(formula.IsVisible)
            ? Describe(formula, parsed.Expression, context, numeric, scope)
            : null;

        if (result.AsNumber() is not { } number)
        {
            trace.Step(formula.Code, formula.Expression, null, formula.IsVisible, detail);
            return result;
        }

        // ⛔ Проміжний крок НЕ округлюється. Тут стояло `numeric.RoundStep`
        // з поясненням «Legacy округлює кожен крок, як чинна система» — і це
        // було вигадано (директива №05 §6).
        //
        // Вихідні тексти `DllProject` показують протилежне: у всіх 148 файлах
        // немає жодного `Math.Round`, жодного `MidpointRounding`, жодного
        // `decimal.Round`. Округлення в чинній системі відбувається виключно
        // всередині `Round()` самої формули; між формулами результат іде
        // рядком у форматі `G17`, який для `double` круговий — тобто
        // точність НЕ втрачається.
        //
        // ⚠ Друга і остання точка округлення — збереження проміжного
        // результату між ЗАЛЕЖНИМИ методологіями, де число проходить через
        // колонку і втрачає знаки за її типом. Це ребро графа
        // `calc.MethodologyDependency`, а не крок усередині формули.
        trace.Step(formula.Code, formula.Expression, number, formula.IsVisible, detail);

        return result;
    }

    /// <summary>Що формула прочитала: одиниця результату й входи для <c>TraceJson</c> v1.</summary>
    /// <remarks>
    /// ⛔ Значення входу — тим САМИМ рушієм у тому САМОМУ контексті, що й формула
    /// (рішення V-8, <c>D-177</c>): вузол посилання обчислюється як вираз. Власне
    /// читання аргументу чи властивості періоду тут було б другою семантикою, і трейс
    /// пояснював би не те число, яке пішло в результат.
    /// </remarks>
    private TraceDetail Describe(
        MethodologyFormula formula,
        ParsedExpression parsed,
        MethodologyEvaluationContext context,
        NumericPolicy numeric,
        TraceScope scope)
    {
        var inputs = new List<TraceInput>();

        foreach (var reference in ReferenceCollector.Collect(parsed.Root))
        {
            var value = FormatValue(formulaEngine.Evaluate(parsed with { Root = reference.Node }, context, numeric.Mode).Value);

            inputs.Add(reference.Kind switch
            {
                TraceInputKind.Argument => new TraceInput(
                    reference.Kind,
                    reference.Code,
                    value,
                    UnitCode(scope.Input.Arguments
                        .FirstOrDefault(a => string.Equals(a.ArgumentCode, reference.Code, StringComparison.OrdinalIgnoreCase))
                        ?.UnitId),
                    new TraceCell(scope.Input.TableInstanceId, scope.Input.SourceRowKey, reference.Code)),

                TraceInputKind.Constant => new TraceInput(
                    reference.Kind,
                    reference.Code,
                    value,
                    UnitCode(scope.ConstantUnits.GetValueOrDefault(reference.Code)),
                    SubstanceEntryId: scope.SubstanceEntryId),

                TraceInputKind.Formula => new TraceInput(
                    reference.Kind,
                    reference.Code,
                    value,
                    UnitCode(scope.Formulas.TryGetValue(reference.Code, out var source) ? source.OutputUnitId : null)),

                _ => new TraceInput(reference.Kind, reference.Code, value, null, PeriodOffset: reference.PeriodOffset),
            });
        }

        return new TraceDetail(UnitCode(formula.OutputUnitId), inputs);
    }

    /// <summary>Код одиниці за Id; невідома одиниця — <c>null</c>, а не вигаданий код.</summary>
    private string? UnitCode(int? unitId)
        => unitId is { } id && _unitCodes.TryGetValue(id, out var code) ? code : null;

    /// <summary>Значення входу текстом — так, як його записує <c>TraceJson</c> v1.</summary>
    private static string? FormatValue(ExpressionValue value)
        => value.Type switch
        {
            Expressions.Ast.ExpressionValueType.Null => null,
            Expressions.Ast.ExpressionValueType.Error => value.ErrorCode,
            Expressions.Ast.ExpressionValueType.Number => value.AsNumber() is { } number
                ? TraceJson.Format(number)
                : value.AsDouble()?.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            Expressions.Ast.ExpressionValueType.Boolean => (bool)value.Value! ? "true" : "false",
            Expressions.Ast.ExpressionValueType.Date =>
                ((DateTime)value.Value!).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            _ => value.Value as string,
        };

    /// <summary>Звідки трейс кроку бере адресу входів і їхні одиниці.</summary>
    /// <param name="Input">Рядок, що рахується.</param>
    /// <param name="Formulas">Формули версії за кодом.</param>
    /// <param name="ConstantUnits">Одиниці розв'язаних констант цієї речовини.</param>
    /// <param name="SubstanceEntryId">Речовина; <c>null</c> — рівень рядка.</param>
    private sealed record TraceScope(
        CalculationInput Input,
        IReadOnlyDictionary<string, MethodologyFormula> Formulas,
        IReadOnlyDictionary<string, int?> ConstantUnits,
        long? SubstanceEntryId);

    /// <summary>Резолвить усі константи, згадані у формулах, для однієї речовини.</summary>
    /// <remarks>
    /// ⛔ Без походу в базу: кандидати прочитано в <see cref="PrepareAsync(MethodologyDescriptor, long, PeriodKey, RegistrySnapshotCache, CancellationToken)"/>
    /// (аудит P1), тут — лише вибір у пам'яті.
    /// </remarks>
    private Dictionary<string, ExpressionValue> ResolveConstants(
        MethodologyDescriptor version,
        IReadOnlyList<MethodologyFormula> formulas,
        long? substanceEntryId,
        Expressions.PeriodContext period,
        IReadOnlyDictionary<string, IReadOnlyList<MethodologyConstant>> candidatesByCode,
        Dictionary<string, int?> resolvedUnits)
    {
        var resolved = new Dictionary<string, ExpressionValue>(StringComparer.OrdinalIgnoreCase);

        foreach (var code in ConstantCodes(formulas))
        {
            var constant = constants.Resolve(
                candidatesByCode.TryGetValue(code, out var candidates) ? candidates : [],
                version.MethodologyVersionId,
                code,
                category: null,
                substanceEntryId,

                // ⚠ Дата періоду, а не «сьогодні»: константа темпоральна, і
                // перерахунок минулого року цього року має брати коефіцієнт,
                // чинний тоді (ФВ-16.5).
                period.End);

            // ⛔ Текстова константа підставляється ТЕКСТОМ, а не числом
            // (поправка 2-біс директиви ПК-1 №05). У корпусі ~90 констант
            // стоять операндом порівняння —
            // `if(@Land_Category = CST.k1_CategorySelection_, …)`; спроба
            // зробити з `'Summer'` число дала б помилку обчислення на кожному
            // рядку, де категорія збігається.
            if (constant is { } found)
            {
                resolved[code] = found.Number is { } number
                    ? ExpressionValue.Number(number)
                    : ExpressionValue.Text(found.Text ?? string.Empty);

                // Одиниця — лише для трейсу (§7.2): у вираз константа йде числом.
                resolvedUnits[code] = found.UnitId;
            }
        }

        return resolved;
    }

    /// <summary>Коди констант, згадані у виразах версії.</summary>
    /// <remarks>
    /// Витягуються з тексту, а не з окремого списку: список довелося б
    /// підтримувати руками, і формула з новою константою мовчки читала б
    /// <c>#REF</c>.
    /// </remarks>
    private HashSet<string> ConstantCodes(IReadOnlyList<MethodologyFormula> formulas)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var formula in formulas)
        {
            var parsed = formulaEngine.Parse(formula.Expression, ExpressionDialect.Methodology);
            if (!parsed.IsSuccess || parsed.Expression is null)
            {
                continue;
            }

            foreach (var code in Walk(parsed.Expression.Root))
            {
                codes.Add(code);
            }
        }

        return codes;
    }

    /// <summary>Обхід дерева у пошуку <c>CST.Code</c>.</summary>
    private static IEnumerable<string> Walk(Expressions.Ast.AstNode node)
    {
        switch (node)
        {
            case Expressions.Ast.SymbolReferenceNode { Kind: Expressions.Ast.SymbolKind.Constant } symbol:
                yield return symbol.Name;
                break;

            case Expressions.Ast.BinaryNode binary:
                foreach (var code in Walk(binary.Left).Concat(Walk(binary.Right)))
                {
                    yield return code;
                }

                break;

            case Expressions.Ast.UnaryNode unary:
                foreach (var code in Walk(unary.Operand))
                {
                    yield return code;
                }

                break;

            case Expressions.Ast.ConditionalNode conditional:
                foreach (var code in Walk(conditional.Condition)
                             .Concat(Walk(conditional.WhenTrue))
                             .Concat(Walk(conditional.WhenFalse)))
                {
                    yield return code;
                }

                break;

            case Expressions.Ast.FunctionNode function:
                foreach (var code in function.Arguments.SelectMany(Walk))
                {
                    yield return code;
                }

                break;

            default:
                break;
        }
    }

    /// <summary>Календарний контекст періоду за режимом версії.</summary>
    /// <remarks>
    /// ⛔ Межі беруться з <c>doc.Period</c>, а НЕ виводяться з
    /// <c>PeriodKey</c>. Вивести їх із ключа неможливо:
    /// <c>PeriodKey = Year*100 + Sequence</c> (R-A6), і для квартального
    /// проєкту <c>202602</c> — це другий КВАРТАЛ. Тлумачити <c>Sequence</c> як
    /// місяць означало б поділити на 28 днів замість 91: усі <c>г/с</c> у
    /// звіті стали б утричі більшими, і жодна перевірка цього не побачила б —
    /// число залишається правдоподібним (ФВ-16.11a, D-112).
    /// </remarks>
    private async Task<Expressions.PeriodContext> PeriodAsync(
        MethodologyDescriptor version, long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        var bounds = await periods
            .FindPeriodBoundsAsync(documentId, periodKey.Value, ct)
            .ConfigureAwait(false)
            ?? throw new Domain.Abstractions.DomainException(
                "ECR-PRD-0404",
                $"Періоду {periodKey.Value} для документа {documentId} не існує: "
                + "тривалість обчислити нема з чого.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-0404.periodForDocument",
                    ["periodKey"] = periodKey.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });

        // Sequence — порядковий номер періоду в році, і саме він, а не місяць:
        // у квартальному проєкті їх чотири (R-A6, D-108).
        var sequence = (byte)(periodKey.Value % 100);

        return calendar.Build(
            bounds.PeriodStart, bounds.PeriodEnd, version.CalendarMode,
            periodKey.Value / 100, sequence);
    }

    /// <summary>Аргумент розрахунку як значення виразу.</summary>
    /// <remarks>
    /// ✎ RT-23a: <c>EntryRef</c> (<see cref="CalculationArgument.EntryId"/>) у рантаймі —
    /// число, id запису (§5.3), той самий вибір, що вже діє для <c>Lookup</c>-комірок у
    /// шаблонах. Його заповнює лише <see cref="CalculationInputBuilder"/> і лише для
    /// <c>Strict</c> (<c>D-161</c>), тож для <c>Legacy</c> гілка недосяжна.
    /// </remarks>
    private static ExpressionValue ToValue(CalculationArgument argument)
        => argument.EntryId is { } entryId
            ? ExpressionValue.Number(entryId)
            : argument.Value is { } number
                ? ExpressionValue.Number(number)
                : argument.ValueString is { } text
                    ? ExpressionValue.Text(text)
                    : ExpressionValue.Null;
}
