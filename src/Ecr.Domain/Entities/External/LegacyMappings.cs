// src/Ecr.Domain/Entities/External/LegacyMappings.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.External;

/// <summary>
/// Відповідність аркуша ECR аркушу чинної книги (<c>ext.LegacySheetMapping</c>).
/// </summary>
/// <remarks>
/// ⚠ Уся специфіка Excel і PI AF живе тільки в схемі <c>ext</c> — ядро про неї
/// не знає, і це перевіряється архітектурним тестом (ФВ-11.9). Мапінг
/// потрібен рівно двом речам: імпорту з чинної книги і **валідації міграції**
/// (ІНТ-11), тобто доказу, що перенесені числа збігаються з вихідними.
/// </remarks>
public sealed class LegacySheetMapping : Entity<int>
{
    private LegacySheetMapping() { }

    /// <summary>Створює відповідність аркуша.</summary>
    /// <param name="sheetDefId">Аркуш ECR.</param>
    /// <param name="legacyName">Ім'я аркуша в книзі: <c>'7. Water Report'</c>.</param>
    public LegacySheetMapping(int sheetDefId, string legacyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyName);

        SheetDefId = sheetDefId;
        LegacyName = legacyName;
    }

    public int SheetDefId { get; private set; }
    public string LegacyName { get; private set; } = null!;
}

/// <summary>
/// Відповідність таблиці ECR структурам AF і Excel (<c>ext.LegacyTableMapping</c>).
/// </summary>
public sealed class LegacyTableMapping : Entity<int>
{
    private LegacyTableMapping() { }

    /// <summary>Створює відповідність таблиці.</summary>
    /// <param name="tableDefId">Таблиця ECR.</param>
    public LegacyTableMapping(int tableDefId) => TableDefId = tableDefId;

    public int TableDefId { get; private set; }

    /// <summary>Шаблон event frame у PI AF.</summary>
    public string? EventFrameTemplate { get; private set; }

    /// <summary>Шаблон елемента в PI AF.</summary>
    public string? ElementTemplate { get; private set; }

    /// <summary>Ім'я конкретного елемента.</summary>
    public string? ElementName { get; private set; }

    /// <summary>Рядок шаблону в книзі.</summary>
    public int? TemplateRow { get; private set; }

    /// <summary>Базовий зсув рядків — звідси починається таблиця на аркуші.</summary>
    public int? RowOffsetBase { get; private set; }

    /// <summary>Ставить відповідність структурам PI AF.</summary>
    /// <param name="eventFrameTemplate">Шаблон event frame.</param>
    /// <param name="elementTemplate">Шаблон елемента.</param>
    /// <param name="elementName">Ім'я елемента.</param>
    public void MapToAf(string? eventFrameTemplate, string? elementTemplate, string? elementName)
    {
        EventFrameTemplate = eventFrameTemplate;
        ElementTemplate = elementTemplate;
        ElementName = elementName;
    }

    /// <summary>Ставить відповідність розкладці аркуша.</summary>
    /// <param name="templateRow">Рядок шаблону.</param>
    /// <param name="rowOffsetBase">Базовий зсув рядків.</param>
    public void MapToExcel(int? templateRow, int? rowOffsetBase)
    {
        TemplateRow = templateRow;
        RowOffsetBase = rowOffsetBase;
    }
}

/// <summary>
/// Відповідність рядка ECR атрибуту AF і рядку книги (<c>ext.LegacyRowMapping</c>).
/// </summary>
public sealed class LegacyRowMapping : Entity<int>
{
    private LegacyRowMapping() { }

    /// <summary>Створює відповідність рядка.</summary>
    /// <param name="rowDefId">Рядок ECR.</param>
    public LegacyRowMapping(int rowDefId) => RowDefId = rowDefId;

    public int RowDefId { get; private set; }

    /// <summary>Ім'я атрибута AF: <c>Attribute_0010</c>.</summary>
    /// <remarks>
    /// Саме такі імена ECR і виводить з обігу: атрибут, чий тип відомий лише
    /// за домовленістю, неможливо ні перевірити, ні пояснити (ФВ-8.10).
    /// </remarks>
    public string? AfAttributeName { get; private set; }

    /// <summary>Номер рядка на аркуші книги.</summary>
    public int? ExcelRow { get; private set; }

    /// <summary>Ставить відповідність.</summary>
    /// <param name="afAttributeName">Ім'я атрибута AF.</param>
    /// <param name="excelRow">Номер рядка книги.</param>
    public void Map(string? afAttributeName, int? excelRow)
    {
        AfAttributeName = afAttributeName;
        ExcelRow = excelRow;
    }
}

/// <summary>
/// Відповідність колонки ECR полю legacy-рядка (<c>ext.LegacyColumnMapping</c>).
/// </summary>
/// <remarks>
/// ⚠ <see cref="LegacyFieldIndex"/> — позиція поля в <c>;</c>-рядку
/// <c>Attribute_XXXX</c>. Порядок **індивідуальний для кожної з ~90 таблиць**,
/// і саме тому він дані, а не константа в коді: без нього неможливо звірити
/// перенесені числа з вихідними (ІНТ-11).
/// </remarks>
public sealed class LegacyColumnMapping : Entity<int>
{
    private LegacyColumnMapping() { }

    /// <summary>Створює відповідність колонки.</summary>
    /// <param name="columnDefId">Колонка ECR.</param>
    public LegacyColumnMapping(int columnDefId) => ColumnDefId = columnDefId;

    public int ColumnDefId { get; private set; }

    /// <summary>Позиція поля в <c>;</c>-рядку; індивідуальна для кожної таблиці.</summary>
    public int? LegacyFieldIndex { get; private set; }

    /// <summary>Літера колонки в книзі: <c>J</c>, <c>AB</c>.</summary>
    public string? LegacyExcelColumn { get; private set; }

    /// <summary>Ім'я атрибута AF.</summary>
    public string? AfAttributeName { get; private set; }

    /// <summary>Ставить відповідність.</summary>
    /// <param name="legacyFieldIndex">Позиція в <c>;</c>-рядку.</param>
    /// <param name="legacyExcelColumn">Літера колонки книги.</param>
    /// <param name="afAttributeName">Ім'я атрибута AF.</param>
    public void Map(int? legacyFieldIndex, string? legacyExcelColumn, string? afAttributeName)
    {
        LegacyFieldIndex = legacyFieldIndex;
        LegacyExcelColumn = legacyExcelColumn;
        AfAttributeName = afAttributeName;
    }
}
