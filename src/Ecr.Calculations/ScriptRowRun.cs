using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.Entities.Calculations;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Scripting;

namespace Ecr.Calculations;

/// <summary>Вид величини живого перегляду.</summary>
public enum ScriptValueKind : byte
{
    /// <summary>Проміжна величина <c>var</c>.</summary>
    Var,

    /// <summary>Вихід <c>output</c>.</summary>
    Output,

    /// <summary>Видима проміжна величина <c>visible var</c>.</summary>
    Visible,
}

/// <summary>Одна обчислена величина рядка — для живого перегляду скрипта.</summary>
/// <param name="Name">Ім'я величини.</param>
/// <param name="SubstanceCode">Код речовини ітерації <c>foreach</c>; <c>null</c> — рівень рядка.</param>
/// <param name="Kind">Вид.</param>
/// <param name="ValueType">Тип значення: <c>Number</c>, <c>Text</c>, <c>Boolean</c>, <c>Date</c>, <c>Null</c>, <c>Error</c>.</param>
/// <param name="Text">Значення текстом (число — інваріантно); <c>null</c> — немає.</param>
/// <param name="ErrorCode">Код помилки, якщо значення — помилка.</param>
public sealed record ScriptValue(
    string Name, string? SubstanceCode, ScriptValueKind Kind, string ValueType, string? Text, string? ErrorCode);

/// <summary>
/// Межі одного прогону рядка понад типові (PSS-21): спільний бюджет кроків запиту й стеля величин живого перегляду.
/// </summary>
/// <param name="Pool">
/// Спільний бюджет кроків усіх рядків запиту (живий перегляд): рядок бере не більше, ніж лишилося, і витрачене
/// списується з нього. <c>null</c> — лише межа рядка (<see cref="CalculationLimits.MaxRowEvaluationSteps"/>).
/// </param>
/// <param name="MaxValues">Скільки величин живого перегляду зібрати з рядка; решта не збирається.</param>
public sealed record ScriptRunLimits(EvaluationBudget? Pool = null, int MaxValues = int.MaxValue)
{
    /// <summary>Типові межі: лише бюджет рядка, величин без стелі.</summary>
    public static ScriptRunLimits Default { get; } = new();
}

