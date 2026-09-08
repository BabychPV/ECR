using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Validation;

/// <summary>
/// Прогін правил рівнів рядка, таблиці й документа над зрізом однієї таблиці.
/// </summary>
/// <remarks>
/// ⛔ Спільний код, а не копія в двох обробниках, і причина не в економії
/// рядків: `ФВ-5.4` вимагає, щоб ці самі рівні блокували `Submit`, — тобто
/// подання зобов'язане рахувати РІВНО те саме, що показує кнопка
/// «Перевірити». Дві реалізації того самого правила розійшлися б, і
/// розійшлися б мовчки: документ, який щойно показав «помилок немає», не
/// подавався б (директива №09 `W8` п.5, `S-28`).
/// </remarks>
internal static class TableValidation
{
    /// <summary>Рівні правил: 2 таблиця, 3 документ. Рівень рядка йде окремо.</summary>
    private static readonly byte[] AboveRowLevels = [2, 3];

    /// <summary>
    /// Виконує всі правила таблиці і повертає повідомлення з адресами.
    /// </summary>
    /// <param name="engine">Двигун правил.</param>
    /// <param name="table">Опис таблиці зі знімка версії.</param>
    /// <param name="cells">Значення зрізу.</param>
    /// <param name="rowIds">Рядки екземпляра: <c>RowKey</c> → <c>TableRow.Id</c>.</param>
    /// <remarks>
    /// ⛔ Рівень РЯДКА виконується ПО РЯДКАХ, а кожне повідомлення отримує
    /// свій <c>RowKey</c> (директива №09 `W8` п.3, `S-19`). Доти всі три рівні
    /// йшли одним проходом через <see cref="ValidationEngine.ValidateScope"/>,
    /// який ставить <c>rowKey: null</c> завжди: список порушень приходив без
    /// жодної адреси — тобто повідомляв, ЩО не так, і не повідомляв, ДЕ.
    /// Виправити за таким списком не можна нічого.
    ///
    /// ⚠ Рівень таблиці й документа адреси рядка не мають за визначенням і
    /// лишаються з <c>null</c> — це не пропуск, а їхня природа.
    /// </remarks>
    public static IReadOnlyList<ValidationMessage> Run(
        ValidationEngine engine,
        TableDef table,
        IReadOnlyList<CellRecord> cells,
        IReadOnlyDictionary<string, long> rowIds)
    {
        var messages = new List<ValidationMessage>();

        if (table.ValidationRules.Count == 0)
        {
            return messages;
        }

        var slice = new SliceContext(table, cells, rowIds);

        foreach (var rowKey in rowIds.Keys.Order(StringComparer.Ordinal))
        {
            messages.AddRange(engine
                .ValidateScope(scope: 1, table.ValidationRules, slice.ForRow(rowKey))
                .Select(m => m with { RowKey = rowKey }));
        }

        // Правило рівня таблиці бачить усі рядки, і запускати його на кожному
        // означало б повторити те саме порушення N разів.
        foreach (var scope in AboveRowLevels)
        {
            messages.AddRange(engine.ValidateScope(scope, table.ValidationRules, slice));
        }

        return messages;
    }

    /// <summary>Значення зрізу як джерело для виразів правил.</summary>
    /// <remarks>
    /// ⛔ Рядок шукається серед РЯДКІВ ЕКЗЕМПЛЯРА (<c>doc.TableRow</c>), а не
    /// серед описів <c>cfg.RowDef</c>. Раніше тут стояло
    /// <c>table.Rows.FirstOrDefault(...).Id</c> — тобто <c>RowDef.Id</c>, — і
    /// ним індексувався словник, ключований <c>TableRow.Id</c>. Це різні
    /// послідовності: правило рівня рядка читало комірку ЧУЖОГО рядка, а
    /// частіше не знаходило нічого і мовчки бачило <c>null</c>.
    /// </remarks>
    private sealed class SliceContext(
        TableDef table,
        IReadOnlyList<CellRecord> cells,
        IReadOnlyDictionary<string, long> rowIds) : IValidationContext
    {
        private readonly Dictionary<(long Row, int Column), object?> _values =
            cells.ToDictionary(
                c => (c.Address.TableRowId, c.Address.ColumnDefId),
                c => (object?)(c.Value.ValueNumeric ?? (object?)c.Value.ValueString));

        private string? _currentRowKey;

        /// <summary>Той самий зріз, наведений на конкретний рядок.</summary>
        public SliceContext ForRow(string rowKey)
            => new SliceContext(table, cells, rowIds) { _currentRowKey = rowKey };

        /// <inheritdoc />
        public object? GetCell(string columnCode)
        {
            // Наведений на рядок контекст читає СВІЙ рядок. Без цього правило
            // рівня рядка бачило б перше-ліпше значення колонки в таблиці.
            if (_currentRowKey is { } current)
            {
                return GetCell(current, columnCode);
            }

            // Рівень таблиці й документа «поточного рядка» не має, тому
            // однойменний метод віддає перше значення колонки: правило,
            // написане без рядка, і має на увазі саме таблицю цілком.
            var column = table.Columns.FirstOrDefault(c => c.Code == columnCode);
            return column is null
                ? null
                : _values.FirstOrDefault(v => v.Key.Column == column.Id).Value;
        }

        /// <inheritdoc />
        public object? GetCell(string rowKey, string columnCode)
        {
            var column = table.Columns.FirstOrDefault(c => c.Code == columnCode);

            return column is null || !rowIds.TryGetValue(rowKey, out var rowId)
                ? null
                : _values.GetValueOrDefault((rowId, column.Id));
        }
    }
}
