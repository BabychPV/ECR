using Ecr.Domain.Enums;

namespace Ecr.Bootstrap.Excel;

/// <summary>
/// Структура шаблону, вичитана з книги: те, що імпорт запише в чернетку
/// версії, якщо звіт не містить помилок (ФВ-2.10, ФВ-2.11).
/// </summary>
/// <param name="SourceFile">Ім'я файлу книги — лише для звіту.</param>
/// <param name="Sheets">Аркуші в порядку книги.</param>
public sealed record StructurePlan(string SourceFile, IReadOnlyList<PlannedSheet> Sheets)
{
    /// <summary>Усі таблиці плану в порядку аркушів.</summary>
    public IEnumerable<PlannedTable> Tables => Sheets.SelectMany(s => s.Tables);
}

/// <summary>Аркуш шаблону.</summary>
/// <param name="Code">Код аркуша (<c>EcrCode</c>).</param>
/// <param name="Name">Назва аркуша мовою книги.</param>
/// <param name="Ordinal">Позиція, з 1.</param>
/// <param name="Tables">Таблиці аркуша.</param>
public sealed record PlannedSheet(string Code, string Name, int Ordinal, IReadOnlyList<PlannedTable> Tables);

/// <summary>Таблиця на аркуші.</summary>
/// <param name="Code">Код таблиці, унікальний у межах аркуша.</param>
/// <param name="Name">Назва таблиці.</param>
/// <param name="Ordinal">Позиція на аркуші, з 1.</param>
/// <param name="Address">Діапазон книги, з якого вичитано таблицю (<c>Аркуш!B3:F20</c>).</param>
/// <param name="Origin">Звідки взялися межі таблиці.</param>
/// <param name="LayoutKind">Розкладка періодів.</param>
/// <param name="RowMode">Фіксовані рядки чи додає користувач.</param>
/// <param name="Columns">Колонки даних (без колонки підписів рядків).</param>
/// <param name="Rows">Рядки; порожньо для <see cref="TableRowMode.Dynamic"/>.</param>
public sealed record PlannedTable(
    string Code,
    string Name,
    int Ordinal,
    string Address,
    TableOrigin Origin,
    TableLayoutKind LayoutKind,
    TableRowMode RowMode,
    IReadOnlyList<PlannedColumn> Columns,
    IReadOnlyList<PlannedRow> Rows);

/// <summary>Колонка таблиці.</summary>
/// <param name="Code">Код колонки, унікальний у межах таблиці.</param>
/// <param name="Header">Заголовок без позначення одиниці.</param>
/// <param name="Ordinal">Позиція, з 1.</param>
/// <param name="DataType">Тип даних.</param>
/// <param name="Scale">Кількість знаків після коми з формату комірок; <c>null</c> — формат не задає.</param>
/// <param name="UnitCode">Код одиниці каталогу (<c>uom.Unit.Code</c>); <c>null</c> — без одиниці або не розпізнано.</param>
/// <param name="IsReadOnly">Комірки заблоковані на захищеному аркуші.</param>
/// <param name="Formula">Вираз діалекту шаблонів для колонки типу <see cref="CellDataType.Formula"/>.</param>
public sealed record PlannedColumn(
    string Code,
    string Header,
    int Ordinal,
    CellDataType DataType,
    byte? Scale,
    string? UnitCode,
    bool IsReadOnly,
    string? Formula);

/// <summary>Фіксований рядок таблиці.</summary>
/// <param name="Key">RowKey — стабільна ідентичність рядка.</param>
/// <param name="Label">Підпис рядка.</param>
/// <param name="Ordinal">Позиція, з 1.</param>
/// <param name="Kind">Роль рядка.</param>
/// <param name="ParentKey">Ключ батька за відступом у книзі; <c>null</c> — корінь.</param>
public sealed record PlannedRow(string Key, string Label, int Ordinal, RowKind Kind, string? ParentKey);

/// <summary>Звідки взято межі таблиці.</summary>
public enum TableOrigin
{
    /// <summary>Таблиця Excel (ListObject) — межі задав автор книги.</summary>
    ExcelTable,

    /// <summary>Іменований діапазон — межі задав автор книги.</summary>
    DefinedName,

    /// <summary>Блок непорожніх рядків аркуша — межі вгадано, потрібна звірка.</summary>
    UsedRangeBlock,
}
