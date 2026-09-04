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
    UnitTable units) : ICalculationModule
{
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

        var period = PeriodOf(version, input.PeriodKey);
        var arguments = input.Arguments.ToDictionary(
            a => a.ArgumentCode, ToValue, StringComparer.OrdinalIgnoreCase);

        var values = new List<CalculationOutputValue>();

        // ⛔ Для КОЖНОЇ речовини — власний прогін. Константи резолвляться за
        // речовиною, тому спільний контекст дав би всім речовинам коефіцієнт
        // тієї, яку порахували першою (ФВ-9.1).
        var targets = substances
            .Where(s => s.IsActive)
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

        // Момент округлення визначає NumericMode: Legacy округлює КОЖЕН крок,
        // як чинна система, Strict — лише вихід (ФВ-9.9).
        var rounded = numeric.RoundStep(number);
        trace.Step(formula.Code, formula.Expression, rounded);

        return ExpressionValue.Number(rounded);
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
    private Expressions.PeriodContext PeriodOf(
        MethodologyDescriptor version, Domain.ValueObjects.PeriodKey periodKey)
    {
        // PeriodKey = Year*100 + Sequence (R-A6). Місячний період — саме
        // Sequence-й місяць року; інші гранулярності приходять із періоду
        // проєкту і в цій точці ще не потрібні.
        var year = periodKey.Value / 100;
        var sequence = periodKey.Value % 100;

        var start = new DateOnly(year, sequence, 1);
        var end = new DateOnly(year, sequence, DateTime.DaysInMonth(year, sequence));

        return calendar.Build(start, end, version.CalendarMode, year, (byte)sequence);
    }

    /// <summary>Аргумент розрахунку як значення виразу.</summary>
    private static ExpressionValue ToValue(CalculationArgument argument)
        => argument.Value is { } number
            ? ExpressionValue.Number(number)
            : argument.ValueString is { } text
                ? ExpressionValue.Text(text)
                : ExpressionValue.Null;
}
