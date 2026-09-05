namespace Ecr.Adapters.Excel;

/// <summary>
/// Карта книги: що де лежить у вивантаженому <c>.xlsx</c>.
/// </summary>
/// <remarks>
/// ⚠ Пишеться в **прихований аркуш** книги при експорті і читається при
/// імпорті. Це те, що дозволяє зіставляти аркуші й колонки **за кодами, а не
/// за позиціями**: зсув однієї колонки у файлі інакше зіпсував би дані так,
/// що diff показав би зміну в кожній комірці й виглядав би правдоподібно.
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
public sealed record ExcelColumnRef(
    int ColumnDefId, string Code, int Number, bool IsCalculated, int? LookupRegistryDefId);

/// <summary>Рядок блоку.</summary>
/// <param name="RowKey">Ідентичність рядка в системі.</param>
/// <param name="Number">Номер рядка в книзі, з одиниці.</param>
public sealed record ExcelRowRef(string RowKey, int Number);
