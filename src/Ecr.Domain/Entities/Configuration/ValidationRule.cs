using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>Правило валідації. Рівень визначає, чи блокує воно запис (R-B3).</summary>
public sealed class ValidationRule : Entity<int>
{
    private ValidationRule() { }

    public ValidationRule(int tableDefId, EcrCode code, ValidationSeverity severity, byte scope,
                          string expression, LocalizedText message)
    {
        TableDefId = tableDefId;
        Code = code.Value;
        Severity = severity;
        Scope = scope;
        Expression = expression;
        MessageL10n = message;
        IsActive = true;
    }

    public int TableDefId { get; private set; }
    public string Code { get; private set; } = null!;
    public ValidationSeverity Severity { get; private set; }

    /// <summary>0 Cell, 1 Row, 2 Table, 3 Document.</summary>
    public byte Scope { get; private set; }

    public int? ColumnDefId { get; private set; }
    public string Expression { get; private set; } = null!;
    public LocalizedText MessageL10n { get; private set; } = null!;
    public bool IsActive { get; private set; }

    /// <summary>
    /// Чи блокує правило збереження. Блокує **лише** комірковий <c>Error</c>:
    /// заборона зберегти проміжний стан робить роботу з великою таблицею
    /// неможливою (R-B3).
    /// </summary>
    public bool BlocksSave => Severity == ValidationSeverity.Error && Scope == 0;
}
