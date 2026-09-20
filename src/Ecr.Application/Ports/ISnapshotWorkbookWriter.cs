// src/Ecr.Application/Ports/ISnapshotWorkbookWriter.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Складає книгу <c>.xlsx</c> зі зрізу звітності (<c>R7</c>, <c>D-52a</c>).
/// </summary>
/// <remarks>
/// ⚠ Окремий порт, а не метод <see cref="IExcelExporter"/>: той будує книгу
/// ДОКУМЕНТА — аркуші шаблону, стилі, формули, карту для зворотного імпорту.
/// Зріз не має нічого з цього: це один плаский аркуш, який ніколи не
/// повертається назад у систему. Спільного коду між ними рівно нуль, а спільний
/// інтерфейс змусив би кожну заглушку в тестах документа знати про звітність.
/// </remarks>
public interface ISnapshotWorkbookWriter
{
    /// <summary>Формує книгу; потік віддається читачу з початку.</summary>
    /// <param name="workbook">Колонки й рядки зрізу.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>
    /// Потік книги. ⚠ Викликач <b>зобов'язаний</b> його закрити: реалізація
    /// тримає вміст у тимчасовому ФАЙЛІ, і той зникає саме при закритті.
    /// </returns>
    public Task<Stream> WriteAsync(SnapshotWorkbook workbook, CancellationToken ct);
}

/// <summary>Зріз, готовий до запису в книгу.</summary>
/// <param name="SnapshotId">Зріз — з нього береться назва аркуша й файлу.</param>
/// <param name="Columns">Колонки в порядку опису версії; порядок значущий.</param>
/// <param name="Rows">Рядки за зростанням <c>RowNo</c>.</param>
public sealed record SnapshotWorkbook(
    long SnapshotId, IReadOnlyList<SnapshotColumn> Columns, IReadOnlyList<SnapshotRow> Rows);
