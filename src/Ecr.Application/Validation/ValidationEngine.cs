using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Validation;

/// <summary>
/// Виконує правила валідації. Рівні розрізняються не за суворістю тексту, а
/// за тим, що вони блокують (R-B3): комірковий <c>Error</c> блокує запис,
/// решта — лише подання.
/// </summary>
public sealed class ValidationEngine(IFormulaEngine formulaEngine)
{
    /// <summary>Код, під яким повідомляється про несправне правило.</summary>
    /// <remarks>
    /// ⚠ Не збігається з кодом жодного правила навмисно: це повідомлення про
    /// КОНФІГУРАЦІЮ, а не про дані, і плутати їх не можна.
    /// </remarks>
    public const string BrokenRuleCode = "ECR-VAL-RULE";

    /// <summary>Перевіряє одну комірку — виконується синхронно на шляху запису.</summary>
    /// <param name="column">Колонка, до якої належить значення.</param>
    /// <param name="value">Значення комірки.</param>
    /// <param name="rules">Правила таблиці; беруться лише з <c>Scope = 0</c>.</param>
    public IReadOnlyList<ValidationMessage> ValidateCell(
        ColumnDef column, Domain.ValueObjects.CellValueData value, IReadOnlyList<ValidationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(rules);

        var messages = new List<ValidationMessage>();

        // Спершу СТРУКТУРНА перевірка: тип, обов'язковість, довідник, одиниця.
        // Вона не залежить від конфігурації правил і тому не може «зламатися».
        if (column.ValidateValue(value) is { } structural)
        {
            messages.Add(new ValidationMessage(
                ValidationSeverity.Error, "ECR-CELL-0422", structural,
                column.TableDefId, null, column.Code, BlocksSave: true));
        }

        foreach (var rule in rules.Where(r => r.IsActive && r.Scope == 0))
        {
            if (rule.ColumnDefId is { } columnId && columnId != column.Id)
            {
                continue;
            }

            Evaluate(rule, CellContext(column, value), column.TableDefId, null, column.Code, messages);
        }

        // ⚠ Повертаються ВСІ порушення, а не перше: користувач має побачити
        // список того, що виправити, а не отримувати їх по одному.
        return messages;
    }

    /// <summary>Перевіряє рядок, таблицю або документ — не блокує запис.</summary>
    /// <param name="scope">Рівень правил: 1 рядок, 2 таблиця, 3 документ.</param>
    /// <param name="rules">Правила таблиці.</param>
    /// <param name="context">Джерело значень для виразів правил.</param>
    public IReadOnlyList<ValidationMessage> ValidateScope(
        byte scope, IReadOnlyList<ValidationRule> rules, IValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(context);

        var messages = new List<ValidationMessage>();

        foreach (var rule in rules.Where(r => r.IsActive && r.Scope == scope))
        {
            Evaluate(rule, new ScopeContext(context), rule.TableDefId, null, null, messages);
        }

        return messages;
    }

    private void Evaluate(
        ValidationRule rule,
        IEvaluationContext context,
        int tableDefId,
        string? rowKey,
        string? columnCode,
        List<ValidationMessage> messages)
    {
        var parsed = formulaEngine.Parse(rule.Expression, ExpressionDialect.Template);
        if (!parsed.IsSuccess || parsed.Expression is null)
        {
            messages.Add(Broken(rule, tableDefId, rowKey, columnCode,
                $"Правило '{rule.Code}' не розбирається: {(parsed.Diagnostics.Count > 0 ? parsed.Diagnostics[0].Message : string.Empty)}"));
            return;
        }

        var result = formulaEngine.Evaluate(parsed.Expression, context);
        var value = result.Value;

        // ⚠ Помилка ОБЧИСЛЕННЯ виразу — це Warning про несправне ПРАВИЛО, а не
        // Error даних. Інакше зламане правило заблокувало б роботу з цілком
        // коректними даними, і виправити його змогла б лише людина з доступом
        // до конфігурації — тобто не той, хто зараз заповнює звіт.
        if (value.IsError || value.Type != ExpressionValueType.Boolean)
        {
            messages.Add(Broken(rule, tableDefId, rowKey, columnCode,
                $"Правило '{rule.Code}' не дало логічної відповіді: {value.ErrorCode ?? value.Type.ToString()}"));
            return;
        }

        if ((bool)value.Value!)
        {
            return;
        }

        messages.Add(new ValidationMessage(
            rule.Severity, rule.Code, rule.MessageL10n.Get("en") ?? rule.Code, tableDefId, rowKey, columnCode,
            // Блокує запис ЛИШЕ комірковий Error (R-B3, D-90): заборона
            // зберегти проміжний стан зробила б роботу з великою таблицею
            // неможливою.
            rule.BlocksSave));
    }

    private static ValidationMessage Broken(
        ValidationRule rule, int tableDefId, string? rowKey, string? columnCode, string message)
        => new(ValidationSeverity.Warning, BrokenRuleCode, message, tableDefId, rowKey, columnCode, BlocksSave: false);

    private static SingleCellContext CellContext(ColumnDef column, Domain.ValueObjects.CellValueData value)
        => new SingleCellContext(column.Code, CellValueMapping.ToExpressionValue(value, ExpressionValue.Null));

    /// <summary>Контекст правила рівня комірки: видно рівно одну колонку.</summary>
    private sealed class SingleCellContext(string columnCode, ExpressionValue value) : ValidationEvaluationContext
    {
        public override IReadOnlyList<ExpressionValue> Read(CellReferenceNode reference)
        {
            ArgumentNullException.ThrowIfNull(reference);
            return [string.Equals(reference.ColumnSelector, columnCode, StringComparison.OrdinalIgnoreCase)
                ? value
                : ExpressionValue.Null];
        }
    }

    /// <summary>Контекст правила рівня рядка і вище.</summary>
    private sealed class ScopeContext(IValidationContext inner) : ValidationEvaluationContext
    {
        public override IReadOnlyList<ExpressionValue> Read(CellReferenceNode reference)
        {
            ArgumentNullException.ThrowIfNull(reference);

            var raw = reference.Row is RowSelector.Single single
                ? inner.GetCell(single.RowKey, reference.ColumnSelector)
                : inner.GetCell(reference.ColumnSelector);

            return [FromObject(raw)];
        }

        private static ExpressionValue FromObject(object? raw)
            => raw switch
            {
                null => ExpressionValue.Null,
                decimal number => ExpressionValue.Number(number),
                int number => ExpressionValue.Number(number),
                bool flag => ExpressionValue.Boolean(flag),
                DateTime date => ExpressionValue.Date(date),
                _ => ExpressionValue.Text(raw.ToString() ?? string.Empty),
            };
    }
}

/// <summary>Контекст для правил рівня рядка і вище.</summary>
public interface IValidationContext
{
    /// <summary>Значення комірки поточного рядка.</summary>
    public object? GetCell(string columnCode);

    /// <summary>Значення комірки конкретного рядка таблиці.</summary>
    public object? GetCell(string rowKey, string columnCode);
}
