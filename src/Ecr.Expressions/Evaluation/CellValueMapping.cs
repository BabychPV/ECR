using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Переклад між значенням виразу і збереженим значенням комірки.
/// </summary>
/// <remarks>
/// ⚠ Найважливіше правило тут — доля ПОМИЛКИ. Вона зберігається як
/// <c>IsEmpty = 0</c>, <c>ValueString = '#DIV/0'</c>, <c>IsCalculated = 1</c>
/// (02b §6.4), а не як порожня комірка. Різниця не косметична: порожня комірка
/// в звіті виглядає як «ще не заповнили», і зіпсоване число знайшли б аж на
/// звірці. Видимий <c>#DIV/0</c> знаходять одразу.
/// </remarks>
public static class CellValueMapping
{
    /// <summary>Значення виразу як значення комірки.</summary>
    public static CellValueData ToCellValue(ExpressionValue value)
        => value.Type switch
        {
            ExpressionValueType.Error => new CellValueData
            {
                ValueString = value.ErrorCode,
                IsCalculated = true,
            },
            ExpressionValueType.Number => new CellValueData
            {
                ValueNumeric = (decimal)value.Value!,
                IsCalculated = true,
            },
            ExpressionValueType.Text => new CellValueData
            {
                ValueString = (string)value.Value!,
                IsCalculated = true,
            },
            ExpressionValueType.Boolean => new CellValueData
            {
                ValueBool = (bool)value.Value!,
                IsCalculated = true,
            },
            ExpressionValueType.Date => new CellValueData
            {
                ValueDate = (DateTime)value.Value!,
                IsCalculated = true,
            },

            // Обчислена порожнеча — саме ЯВНА порожнеча: формула відпрацювала
            // і дала «нічого». Відсутність рядка означала б «ще не рахували».
            _ => new CellValueData { IsEmpty = true, IsCalculated = true },
        };

    /// <summary>
    /// Збережене значення комірки як «сире» значення для мови правил і для
    /// клієнта — одне розгортання на всі шляхи.
    /// </summary>
    /// <param name="cell">Значення комірки; <c>null</c> — рядка немає.</param>
    /// <remarks>
    /// ⛔ Існує, щоб розгортання було ОДНЕ (аудит 2026-09-16, §3.2). До цього
    /// їх було два: <c>GetTableSliceHandler.Unwrap</c> обробляв усі шість полів,
    /// а <c>TableValidation.SliceContext</c> — ad-hoc
    /// <c>ValueNumeric ?? ValueString</c>, тобто правила рівня рядка/таблиці/
    /// документа бачили <c>Null</c> для КОЖНОЇ Bool- і Date-колонки. Наслідок
    /// двобічний і однаково поганий: правило
    /// <c>"[IncludeInReport] = false OR [Volume] &gt; 0"</c> або хибно ламалось
    /// (Error вироджувався у Warning, маскуючи те, на що розраховує подання —
    /// надто дозволяюче), або хибно спрацьовувало на коректних даних (блокуючи
    /// легітимну подачу). Той самий клас значень для скоупу 0 (комірка) уже
    /// коректно йшов через <see cref="ToExpressionValue"/> — одна мова правил
    /// мала два шляхи, і один із них ламав Bool/Date.
    ///
    /// ⚠ Порядок перевірок не має значення: заповнене поле рівно одне (R-B4).
    /// </remarks>
    public static object? ToRuleValue(CellValueData? cell)
    {
        if (cell is null || cell.IsEmpty)
        {
            return null;
        }

        if (cell.ValueNumeric is { } number)
        {
            return number;
        }

        if (cell.ValueDate is { } date)
        {
            return date;
        }

        if (cell.ValueBool is { } flag)
        {
            return flag;
        }

        if (cell.ValueString is { } text)
        {
            return text;
        }

        if (cell.ValueRegistryEntryId is { } entry)
        {
            return entry;
        }

        return cell.ValueUnitId;
    }

    /// <summary>Збережене значення комірки як значення виразу.</summary>
    /// <param name="cell">Значення комірки; <c>null</c> — рядка немає.</param>
    /// <param name="defaultValue">
    /// <c>DefaultValue</c> колонки; застосовується ЛИШЕ коли комірки немає
    /// зовсім. Явна порожнеча його не бере (02b §6.3).
    /// </param>
    public static ExpressionValue ToExpressionValue(CellValueData? cell, ExpressionValue defaultValue)
    {
        if (cell is null)
        {
            return defaultValue;
        }

        if (cell.IsEmpty)
        {
            return ExpressionValue.Null;
        }

        if (cell.ValueNumeric is { } number)
        {
            return ExpressionValue.Number(number);
        }

        if (cell.ValueDate is { } date)
        {
            return ExpressionValue.Date(date);
        }

        if (cell.ValueBool is { } flag)
        {
            return ExpressionValue.Boolean(flag);
        }

        if (cell.ValueString is { } text)
        {
            // Помилка, збережена як текст, повертається помилкою — інакше
            // #DIV/0 брав би участь у наступному обчисленні як рядок.
            return text.StartsWith('#')
                ? ExpressionValue.Error(text)
                : ExpressionValue.Text(text);
        }

        // ⚠ Id запису довідника — ЧИСЛО, а не окремий тип значення виразу:
        // мова не заводить п'ятого скалярного типу заради одного стовпця
        // Lookup-колонки. Це навмисно те саме число, яке приймає перший
        // аргумент REGFIELD — «той самий механізм отримання значення
        // комірки», яким комірки взагалі передаються у формулах, без нового.
        //
        // ⛔ До цього поле мовчки НЕ розгорталося тут узагалі: Lookup-комірка
        // в будь-якому виразі (зокрема й предикаті `[WHERE [Code] = 'W-01']`)
        // давала Null. Порівняння з рядковим кодом лишається хибним і зараз —
        // Number проти Text ніколи не рівні (`Evaluator.AreEqual`), — тобто
        // це не регресія: предикат так само не бачив жодного рядка й до цієї
        // правки. Полагодити саме це — окрема задача, не ця.
        if (cell.ValueRegistryEntryId is { } entryId)
        {
            return ExpressionValue.Number(entryId);
        }

        return ExpressionValue.Null;
    }
}
