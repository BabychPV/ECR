using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Validation;

/// <summary>
/// Виконує правила валідації. Рівні розрізняються не за суворістю тексту, а
/// за тим, що вони блокують (R-B3): комірковий <c>Error</c> блокує запис,
/// решта — лише подання.
/// </summary>
public sealed class ValidationEngine(IFormulaEngine formulaEngine)
{
    /// <summary>Перевіряє одну комірку — виконується синхронно на шляху запису.</summary>
    public IReadOnlyList<ValidationMessage> ValidateCell(
        ColumnDef column, Domain.ValueObjects.CellValueData value, IReadOnlyList<ValidationRule> rules)
        => throw new NotImplementedException(
            "TODO: спершу структурна перевірка column.ValidateValue (тип, обов'язковість, довідник, " +
            "одиниця), потім правила Scope = 0. Повертати ВСІ порушення, не перше.");

    /// <summary>Перевіряє рядок, таблицю або документ — не блокує запис.</summary>
    public IReadOnlyList<ValidationMessage> ValidateScope(
        byte scope, IReadOnlyList<ValidationRule> rules, IValidationContext context)
        => throw new NotImplementedException(
            "TODO: обчислити вираз кожного правила через formulaEngine; " +
            "FALSE → повідомлення відповідного рівня. Помилка обчислення виразу — " +
            "це Warning про несправне правило, а не Error даних: інакше зламане правило " +
            "заблокує роботу з коректними даними.");
}

/// <summary>Контекст для правил рівня рядка і вище.</summary>
public interface IValidationContext
{
    /// <summary>Значення комірки поточного рядка.</summary>
    object? GetCell(string columnCode);

    /// <summary>Значення комірки конкретного рядка таблиці.</summary>
    object? GetCell(string rowKey, string columnCode);
}
