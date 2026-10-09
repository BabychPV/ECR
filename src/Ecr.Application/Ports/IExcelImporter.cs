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
    /// <param name="documentId">Документ.</param>
    /// <param name="previewToken">Токен перегляду.</param>
    /// <param name="overwriteRows">
    /// ✎ AN-114 (D-338). Рядки, для яких людина свідомо перезаписує чужі правки,
    /// зроблені після експорту книги; <c>null</c>/порожньо — поведінка AN-103
    /// (конфліктні рядки не застосовуються, решта — так). Рядок, що не був
    /// конфліктом «змінено після експорту» в цьому перегляді, — відмова
    /// <c>ECR-IMP-0422</c> (<c>overwriteNotConflict</c>).
    /// </param>
    /// <param name="ct">Скасування.</param>
    public Task<PatchCellsResponse> ApplyAsync(
        long documentId, string previewToken, IReadOnlyList<ImportOverwriteRow>? overwriteRows, CancellationToken ct);

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
    /// <para>
    /// ⛔ L1-20: <paramref name="documentId"/> — документ ЗАПИТУ; перегляд іншого документа відмовляє
    /// <c>ECR-IMP-0422</c> (<c>previewOtherDocument</c>) ще ДО постановки в чергу, а не лише всередині задачі.
    /// </para>
    /// <para>
    /// ✎ AN-114: рахує й перезаписувані рядки (<paramref name="overwriteRows"/>) — їх
    /// застосування теж пише.
    /// </para>
    public Task<int> CountPendingChangesAsync(
        long documentId, string previewToken, IReadOnlyList<ImportOverwriteRow>? overwriteRows, CancellationToken ct);
}

/// <summary>
/// Рядок книги, для якого людина свідомо перезаписує чужу правку, зроблену
/// після експорту (AN-114, D-338).
/// </summary>
/// <param name="TableCode">Таблиця (як <c>tableCode</c> відмови-конфлікту перегляду).</param>
/// <param name="RowKey">Рядок (як <c>rowKey</c> відмови-конфлікту перегляду).</param>
/// <remarks>
/// ⚠ Перелік рядків, а не прапорець на весь імпорт: згода дається на ТЕ, що
/// людина бачила й позначила, а не «на все, що там буде». Рядок, а не комірка:
/// конфлікт визначає версія рядка, тож усі його змінені комірки — один конфлікт.
/// </remarks>
public sealed record ImportOverwriteRow(string TableCode, string RowKey);

/// <summary>Результат попереднього перегляду імпорту.</summary>
/// <param name="PreviewToken">Токен для застосування; діє обмежений час.</param>
/// <param name="Changes">Комірки, які зміняться.</param>
/// <param name="Rejected">Комірки, які буде відхилено, із причиною.</param>
/// <param name="Conflicts">Комірки, змінені іншим користувачем після відкриття.</param>
/// <param name="Overwritable">
/// ✎ AN-114 (D-338). Комірки рядків, змінених кимось після експорту книги
/// (у <paramref name="Rejected"/> вони ж — відмовою
/// <c>err.ECR-CELL-0409.importRowChangedSinceExport</c>): <c>OldValue</c> — чинне
/// (чуже) значення, <c>NewValue</c> — значення з книги. Застосовуються лише для
/// рядків, названих у <c>ImportApplyRequest.OverwriteRows</c>. ⛔ AN-118: лише
/// комірки, які людина в книзі змінила відносно експорту; ті, що лишились як
/// були при вивантаженні, сюди не потрапляють — чуже новіше значення в них
/// лишається.
/// </param>
public sealed record ImportPreview(
    string PreviewToken,
    IReadOnlyList<ImportChange> Changes,
    IReadOnlyList<ImportRejection> Rejected,
    IReadOnlyList<CellConflictDto> Conflicts,
    IReadOnlyList<ImportChange> Overwritable);

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
/// <param name="RoundedFrom">
/// Число з файлу ДО округлення до `Scale` колонки (ФВ-9.16b); <c>null</c> — не
/// округлювалось. <c>NewValue</c> — вже округлене: саме воно буде записано.
/// </param>
public sealed record ImportChange(
    string RowKey,
    string ColumnCode,
    object? OldValue,
    object? NewValue,
    string? TableCode = null,
    Ecr.Domain.ValueObjects.LocalizedText? TableNameL10n = null,
    object? RoundedFrom = null);

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
/// <param name="ExcelCell">
/// Адреса комірки книги (<c>B3</c>), у якій стоїть відхилене значення (P3);
/// для значення поза рядками таблиці (`V-10`) — єдиний її орієнтир.
/// <c>null</c> — відмова цілої таблиці.
/// </param>
public sealed record ImportRejection(
    string RowKey,
    string ColumnCode,
    string ReasonCode,
    string Message,
    string? TableCode = null,
    Ecr.Domain.ValueObjects.LocalizedText? TableNameL10n = null,
    string? MessageKey = null,
    string? ExcelCell = null);
