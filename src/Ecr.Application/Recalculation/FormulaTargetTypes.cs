using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Чи приймає колонка результат формули даного типу.
/// </summary>
/// <remarks>
/// ⛔ V-04 (UX-прохід 2026-09-24): формула рядка без явної колонки обчислює
/// «свій рядок у кожній колонці» (<see cref="RecalculationService"/>,
/// <c>Targets</c>), і до цього писала число в КОЖНУ колонку рядка — String,
/// Date, Bool, Lookup, Unit. Задум формули рядка — підсумковий рядок
/// (02b §3.1: <c>[Jan]</c> — колонка того самого рядка): результат має сенс
/// лише там, де тип колонки його приймає.
///
/// ⚠ Lookup, Unit і Calculated не приймають НІЧОГО: у Lookup лежить
/// ідентифікатор запису довідника, в Unit — одиниці, а Calculated у комірку
/// не пишеться взагалі (<see cref="CellDataType.Calculated"/>). Число з
/// формули в будь-якій із них — не значення, а сміття з виглядом значення.
///
/// ⚠ <see cref="CellDataType.Formula"/> приймає будь-який тип: це колонка,
/// призначена саме під обчислене значення, і що саме в ній лежить, визначає
/// формула, а не колонка.
/// </remarks>
public static class FormulaTargetTypes
{
    /// <summary>Чи можна покласти результат типу <paramref name="result"/> у колонку типу <paramref name="column"/>.</summary>
    /// <param name="column">Тип колонки; <c>null</c> — колонки немає в структурі.</param>
    /// <param name="result">Тип обчисленого значення.</param>
    public static bool Accepts(CellDataType? column, ExpressionValueType result)
        => column switch
        {
            CellDataType.Formula => true,
            CellDataType.Int or CellDataType.Decimal => result == ExpressionValueType.Number,
            CellDataType.String => result == ExpressionValueType.Text,
            CellDataType.Bool => result == ExpressionValueType.Boolean,
            CellDataType.Date => result == ExpressionValueType.Date,
            _ => false,
        };
}
