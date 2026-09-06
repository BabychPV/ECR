using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Що саме обчислює формула — одне визначення на всю систему.
/// </summary>
/// <remarks>
/// ⛔ Правило потрібне двічі й у різний час: при ПУБЛІКАЦІЇ, щоб побудувати
/// топологічний порядок, і в РАНТАЙМІ, щоб зрозуміти, яка формула читає
/// результат якої. Дві копії цього правила розійшлися б на першій же правці, і
/// розбіжність була б видима лише як «баланс порахувався раніше за суми, з
/// яких складається» — тобто як неправильне число, а не як помилка.
/// </remarks>
public static class FormulaOutputs
{
    /// <summary>Чи обчислює формула саме цю комірку.</summary>
    /// <param name="formula">Формула-кандидат.</param>
    /// <param name="table">Таблиця, якій вона належить.</param>
    /// <param name="rowKey">Рядок цілі; <c>null</c> — ціль не про рядок.</param>
    /// <param name="columnDefId">Колонка цілі.</param>
    /// <remarks>
    /// ⚠ Формула рівня КОЛОНКИ обчислює свою колонку в кожному рядку, тому
    /// рядок цілі для неї не має значення. Формула рівня РЯДКА — навпаки.
    /// Плутати ці два випадки означає або втратити ребро графа, або додати
    /// зайве: перше дає старі числа, друге — зайвий перерахунок усього.
    /// </remarks>
    public static bool Produces(FormulaDef formula, TableDef table, string? rowKey, int? columnDefId)
    {
        ArgumentNullException.ThrowIfNull(formula);
        ArgumentNullException.ThrowIfNull(table);

        if (formula.ColumnDefId is { } ownColumn
            && columnDefId != ownColumn
            && formula.Scope != FormulaScope.Row)
        {
            return false;
        }

        return formula.Scope switch
        {
            FormulaScope.Column => formula.ColumnDefId == columnDefId,
            FormulaScope.Row => RowKeyOf(table, formula) == rowKey,
            _ => formula.ColumnDefId == columnDefId && RowKeyOf(table, formula) == rowKey,
        };
    }

    /// <summary>Ключ рядка, якому належить формула; <c>null</c> — формула не рядкова.</summary>
    public static string? RowKeyOf(TableDef table, FormulaDef formula)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(formula);

        return formula.RowDefId is { } rowId
            ? table.Rows.FirstOrDefault(r => r.Id == rowId)?.RowKeyValue
            : null;
    }
}