/// <summary>
/// Виконує оператори скрипта на ОДИН рядок згори вниз (MS, план §3.7). Числа — лише через
/// <see cref="Evaluator"/> у контексті; маскування <c>Legacy</c>, кроки трейсу, шкали й округлення виходу —
/// <see cref="CalculationSteps"/>, той самий код, що в старого модуля.
/// </summary>
/// <remarks>
/// ⛔ PSS-21: бюджет кроків — не лише на оператор (<see cref="Evaluator.MaxEvaluationSteps"/>), а й на РЯДОК
/// (<see cref="CalculationLimits.MaxRowEvaluationSteps"/>): тисячі операторів по 20 000 кроків кожен інакше
/// тримали б потік хвилинами на один рядок. Вичерпаний бюджет рядка — <c>#BUDGET</c> на кожен наступний оператор без
/// обчислення (детерміновано: рахується робота, а не час). Скасування перевіряється перед кожним оператором.
/// <para>
/// ⛔ <c>reject when</c>, умова якого не порахувалася через вичерпаний бюджет, — не відхилення рядка, а помилка кроку
/// <c>#BUDGET</c> (рецензія PSS, п. 4): невідомо, відхилив би скрипт рядок чи ні, тож рядок не дає жодного числа й
/// має проблему <c>StepError</c>, а не «відхилено правилом».
/// </para>
/// </remarks>
internal sealed class ScriptRowRun(
    ScriptEvaluationContext context,
    ScriptRuntime runtime,
    CalculationBindingContext binding,
    CalculationInput input,
    NumericPolicy numeric,
    TraceRecorder trace,
    List<CalculationOutputValue> values,
    List<ScriptValue>? detail,
    ScriptRunLimits limits,
    int rowSteps,
    CancellationToken ct)
{
    private enum Flow : byte
    {
        Next,
        Skip,
    }

    private readonly MethodologyDescriptor _version = input.Methodology;
    private readonly EvaluationBudget? _row = RowBudget(limits.Pool, rowSteps);
    private string? _substanceCode;
    private MethodologySubstance? _substance;

    // Рядок зупинено відмовою з #BUDGET (BudgetFailedReject): решта операторів не виконується.
    private bool _halted;

    /// <summary>Скільки кроків витратив рядок.</summary>
    public int Spent => _row?.Spent ?? 0;

    /// <summary>Виконує скрипт версії.</summary>
    /// <exception cref="OperationCanceledException">Запит чи задачу скасовано між операторами.</exception>
    public void Run()
    {
        var script = binding.Script!;
        try
        {
            foreach (var statement in script.Statements)
            {
                Execute(statement);
            }
        }
        finally
        {
            // Витрачене рядком — зі спільного бюджету запиту, і тоді, коли рядок перервано.
            limits.Pool?.TryConsume(Spent);
        }

        trace.EnterSubstance(null);
    }

    /// <summary>Бюджет рядка: межа рядка, але не більше, ніж лишилося в спільному; <c>null</c> — спільний вичерпано.</summary>
    private static EvaluationBudget? RowBudget(EvaluationBudget? pool, int rowSteps)
    {
        var allowed = pool is null
            ? rowSteps
            : Math.Min(rowSteps, pool.MaxSteps - pool.Spent);
        return allowed > 0 ? new EvaluationBudget(allowed) : null;
    }

    private Flow ExecuteBlock(ScriptBlock block)
    {
        context.PushScope();
        try
        {
            foreach (var statement in block.Statements)
            {
                if (Execute(statement) == Flow.Skip)
                {
                    return Flow.Skip;
                }
            }

            return Flow.Next;
        }
        finally
        {
            context.PopScope();
        }
    }

    private Flow Execute(ScriptStatement statement)
    {
        // PSS-21: розірваний запит (клієнт пішов, вичерпано час перегляду) чи скасована задача не рахує решту операторів.
        ct.ThrowIfCancellationRequested();

        if (_halted)
        {
            return Flow.Skip;
        }

        switch (statement)
        {
            case VarStatement variable:
                Var(variable);
                return Flow.Next;

            case OutputStatement output:
                Output(output);
                return Flow.Next;

            case RejectStatement reject:
                Reject(reject);
                return Flow.Next;

            case SkipStatement skip:
                return Evaluate(skip.Cond) is { Type: ExpressionValueType.Boolean, Value: true }
                    ? SkipSubstance()
                    : Flow.Next;

            case IfStatement branch:
                If(branch);
                return Flow.Next;

            case ForeachSubstanceStatement loop:
                Foreach(loop);
                return Flow.Next;

            default:
                return Flow.Next;
        }
    }

    private Flow SkipSubstance()
    {
        trace.Step("SubstanceNotApplicable", _substanceCode, value: null);
        return Flow.Skip;
    }

    /// <summary>
    /// Один бюджет на оператор (план §3.7), не більший за залишок бюджету рядка; витрачене списується з рядка.
    /// Рядок, що вичерпав бюджет, — <c>#BUDGET</c> без обчислення.
    /// </summary>
    private ExpressionValue Evaluate(AstNode node)
    {
        if (_row is null || _row.IsExhausted)
        {
            return ExpressionValue.Error(Expressions.ExpressionErrors.BudgetExceeded);
        }

        var budget = new EvaluationBudget(Math.Min(Evaluator.MaxEvaluationSteps, _row.MaxSteps - _row.Spent));
        context.Budget = budget;
        try
        {
            return context.Evaluate(node, budget);
        }
        finally
        {
            context.Budget = null;
            _row.TryConsume(budget.Spent);
        }
    }

    private ExpressionValue Step(string code, string text, AstNode expression, string? unitCode, bool visible)
    {
        var result = Evaluate(expression);
        return CalculationSteps.FinishStep(
            code,
            text,
            result,
            numeric,
            trace,
            visible,
            source: null,
            (context, expression, unitCode),
            static state => state.context.Describe(state.expression, state.unitCode));
    }

    private void Var(VarStatement statement)
    {
        var value = Step(statement.Name, statement.SourceText, statement.Expr, statement.UnitCode, statement.IsVisible);
        context.SetVariable(statement.Name, value, statement.UnitCode);
        Record(statement.Name, statement.IsVisible ? ScriptValueKind.Visible : ScriptValueKind.Var, value);

        if (!statement.IsVisible)
        {
            return;
        }

        // Рівень Module: що саме емітувати й з якою одиницею, вирішує зовнішній модуль через binding.Outputs.
        if (_version.Level == CalculationLevel.Module)
        {
            EmitModuleTarget(statement.Name, value);
            return;
        }

        if (statement.UnitCode is { } unitCode && runtime.UnitIds.TryGetValue(unitCode, out var unitId))
        {
            CalculationSteps.EmitValue(
                values, _version, binding, numeric, trace, statement.Name, value, unitId, _substance,
                CalculationResultKind.Intermediate);
        }
        else
        {
            trace.Failed(statement.Name, null, Expressions.ExpressionErrors.BadUnit);
        }
    }

    private void Output(OutputStatement statement)
    {
        // Рівень Module: крок успішної формули на ErrorsOnly не пишеться (як у формульної версії без IsVisible).
        var value = Step(
            statement.Code, statement.SourceText, statement.Expr, statement.UnitCode,
            visible: _version.Level != CalculationLevel.Module);
        context.SetVariable(statement.Code, value, statement.UnitCode);
        Record(statement.Code, ScriptValueKind.Output, value);

        var materialized = binding.Outputs.FirstOrDefault(o => string.Equals(o.Code, statement.Code, StringComparison.OrdinalIgnoreCase));
        if (_version.Level == CalculationLevel.Module)
        {
            EmitModuleTarget(statement.Code, value);
            return;
        }

        int unitId;
        if (materialized is not null)
        {
            unitId = materialized.UnitId;
        }
        else if (!runtime.UnitIds.TryGetValue(statement.UnitCode, out unitId))
        {
            // Одиниці виходу немає ні в матеріалізованих виходах, ні в довіднику: значення без одиниці не пишемо.
            trace.OutputFailed(statement.Code, Expressions.ExpressionErrors.BadUnit);
            trace.Failed(statement.Code, null, Expressions.ExpressionErrors.BadUnit);
            return;
        }

        CalculationSteps.EmitValue(
            values, _version, binding, numeric, trace, statement.Code, value, unitId,
            statement.SubstanceVar is null ? null : _substance, CalculationResultKind.Output);
    }

    /// <summary>
    /// Рівень <c>Module</c> (HSE400): виходи й масштаби задає зовнішній модуль (<c>binding.Outputs</c> /
    /// <c>OutputScales</c>) — емітується рівно те, що в переліку, з його одиницею; решта кроків — лише проміжні.
    /// </summary>
    private void EmitModuleTarget(string code, ExpressionValue value)
    {
        if (binding.Outputs.FirstOrDefault(o => string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase)) is { } target)
        {
            CalculationSteps.EmitValue(
                values, _version, binding, numeric, trace, code, value, target.UnitId, null, CalculationResultKind.Output);
        }
    }

    private void Reject(RejectStatement statement)
    {
        var condition = Evaluate(statement.Cond);
        if (condition is { Type: ExpressionValueType.Boolean, Value: false })
        {
            return;
        }

        // ⛔ Бюджет вичерпано (рецензія PSS, п. 4): умову не пораховано, тож невідомо, чи скрипт відхилив би рядок.
        // Це помилка кроку #BUDGET, а не відхилення правилом: жодного числа рядка (навіть уже пораховані виходи — їх
        // відхилення могло б скасувати), кожен вихід — без результату з #BUDGET, проблема рядка — StepError.
        if (condition.ErrorCode == Expressions.ExpressionErrors.BudgetExceeded)
        {
            BudgetFailedReject(statement);
            return;
        }

        // ⛔ Той самий виняток і ключ, що в правила категорії: оркестратор відхиляє лише цей рядок
        // (`GenericCalculationModule.IsCategoryRuleRowFailure`) і пише проблему CategoryRuleRejected.
        throw new DomainException(
            "ECR-CALC-0422",
            $"Скрипт відхилив рядок: «{statement.SourceText}».",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-CALC-0422.categoryRuleFailed",
                ["expression"] = statement.SourceText,
                ["reason"] = condition.IsError ? condition.ErrorCode : "reject",
            });
    }

    /// <summary>
    /// <c>reject when</c> із умовою <c>#BUDGET</c>: рядок без чисел, кожен вихід скрипта — без результату з
    /// <c>#BUDGET</c> (звідси <c>StepError</c> рядка), крок відмови — у трейсі.
    /// </summary>
    private void BudgetFailedReject(RejectStatement statement)
    {
        trace.Failed("reject", statement.SourceText, Expressions.ExpressionErrors.BudgetExceeded);
        foreach (var output in binding.Script!.Outputs)
        {
            trace.OutputFailed(output.Code, Expressions.ExpressionErrors.BudgetExceeded);
        }

        values.Clear();

        // Решта операторів не виконується: #BUDGET умови буває й від стелі ОДНОГО оператора, коли бюджет рядка ще є, —
        // наступні виходи інакше дали б числа рядку, долю якого відмова мала вирішити.
        _halted = true;
    }

    private void If(IfStatement statement)
    {
        var condition = Evaluate(statement.Cond);
        if (condition is { Type: ExpressionValueType.Boolean })
        {
            if (condition.Value is true)
            {
                ExecuteBlock(statement.Then);
            }
            else if (statement.Else is { } otherwise)
            {
                ExecuteBlock(otherwise);
            }

            return;
        }

        // Жодна гілка не виконується: кожен вихід, оголошений у гілках, лишається без результату — з причиною.
        var error = condition.IsError
            ? condition.ErrorCode!
            : condition.IsNull ? TraceRecorder.NoValueError : Expressions.ExpressionErrors.BadValue;
        foreach (var code in statement.OutputCodes)
        {
            trace.OutputFailed(code, error);
        }
    }

    private void Foreach(ForeachSubstanceStatement loop)
    {
        foreach (var code in loop.Codes)
        {
            if (binding.SubstanceIds is not { } ids || !ids.TryGetValue(code, out var entryId))
            {
                trace.Step("SubstanceUnknown", code, value: null);
                continue;
            }

            _substanceCode = code;
            _substance = binding.Substances.FirstOrDefault(s => s.SubstanceEntryId == entryId)
                         ?? new MethodologySubstance(_version.MethodologyVersionId, entryId);
            trace.EnterSubstance(entryId);
            context.EnterSubstance(loop.VarName, code, entryId);
            try
            {
                ExecuteBlock(loop.Body);
            }
            finally
            {
                context.LeaveSubstance();
                _substanceCode = null;
                _substance = null;
            }
        }

        trace.EnterSubstance(null);
    }

    private void Record(string name, ScriptValueKind kind, ExpressionValue value)
    {
        // PSS-21: величин живого перегляду не більше стелі — тисячі операторів на сотні рядків інакше росли б у пам'яті
        // й у відповіді без межі.
        if (detail is null || detail.Count >= limits.MaxValues)
        {
            return;
        }

        detail.Add(new ScriptValue(
            name, _substanceCode, kind, value.Type.ToString(), ScriptEvaluationContext.Format(value), value.ErrorCode));
    }
}
