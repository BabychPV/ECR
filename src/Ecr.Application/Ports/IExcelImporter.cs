using Ecr.Application.Documents.Dto;

namespace Ecr.Application.Ports;

/// <summary>Імпорт із <c>.xlsx</c> — завжди через попередній перегляд diff (ФВ-4.3).</summary>
public interface IExcelImporter
{
    /// <summary>
    /// Розбирає файл і будує diff **без застосування**. Показує, що зміниться,
    /// що конфліктує і що буде відхилено правами або станом періоду.
    /// </summary>
    public Task<ImportPreview> PreviewAsync(long documentId, Stream file, CancellationToken ct);

    /// <summary>Застосовує раніше побудований diff після підтвердження користувачем.</summary>
    public Task<PatchCellsResponse> ApplyAsync(long documentId, string previewToken, CancellationToken ct);

    /// <summary>
    /// Скільки комірок змінить раніше побудований diff — БЕЗ застосування
    /// (директива №11, T10 #45).
    /// </summary>
    /// <remarks>
    /// ⚠ Потрібен ДО рішення «синхронно чи в чергу»: щоб не запускати
    /// застосування заради виміру його ж розміру, обробник питає ЦЕ
    /// напередодні, беручи вже підрахований <c>ImportPlan</c> (той самий,
    /// що збережено при <see cref="PreviewAsync"/>), а не рахує наново з файлу.
    /// </remarks>
    public Task<int> CountPendingChangesAsync(string previewToken, CancellationToken ct);
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
