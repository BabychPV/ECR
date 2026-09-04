using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>Формула шаблону: вираз плюс область дії.</summary>
public sealed class FormulaDef : Entity<int>
{
    private FormulaDef() { }

    public FormulaDef(int tableDefId, FormulaScope scope, string expression, ExpressionDialect dialect)
    {
        TableDefId = tableDefId;
        Scope = scope;
        Expression = expression;
        Dialect = dialect;
    }

    public int TableDefId { get; private set; }
    public FormulaScope Scope { get; private set; }
    public int? ColumnDefId { get; private set; }
    public int? RowDefId { get; private set; }
    public ExpressionDialect Dialect { get; private set; }
    public string Expression { get; private set; } = null!;

    /// <summary>
    /// Топологічний порядок. Обчислюється **при публікації**, а не в рантаймі:
    /// сортувати граф на кожен запит — це витрата, якої бюджет не передбачає.
    /// </summary>
    public int EvaluationOrder { get; private set; }

    public bool IsCrossSheet { get; private set; }

    /// <summary>Знімок: значення матеріалізується один раз і не перераховується каскадом.</summary>
    public bool IsSnapshot { get; private set; }

    public bool IsDeleted { get; private set; }

    /// <summary>Фіксує обчислений порядок. Викликається лише під час <c>Publish</c>.</summary>
    public void SetEvaluationOrder(int order) => EvaluationOrder = order;
}
