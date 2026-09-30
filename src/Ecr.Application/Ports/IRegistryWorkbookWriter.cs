// src/Ecr.Application/Ports/IRegistryWorkbookWriter.cs
using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Книга експорту записів довідника (RT-16, FEATURE-REGISTRY-TABLES §7.1): плаский аркуш на довідник —
/// один, або батько й частини композиції (<c>includeChildren</c>, ФВ-8.16).
/// </summary>
/// <remarks>
/// ⚠ Окремий порт, а не <see cref="ISnapshotWorkbookWriter"/>: там колонки й підсумки звіту, тут —
/// типи полів довідника і правило «число без втрати знаків».
/// </remarks>
public interface IRegistryWorkbookWriter
{
    /// <summary>Формує книгу; потік віддається читачу з початку.</summary>
    /// <param name="sheets">Аркуші в порядку показу (≥ 1); однакові після скорочення назви розводяться суфіксом.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>
    /// Потік книги. ⚠ Викликач <b>зобов'язаний</b> його закрити: вміст лежить у тимчасовому ФАЙЛІ,
    /// і той зникає саме при закритті.
    /// </returns>
    public Task<Stream> WriteAsync(IReadOnlyList<RegistryWorkbook> sheets, CancellationToken ct);
}

/// <summary>Записи довідника, готові до запису в книгу, — один аркуш.</summary>
/// <param name="SheetName">Назва аркуша — код довідника.</param>
/// <param name="Columns">Колонки в порядку показу.</param>
/// <param name="Rows">Рядки; значення — у поданні <c>GET …/rows</c> (число інваріантно, дата <c>yyyy-MM-dd</c>).</param>
public sealed record RegistryWorkbook(
    string SheetName,
    IReadOnlyList<RegistryWorkbookColumn> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows);

/// <summary>Колонка книги.</summary>
/// <param name="Header">Заголовок — код поля або службова колонка (<c>code</c>, <c>@name</c>…).</param>
/// <param name="Kind">Тип значень: від нього залежить тип комірки.</param>
public sealed record RegistryWorkbookColumn(string Header, CellDataType Kind);
