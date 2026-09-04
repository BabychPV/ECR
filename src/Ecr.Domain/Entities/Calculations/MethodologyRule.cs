// src/Ecr.Domain/Entities/Calculations/MethodologyRule.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Правило прив'язки методології до рядків документа (ФВ-13.3, ФВ-13.8):
/// **умова**, а не жорсткий список <c>RowKey</c>.
/// </summary>
/// <remarks>
/// Правила впорядковані за <see cref="Priority"/>, **перший збіг виграє**
/// (ФВ-13.4). На тому самому механізмі будується матриця покриття: рядок, який
/// не зачепило жодне правило, видно до публікації, а не за розбіжністю в звіті.
/// </remarks>
public sealed class MethodologyRule : Entity<int>
{
    private MethodologyRule() { }

    public MethodologyRule(int methodologyVersionId, string conditionExpression, int priority)
    {
        MethodologyVersionId = methodologyVersionId;
        ConditionExpression = conditionExpression;
        Priority = priority;
        IsActive = true;
    }

    public int MethodologyVersionId { get; private set; }

    /// <summary>Умова діалекту методологій; посилається на реєстри й атрибути.</summary>
    public string ConditionExpression { get; private set; } = null!;

    /// <summary>Менше значення — вищий пріоритет. Перший збіг виграє.</summary>
    public int Priority { get; private set; }

    public bool IsActive { get; private set; }
}
