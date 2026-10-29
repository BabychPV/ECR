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

        // ⚠ Ключ рядка формули потрібен лише не-колонковим областям; колонкова його не читає.
        return Produces(
            formula,
            formula.Scope == FormulaScope.Column ? null : RowKeyOf(table, formula),
            rowKey,
            columnDefId);
    }

    /// <summary>
    /// Те саме правило, але з УЖЕ відомим ключем рядка формули (<see cref="RowKeyOf"/>).
    /// </summary>
    /// <param name="formula">Формула-кандидат.</param>
    /// <param name="formulaRowKey">
    /// Ключ рядка самої формули (<see cref="RowKeyOf"/>); для колонкової області не читається.
    /// </param>
    /// <param name="rowKey">Рядок цілі; <c>null</c> — ціль не про рядок.</param>
    /// <param name="columnDefId">Колонка цілі.</param>
    /// <remarks>
    /// ⛔ Єдине визначення правила: версія з таблицею лише обчислює <paramref name="formulaRowKey"/> і йде сюди.
    /// Існує, бо <see cref="RowKeyOf"/> — лінійний пошук по рядках таблиці, і індекс формул версії
    /// (<c>RecalculationPlanBuilder</c>) рахує його ОДИН раз на формулу, а не на кожну пару «залежність × формула».
    /// </remarks>
    public static bool Produces(FormulaDef formula, string? formulaRowKey, string? rowKey, int? columnDefId)
    {
        ArgumentNullException.ThrowIfNull(formula);

        // ⚠ Row-скоуп із ЯВНОЮ колонкою обчислює ОДНУ комірку, а не «весь
        // рядок» (аудит 2026-09-16, §1.3). До фіксу тут стояло виключення
        // `&& formula.Scope != FormulaScope.Row`, тобто `ColumnDefId` для
        // Row-скоупу просто ігнорувався: формула звітувала, що виробляє кожну
        // колонку свого рядка, і граф отримував ребра до формул, які з неї
        // нічого не читають. Односторонній надлишок — зайвий перерахунок, а не
        // неправильне число, але й не безкоштовний.
        if (formula.ColumnDefId is { } ownColumn && columnDefId != ownColumn)
        {
            return false;
        }

        return formula.Scope switch
        {
            FormulaScope.Column => formula.ColumnDefId == columnDefId,
            FormulaScope.Row => formulaRowKey == rowKey,
            _ => formula.ColumnDefId == columnDefId && formulaRowKey == rowKey,
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
