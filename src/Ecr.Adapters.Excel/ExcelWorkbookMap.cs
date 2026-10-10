namespace Ecr.Adapters.Excel;

/// <summary>
/// Карта книги: що де лежить у вивантаженому <c>.xlsx</c>.
/// </summary>
/// <remarks>
/// ⚠ Пишеться в **прихований аркуш** книги при експорті і читається при
/// імпорті. Карта називає таблиці, колонки й рядки КОДАМИ, але місце кожного
/// з них у книзі — ПОЗИЦІЄЮ на момент експорту: Excel про карту не знає й
/// нічого в ній не оновлює. ⛔ Y5-01 (аудит 7): тому сортування, вставка чи
/// видалення рядка або колонки ловиться відбитками підписів рядків і
/// заголовків колонок (<see cref="LayoutFingerprint"/>) — розбіжність стає
/// відмовою таблиці, а не правдоподібним diff зі зсунутими значеннями.
/// <para>
/// ⚠ Карта несе <see cref="PeriodKey"/>, бо порт імпорту його не приймає:
/// <c>PreviewAsync(documentId, file, ct)</c>. Період, узятий «поточний»,
/// клав би дані минулого місяця в поточний рівно тоді, коли звіт здають, —
/// у перші дні наступного періоду.
/// </para>
/// </remarks>
/// <param name="DocumentId">Документ, з якого вивантажено.</param>
/// <param name="PeriodKey">Період вивантаження.</param>
/// <param name="TemplateVersionId">Версія шаблону, за якою будувалася книга.</param>
/// <param name="Tables">Блоки таблиць у книзі.</param>
public sealed record ExcelWorkbookMap(
    long DocumentId,
    int PeriodKey,
    int TemplateVersionId,
    IReadOnlyList<ExcelTableBlock> Tables)
{
    /// <summary>Ім'я прихованого аркуша з картою.</summary>
    /// <remarks>
    /// Починається з підкреслення, щоб не збігтися з жодним кодом аркуша
    /// шаблону: аркуш карти, перезаписаний аркушем даних, зробив би книгу
    /// неімпортовною без жодного повідомлення.
    /// </remarks>
    public const string SheetName = "_ecr";

    /// <summary>
    /// Скільки символів карти лягає в одну комірку.
    /// </summary>
    /// <remarks>
    /// ⛔ Excel не приймає в комірку більше за 32 767 символів, і карта
    /// реального документа цю межу переходить: дев'яносто таблиць по сорок
    /// колонок дають сотні кілобайт JSON. До `A7-29` карта писалася ОДНИМ
    /// значенням, тому експорт будь-якого несинтетичного документа падав —
    /// уже після того, як книга була побудована.
    ///
    /// ⚠ Межа взята з запасом: у комірку йде UTF-16, і рівно 32 767 —
    /// це стеля, а не робоче значення.
    /// </remarks>
    public const int ChunkSize = 30_000;
}

/// <summary>Блок однієї таблиці в книзі.</summary>
/// <param name="TableInstanceId">Екземпляр таблиці — саме в нього повертається імпорт.</param>
/// <param name="TableDefId">Опис таблиці.</param>
/// <param name="TableCode">Код таблиці; ним адресують формули.</param>
/// <param name="SheetName">Аркуш книги, на якому лежить блок.</param>
/// <param name="HeaderRow">Номер рядка заголовків.</param>
/// <param name="Columns">Колонки блоку.</param>
/// <param name="Rows">Рядки блоку.</param>
public sealed record ExcelTableBlock(
    long TableInstanceId,
    int TableDefId,
    string TableCode,
    string SheetName,
    int HeaderRow,
    IReadOnlyList<ExcelColumnRef> Columns,
    IReadOnlyList<ExcelRowRef> Rows);

/// <summary>Колонка блоку.</summary>
/// <param name="ColumnDefId">Опис колонки.</param>
/// <param name="Code">Код колонки.</param>
/// <param name="Number">Номер стовпця в книзі, з одиниці.</param>
/// <param name="IsCalculated">Комірки колонки рахує система — правки відхиляються (<c>ECR-CELL-4221</c>).</param>
/// <param name="LookupRegistryDefId">Довідник підстановки; <c>null</c> — не підстановка.</param>
/// <param name="Header">
/// ⛔ Y5-01. Відбиток заголовка колонки на момент експорту (<see cref="LayoutFingerprint"/>):
/// вставлена чи видалена в Excel колонка зсуває заголовки, і імпорт відмовляє таблицю,
/// а не читає значення сусідньої колонки. <c>null</c> — книгу вивантажено до появи поля.
/// </param>
public sealed record ExcelColumnRef(
    int ColumnDefId,
    string Code,
    int Number,
    bool IsCalculated,
    int? LookupRegistryDefId,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Header = null);

/// <summary>Рядок блоку.</summary>
/// <param name="RowKey">Ідентичність рядка в системі.</param>
/// <param name="Number">Номер рядка в книзі, з одиниці.</param>
/// <param name="Calc">
/// Відбитки обчислюваних комірок рядка на момент експорту
/// (<see cref="CalculatedCellFingerprint"/>); <c>null</c> — у блоці немає
/// обчислюваних колонок або книгу вивантажено до появи поля.
/// </param>
/// <param name="Version">
/// ⛔ D1-02 (HU-13 Q1, варіант A). Версія рядка (<c>rowversion</c> у Base64,
/// як у <c>IRowStore.GetRowVersionsBatchAsync</c>) на момент ЕКСПОРТУ.
/// Перегляд імпорту звіряє її з поточною: рядок, змінений після експорту
/// кимось іншим, не перезаписується значенням зі старої книги мовчки, а
/// стає відмовою-конфліктом (<see cref="ImportMessageKeys.RowChangedSinceExport"/>).
/// <c>null</c> — книгу вивантажено до появи поля (або рядок без версії):
/// тоді поведінка колишня, «книга проти поточного».
/// </param>
/// <param name="Cells">
/// ⛔ AN-118 (R1-01, HU-14 Q2). Відбитки ВВЕДЕНИХ комірок рядка на момент
/// експорту (<see cref="EnteredCellFingerprint"/>). У рядку з конфліктом
/// версії перезаписати можна лише комірку, яку людина в книзі змінила; та, що
/// збігається з відбитком, — не її правка, і чуже новіше значення в ній
/// лишається. <c>null</c> — у блоці немає введених колонок або книгу
/// вивантажено до появи поля: тоді конфліктом стає кожна комірка «книга ≠
/// поточне», а перегляд попереджає (<see cref="ImportMessageKeys.OutdatedWorkbook"/>).
/// </param>
/// <param name="Label">
/// ⛔ Y5-01. Відбиток підпису рядка (стовпець праворуч від колонок даних) на момент
/// експорту (<see cref="LayoutFingerprint"/>): відсортований, вставлений чи видалений у
/// Excel рядок зсуває підписи, і імпорт відмовляє таблицю, а не кладе значення в чужий
/// рядок. <c>null</c> — книгу вивантажено до появи поля.
/// </param>
public sealed record ExcelRowRef(
    string RowKey,
    int Number,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Calc = null,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Version = null,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Cells = null,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Label = null);
