// src/Ecr.Application/Ports/IPermitBindingStore.cs
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Ports;

/// <summary>Прив'язка з тим, чого їй бракує для показу: коди таблиць, колонок, типу (читання, без відстеження).</summary>
/// <param name="Binding">Прив'язка.</param>
/// <param name="Columns">Її колонки.</param>
/// <param name="SheetTableCode">Код таблиці аркуша.</param>
/// <param name="PermitTypeCode">Код типу дозволу.</param>
/// <param name="PermitTableCode">Код таблиці типу.</param>
/// <param name="HeaderFieldCode">Код поля шапки; <c>null</c> - поля шапки немає.</param>
/// <param name="SheetColumnCodes">Коди колонок аркуша за ідентифікатором.</param>
public sealed record PermitBindingView(
    PermitSheetBinding Binding,
    IReadOnlyList<PermitSheetBindingColumn> Columns,
    string SheetTableCode,
    string PermitTypeCode,
    string PermitTableCode,
    string? HeaderFieldCode,
    IReadOnlyDictionary<int, string> SheetColumnCodes);

/// <summary>Доступ до прив'язок таблиць аркуша до дозволів (Permits, PM-09).</summary>
/// <remarks>
/// ⚠ Каскаду від прив'язки до колонок у схемі немає (усі FK - Restrict): колонки видаляє той, хто видаляє прив'язку,
/// явно (<see cref="RemoveColumns"/>).
/// </remarks>
public interface IPermitBindingStore
{
    /// <summary>Прив'язки версії в порядку коду (без відстеження).</summary>
    /// <param name="templateVersionId">Версія шаблону.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<PermitBindingView>> ListAsync(int templateVersionId, CancellationToken ct);

    /// <summary>Прив'язка версії за кодом (без відстеження); <c>null</c> - немає.</summary>
    /// <param name="templateVersionId">Версія шаблону.</param>
    /// <param name="code">Код прив'язки.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<PermitBindingView?> GetAsync(int templateVersionId, string code, CancellationToken ct);

    /// <summary>Прив'язка версії за кодом ВІДСТЕЖУВАНА, колонки не завантажені; <c>null</c> - немає.</summary>
    /// <param name="templateVersionId">Версія шаблону.</param>
    /// <param name="code">Код прив'язки.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<PermitSheetBinding?> FindForUpdateAsync(int templateVersionId, string code, CancellationToken ct);

    /// <summary>Колонки прив'язки ВІДСТЕЖУВАНІ (для видалення).</summary>
    /// <param name="bindingId">Прив'язка.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<PermitSheetBindingColumn>> ListColumnsForUpdateAsync(int bindingId, CancellationToken ct);

    /// <summary>Код прив'язки, що вже зайняла таблицю аркуша версії (без відстеження); <c>null</c> - таблиця вільна.</summary>
    /// <param name="templateVersionId">Версія шаблону.</param>
    /// <param name="sheetTableDefId">Таблиця аркуша.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<string?> FindCodeBySheetTableAsync(int templateVersionId, int sheetTableDefId, CancellationToken ct);

    /// <summary>Чи є прив'язка з таким кодом у будь-якій версії (<c>UQ_PermitSheetBinding_Code</c> - по всій системі).</summary>
    /// <param name="code">Код.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<bool> CodeExistsAsync(string code, CancellationToken ct);

    /// <summary>
    /// Найбільше рядків таблиці в одному живому дозволі; <c>null</c> - рядків ще немає ніде (кількість невідома).
    /// </summary>
    /// <param name="tableRegistryDefId">Реєстр таблиці дозволу.</param>
    /// <param name="compositionFieldDefId">Поле композиції (посилання на дозвіл-батька).</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<int?> MaxPermitRowsAsync(int tableRegistryDefId, int compositionFieldDefId, CancellationToken ct);

    /// <summary>Ставить прив'язку (з колонками) у чергу на вставку.</summary>
    /// <param name="binding">Прив'язка.</param>
    public void Add(PermitSheetBinding binding);

    /// <summary>Ставить прив'язку в чергу на видалення (колонки - окремо).</summary>
    /// <param name="binding">Прив'язка.</param>
    public void Remove(PermitSheetBinding binding);

    /// <summary>Ставить колонки в чергу на видалення.</summary>
    /// <param name="columns">Колонки.</param>
    public void RemoveColumns(IEnumerable<PermitSheetBindingColumn> columns);
}
