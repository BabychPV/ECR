// src/Ecr.Domain/Entities/Calculations/CalculationResult.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Результат розрахунку. **Не потрапляє в `doc.CellValue`** (ФВ-9.12, D-69):
/// у документ він приходить посиланням через <c>cfg.CalculationBinding</c>.
/// </summary>
/// <remarks>
/// Тип — <c>decimal(28,10)</c>, ніколи <c>float</c> (ФВ-9.11): на мільйонах
/// рядків подвійна точність дає розбіжність, яку неможливо пояснити методологу.
/// </remarks>
public sealed class CalculationResult : Entity<long>
{
    private CalculationResult() { }

    public CalculationResult(long runId, int methodologyVersionId, int periodKey, string outputCode, decimal value, int unitId)
    {
        CalculationRunId = runId;
        MethodologyVersionId = methodologyVersionId;
        PeriodKey = periodKey;
        OutputCode = outputCode;
        Value = value;
        UnitId = unitId;
    }

    public long CalculationRunId { get; private set; }

    /// <summary>Версія, що дала число. Без неї результат неможливо пояснити.</summary>
    public int MethodologyVersionId { get; private set; }

    public int PeriodKey { get; private set; }
    public string OutputCode { get; private set; } = null!;
    public decimal Value { get; private set; }
    public int UnitId { get; private set; }
    public long? SubstanceEntryId { get; private set; }
    public long? SourceRowId { get; private set; }

    /// <summary>Чи це актуальний прогін. Перемикається однією транзакцією.</summary>
    public bool IsCurrent { get; private set; }
}
