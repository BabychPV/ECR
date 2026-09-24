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
/// <param name="RowKey">Рядок.</param>
/// <param name="ColumnCode">Колонка.</param>
/// <param name="OldValue">Поточне значення; <c>null</c> — порожньо.</param>
/// <param name="NewValue">Значення з файлу; <c>null</c> — порожньо.</param>
/// <param name="TableCode">
/// Таблиця зміни. ⛔ `V-10`: у 91 таблиці шаблону ключі рядків і коди колонок
/// ОДНАКОВІ (<c>R1</c>/<c>C1</c>), тож без таблиці рядок переліку не каже,
/// ДЕ саме зміниться число. <c>null</c> лише в плані, збереженому до цієї
/// правки.
/// </param>
/// <param name="TableNameL10n">Назва таблиці мовами каталогу — для показу.</param>
public sealed record ImportChange(
    string RowKey,
    string ColumnCode,
    object? OldValue,
    object? NewValue,
    string? TableCode = null,
    Ecr.Domain.ValueObjects.LocalizedText? TableNameL10n = null);

/// <summary>Відхилена комірка з причиною — користувач має бачити, які саме (ФВ-4.4).</summary>
/// <param name="RowKey">Рядок; <c>—</c> — причина не про рядок.</param>
/// <param name="ColumnCode">Колонка; для відмови цілої таблиці — її код.</param>
/// <param name="ReasonCode">Код причини (<c>ECR-…</c>).</param>
/// <param name="Message">Діагностичний текст для журналу — НЕ для показу людині.</param>
/// <param name="TableCode">Таблиця відмови (`V-10`, як і в <see cref="ImportChange"/>).</param>
/// <param name="TableNameL10n">Назва таблиці мовами каталогу — для показу.</param>
/// <param name="MessageKey">
/// Ключ тексту причини в каталозі (D-95). ⛔ `V-10`: інтерфейс показує текст
/// за цим ключем мовою користувача, а не <see cref="Message"/> — доти відмови
/// приходили готовими українськими реченнями («Правило доступу: лише читання.»).
/// Для відмови правами — <c>deny.&lt;EditDenyReason&gt;</c>, ті самі тексти, що
/// в підказці сірої комірки сітки.
/// </param>
public sealed record ImportRejection(
    string RowKey,
    string ColumnCode,
    string ReasonCode,
    string Message,
    string? TableCode = null,
    Ecr.Domain.ValueObjects.LocalizedText? TableNameL10n = null,
    string? MessageKey = null);
