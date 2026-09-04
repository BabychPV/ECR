namespace Ecr.Application.Ports;

using Ecr.Application.Documents.Dto;

/// <summary>Імпорт із <c>.xlsx</c> — завжди через попередній перегляд diff (ФВ-4.3).</summary>
public interface IExcelImporter
{
    /// <summary>
    /// Розбирає файл і будує diff **без застосування**. Показує, що зміниться,
    /// що конфліктує і що буде відхилено правами або станом періоду.
    /// </summary>
    Task<ImportPreview> PreviewAsync(long documentId, Stream file, CancellationToken ct);

    /// <summary>Застосовує раніше побудований diff після підтвердження користувачем.</summary>
    Task<PatchCellsResponse> ApplyAsync(long documentId, string previewToken, CancellationToken ct);
}

/// <summary>Результат попереднього перегляду імпорту.</summary>
/// <param name="PreviewToken">Токен для застосування; діє обмежений час.</param>
/// <param name="Changes">Комірки, які зміняться.</param>
/// <param name="Rejected">Комірки, які буде відхилено, із причиною.</param>
/// <param name="Conflicts">Комірки, змінені іншим користувачем після відкриття.</param>
public sealed record ImportPreview(
    string PreviewToken,
    IReadOnlyList<ImportChange> Changes,
    IReadOnlyList<ImportRejection> Rejected,
    IReadOnlyList<CellConflictDto> Conflicts);

/// <summary>Зміна, яку принесе імпорт.</summary>
public sealed record ImportChange(string RowKey, string ColumnCode, object? OldValue, object? NewValue);

/// <summary>Відхилена комірка з причиною — користувач має бачити, які саме (ФВ-4.4).</summary>
public sealed record ImportRejection(string RowKey, string ColumnCode, string ReasonCode, string Message);
