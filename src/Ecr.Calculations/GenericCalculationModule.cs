using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
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
public sealed class GenericCalculationModule(
    IFormulaEngine formulaEngine,
    IMethodologyStore methodologies,
    ConstantResolver constants,
    CalendarContext calendar,
    IUnitCatalog unitCatalog,
    IPeriodStore periods) : ICalculationModule
{
    private UnitTable? _units;
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
    public async Task<CalculationOutput> ExecuteAsync(CalculationInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var version = input.Methodology;
        var numeric = new NumericPolicy(version.NumericMode);
        var trace = new TraceRecorder(version.TraceLevel);

        var formulas = await methodologies
            .GetFormulasAsync(version.MethodologyVersionId, ct).ConfigureAwait(false);
        var substances = await methodologies
            .GetSubstancesAsync(version.MethodologyVersionId, ct).ConfigureAwait(false);
        var outputs = await methodologies
            .GetOutputsAsync(version.MethodologyVersionId, ct).ConfigureAwait(false);

        // ⚠ Порядок беремо з EvaluationOrder — він топологічний із Publish
        // (ФВ-9.4). Сортувати граф тут заборонено: порядок мусить бути тим
        // самим, за яким версію перевірили тестами, а не тим, який вийде
        // сьогодні.
        var ordered = formulas.OrderBy(f => f.EvaluationOrder).ThenBy(f => f.Id).ToList();

        var period = await PeriodAsync(version, input, ct).ConfigureAwait(false);
        var arguments = input.Arguments.ToDictionary(
            a => a.ArgumentCode, ToValue, StringComparer.OrdinalIgnoreCase);

        var values = new List<CalculationOutputValue>();

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

        foreach (var substance in targets)
        {
            var resolved = await ResolveConstantsAsync(
                version, ordered, substance?.SubstanceEntryId, period, ct).ConfigureAwait(false);

            var units = await UnitsAsync(ct).ConfigureAwait(false);
            var context = new MethodologyEvaluationContext(period, arguments, resolved, units);

            foreach (var formula in ordered)
            {
                var value = Evaluate(formula, context, numeric, trace);
                context.SetFormulaResult(formula.Code, value);
            }

            foreach (var output in outputs)
            {
                var value = context.GetFormulaResult(output.Code);
                if (value.AsNumber() is not { } number)
                {
                    // Вихід без числа не пишеться: нуль тут виглядав би як
                    // порахований результат. Причина вже в трейсі.
                    trace.Failed(output.Code, null, value.ErrorCode ?? "#NULL");
                    continue;
                }

                values.Add(new CalculationOutputValue(
                    version.MethodologyVersionId,
                    substance is null ? null : checked((int)substance.SubstanceEntryId),
                    output.Code,
                    numeric.RoundOutput(number),
                    output.UnitId));
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
                .Select(s => new CalculationTraceStep(s.Order, s.Code, s.Expression, s.Value, s.Error))
                .ToList());
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

        foreach (var unit in snapshot.Units.Values)
        {
            table.Add(unit.Code, unit.DimensionId, unit.FactorToBase, unit.OffsetToBase);
        }

        _units = table;
        return table;
    }

    /// <summary>Обчислює одну формулу і фіксує крок у трейсі.</summary>
    private ExpressionValue Evaluate(
        MethodologyFormula formula,
        MethodologyEvaluationContext context,
        NumericPolicy numeric,
        TraceRecorder trace)
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

        var result = formulaEngine.Evaluate(parsed.Expression, context).Value;

        if (result.IsError)
        {
            trace.Failed(formula.Code, formula.Expression, result.ErrorCode!);
            return result;
        }

        if (result.AsNumber() is not { } number)
        {
            trace.Step(formula.Code, formula.Expression, null);
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
        trace.Step(formula.Code, formula.Expression, number);

        return result;
    }

    /// <summary>Резолвить усі константи, згадані у формулах, для однієї речовини.</summary>
    private async Task<Dictionary<string, ExpressionValue>> ResolveConstantsAsync(
        MethodologyDescriptor version,
        IReadOnlyList<MethodologyFormula> formulas,
        long? substanceEntryId,
        Expressions.PeriodContext period,
        CancellationToken ct)
    {
        var resolved = new Dictionary<string, ExpressionValue>(StringComparer.OrdinalIgnoreCase);

        foreach (var code in ConstantCodes(formulas))
        {
            var constant = await constants.ResolveAsync(
                version.MethodologyVersionId,
                code,
                category: null,
                substanceEntryId,

                // ⚠ Дата періоду, а не «сьогодні»: константа темпоральна, і
                // перерахунок минулого року цього року має брати коефіцієнт,
                // чинний тоді (ФВ-16.5).
                period.End,
                ct).ConfigureAwait(false);

            if (constant is { } found)
            {
                resolved[code] = ExpressionValue.Number(found.Value);
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
        MethodologyDescriptor version, CalculationInput input, CancellationToken ct)
    {
        var bounds = await periods
            .FindPeriodBoundsAsync(input.DocumentId, input.PeriodKey.Value, ct)
            .ConfigureAwait(false)
            ?? throw new Domain.Abstractions.DomainException(
                "ECR-PRD-0404",
                $"Періоду {input.PeriodKey.Value} для документа {input.DocumentId} не існує: "
                + "тривалість обчислити нема з чого.");

        // Sequence — порядковий номер періоду в році, і саме він, а не місяць:
        // у квартальному проєкті їх чотири (R-A6, D-108).
        var sequence = (byte)(input.PeriodKey.Value % 100);

        return calendar.Build(
            bounds.PeriodStart, bounds.PeriodEnd, version.CalendarMode,
            input.PeriodKey.Value / 100, sequence);
    }

    /// <summary>Аргумент розрахунку як значення виразу.</summary>
    private static ExpressionValue ToValue(CalculationArgument argument)
        => argument.Value is { } number
            ? ExpressionValue.Number(number)
            : argument.ValueString is { } text
                ? ExpressionValue.Text(text)
                : ExpressionValue.Null;
}
