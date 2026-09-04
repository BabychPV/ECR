using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;

namespace Ecr.Application.Templates;

/// <summary>
/// Типи посилань, виведені зі знімка структури версії.
/// </summary>
/// <remarks>
/// ⚠ Без цього класу перевірка №3 з <c>02b</c> §12 («типи сумісні в кожній
/// операції») була реалізована, але **не викликалася**: обробник публікації
/// передавав у <c>PublishChecks</c> порожній контекст, і перевірка мовчки
/// нічого не робила (<c>Q-072</c>). Найгірший вид заглушки — той, що виглядає
/// робочим кодом.
/// </remarks>
public sealed class SnapshotTypeContext(TemplateVersionSnapshot snapshot) : ITypeContext
{
    /// <inheritdoc />
    public ExpressionValueType GetReferenceType(CellReferenceNode reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        // Плейсхолдер {Month} стоїть замість місячної колонки — усі вони
        // числові за побудовою (`ФВ-2.4`).
        if (reference.ColumnSelector is "{Month}" or "{Period}")
        {
            return ExpressionValueType.Number;
        }

        var column = snapshot.Sheets
            .SelectMany(s => s.Tables)
            .Where(t => reference.TableCode is null
                        || string.Equals(t.Code, reference.TableCode, StringComparison.OrdinalIgnoreCase))
            .SelectMany(t => t.Columns)
            .FirstOrDefault(c => !c.IsDeleted
                                 && string.Equals(c.Code, reference.ColumnSelector, StringComparison.OrdinalIgnoreCase));

        // Нерезолвлене посилання — не помилка ТИПУ: про нього вже сказав
        // резолвер, і другий раз казати те саме означало б подвоїти список.
        return column is null ? ExpressionValueType.Null : Map(column.DataType);
    }

    /// <inheritdoc />
    public ExpressionValueType GetColumnType(int tableDefId, int columnDefId)
        => snapshot.ColumnsById.TryGetValue(columnDefId, out var column)
            ? Map(column.DataType)
            : ExpressionValueType.Null;

    /// <inheritdoc />
    /// <remarks>Аргументів методології у версії шаблону немає за побудовою.</remarks>
    public ExpressionValueType GetArgumentType(string name) => ExpressionValueType.Null;

    private static ExpressionValueType Map(CellDataType type)
        => type switch
        {
            CellDataType.Int or CellDataType.Decimal => ExpressionValueType.Number,
            CellDataType.Bool => ExpressionValueType.Boolean,
            CellDataType.Date => ExpressionValueType.Date,

            // ⚠ Lookup і Unit — ТЕКСТ, і це не спрощення. Обидва зберігають
            // посилання, а не число; текстовий тип змушує арифметику з ними
            // впасти (02b §5), лишаючи законним порівняння з кодом у предикаті.
            CellDataType.Lookup or CellDataType.Unit or CellDataType.String => ExpressionValueType.Text,

            // Formula і Calculated — результат обчислення; його тип виводиться
            // з самого виразу, а не з оголошення колонки.
            _ => ExpressionValueType.Null,
        };
}
