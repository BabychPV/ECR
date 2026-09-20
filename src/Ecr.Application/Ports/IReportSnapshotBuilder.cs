using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Побудова зрізу звітності. <c>rpt.*</c> — **зріз без логіки**: агрегації
/// робить сервіс тут, вʼюха лише проєктує (ФВ-0.3).
/// </summary>
public interface IReportSnapshotBuilder
{
    /// <summary>
    /// Будує зріз. Статус успадковується від даних: <c>Draft</c>, поки аркуші
    /// не затверджені (D-65) — регуляторні вʼюхи такий зріз не віддають.
    /// </summary>
    public Task<long> BuildAsync(int reportVersionId, int projectId, PeriodKey? periodKey,
                          string? parametersJson, CancellationToken ct);

    /// <summary>Позначає зріз поданим — після цього він іммутабельний.</summary>
    public Task MarkSubmittedAsync(long snapshotId, int userId, CancellationToken ct);

    /// <summary>Перераховує статус зрізу після зміни стану затвердження аркушів.</summary>
    public Task<SnapshotStatus> RefreshStatusAsync(long snapshotId, CancellationToken ct);

    /// <summary>Перелік побудованих зрізів.</summary>
    /// <param name="projectId">Проєкт; <c>null</c> — усі.</param>
    /// <param name="periodKey">Період; <c>null</c> — усі.</param>
    /// <param name="visibleProjectIds">
    /// Проєкти, які вільно бачити тому, хто питає; <c>null</c> — без обмеження
    /// (системний виклик, не запит користувача). Порожній перелік означає
    /// «жодного», а не «усі».
    /// </param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ У перелік входять час побудови й контрольна сума. Споживач зрізу —
    /// SSRS і людина, яка звіряє звіт, — має бачити, <b>на яких даних</b> його
    /// побудовано: два зрізи однієї версії за один період відрізняються лише
    /// цим, і без обох полів вибрати правильний неможливо.
    /// <para>
    /// ⛔ Q-239. Фільтр за грантами стоїть саме ТУТ, у запиті, а не в
    /// обробнику над уже вибраною сторінкою (як у
    /// <c>ListDocumentsHandler</c>). Причина — стеля <c>MaxSnapshots</c> і
    /// відсутність курсора: перелік бере 500 НАЙНОВІШИХ зрізів усієї бази, і
    /// відсіювання чужих після вибірки лишало б користувача з порожнім
    /// переліком щоразу, коли 500 останніх побудов належать іншим проєктам —
    /// тобто перетворювало б фікс доступу на втрату власних даних.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<ReportSnapshotSummary>> ListAsync(
        int? projectId, int? periodKey, IReadOnlyCollection<int>? visibleProjectIds, CancellationToken ct);

    /// <summary>Проєкт зрізу; <c>null</c> — зрізу немає.</summary>
    /// <remarks>
    /// ⚠ Окремий дешевий запит, а не частина <see cref="VerifyAsync"/>: грант
    /// на проєкт перевіряється ДО перерахунку суми, інакше сторонній змушував
    /// би сервер читати до 200 000 рядків чужого зрізу заради відмови (BE-17).
    /// </remarks>
    public Task<int?> FindProjectIdAsync(long snapshotId, CancellationToken ct);

    /// <summary>
    /// Перераховує контрольну суму ЗБЕРЕЖЕНОГО вмісту тим самим алгоритмом,
    /// що й побудова, і віддає її поруч зі збереженою. Нічого не змінює.
    /// </summary>
    /// <returns><c>null</c> — зрізу немає.</returns>
    public Task<SnapshotHashes?> VerifyAsync(long snapshotId, CancellationToken ct);

    /// <summary>Сторінка рядків зрізу в широкому вигляді (D-52a).</summary>
    /// <param name="snapshotId">Зріз.</param>
    /// <param name="afterRowNo">
    /// Курсор: останній уже відданий <c>RowNo</c>; <c>0</c> — з початку.
    /// ⚠ З макетом (<c>R8</c>) порядок задає група, а не <c>RowNo</c>, і курсор
    /// означає, скільки рядків уже віддано. Для зрізу без макета це те саме
    /// число: <c>RowNo</c> суцільний і починається з одиниці.
    /// </param>
    /// <param name="limit">Скільки рядків щонайбільше.</param>
    /// <param name="language">
    /// Мова, якою підписати колонки (<c>R9</c>): внутрішній код із
    /// <c>ICurrentUser.Language</c>. ⚠ Параметр запиту, а не властивість зрізу —
    /// той самий зріз двом користувачам віддається з різними заголовками й
    /// однаковими даними.
    /// </param>
    /// <param name="ct">Скасування.</param>
    /// <returns><c>null</c> — зрізу немає.</returns>
    public Task<SnapshotRowsPage?> RowsAsync(
        long snapshotId, int afterRowNo, int limit, string language, CancellationToken ct);
}

/// <summary>Сторінка рядків зрізу.</summary>
/// <param name="Columns">Колонки в порядку опису версії.</param>
/// <param name="Rows">
/// Рядки за зростанням <c>RowNo</c>, а з макетом (<c>R8</c>) — у порядку груп.
/// </param>
/// <param name="NextCursor">Курсор наступної сторінки; <c>null</c> — рядків більше немає.</param>
/// <param name="Groups">
/// Групи макета по ВСЬОМУ зрізу в порядку показу; <c>null</c> — версія
/// групування не оголошує.
/// </param>
/// <param name="Totals">
/// Підсумки по ВСЬОМУ зрізу; <c>null</c> — версія підсумків не оголошує.
/// </param>
/// <param name="ShowGroupHeader">
/// Чи показувати рядок заголовка групи. ⚠ Групи й підсумки рахуються по ВСЬОМУ
/// зрізу й тому приходять однакові на кожній сторінці: підсумок, що міняється
/// від сторінки до сторінки, не є підсумком.
/// </param>
public sealed record SnapshotRowsPage(
    IReadOnlyList<SnapshotColumn> Columns,
    IReadOnlyList<SnapshotRow> Rows,
    int? NextCursor,
    IReadOnlyList<SnapshotRowGroup>? Groups = null,
    IReadOnlyList<SnapshotTotal>? Totals = null,
    bool ShowGroupHeader = false);

/// <summary>Група рядків зрізу (<c>R8</c>, макет з однією групою).</summary>
/// <param name="Column">Код колонки групування.</param>
/// <param name="Value">Значення колонки, спільне для рядків групи; <c>null</c> — порожнє.</param>
/// <param name="RowCount">
/// Скільки рядків у групі. ⚠ По ВСЬОМУ зрізу — групи можуть не вміститися в
/// одну сторінку, і саме за цим числом книга знає, де група закінчується.
/// </param>
/// <param name="Totals">Підсумки цієї групи; порожньо — підсумків не оголошено.</param>
public sealed record SnapshotRowGroup(
    string Column, object? Value, int RowCount, IReadOnlyList<SnapshotTotal> Totals);

/// <summary>Порахований підсумок.</summary>
/// <param name="Column">Код колонки.</param>
/// <param name="Fn">Функція: <c>sum</c>, <c>count</c>, <c>avg</c>, <c>min</c>, <c>max</c>.</param>
/// <param name="Value">Значення; <c>null</c> — рахувати не було з чого.</param>
public sealed record SnapshotTotal(string Column, string Fn, object? Value);

/// <summary>Колонка зрізу.</summary>
/// <param name="Code">Код — ключ у <see cref="SnapshotRow.Cells"/>.</param>
/// <param name="Kind">Тип значення: <c>text</c>, <c>number</c>, <c>date</c>.</param>
/// <param name="Name">
/// Підпис колонки мовою запиту (<c>R9</c>), уже з розгорнутим фолбеком
/// <c>мова → en → код</c> (<see cref="Reporting.ReportColumnNames"/>). ⚠ Ніколи
/// не порожній: опис без назв підписує колонку її КОДОМ, тож споживачеві не
/// треба знати про фолбек і тримати другу його копію.
/// </param>
public sealed record SnapshotColumn(string Code, string Kind, string Name);

/// <summary>Рядок зрізу.</summary>
/// <param name="RowNo">Номер рядка в зрізі.</param>
/// <param name="Cells">Значення за кодом колонки: рядок, число або <c>null</c>.</param>
public sealed record SnapshotRow(int RowNo, IReadOnlyDictionary<string, object?> Cells);

/// <summary>Суми зрізу: записана при побудові й перераховані зараз.</summary>
/// <param name="Stored">Збережена сума в hex; порожньо — не рахувалася.</param>
/// <param name="Actual">Сума, перерахована за рядками зрізу, в hex.</param>
/// <param name="LegacyActual">
/// Та сама сума за форматом до BE-17; <c>null</c> — не рахувалася, бо
/// <paramref name="Actual"/> уже збіглася. ⚠ Тимчасово: прибрати разом зі
/// старим форматом, коли зрізів, побудованих до BE-17, не лишиться.
/// </param>
public sealed record SnapshotHashes(string Stored, string Actual, string? LegacyActual = null);

/// <summary>Зріз у переліку.</summary>
/// <param name="Id">Ідентифікатор зрізу.</param>
/// <param name="ReportVersionId">Версія звіту.</param>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="PeriodKey">Період; <c>null</c> — увесь рік проєкту.</param>
/// <param name="Status">Статус даних зрізу (D-65).</param>
/// <param name="IsCurrent">Чи це поточний зріз для пари «версія × проєкт × період».</param>
/// <param name="RowCount">Скільки рядків.</param>
/// <param name="ContentHash">Контрольна сума вмісту в hex; <c>null</c> — не рахувалася.</param>
/// <param name="BuiltAt">Коли побудовано.</param>
public sealed record ReportSnapshotSummary(
    long Id,
    int ReportVersionId,
    int ProjectId,
    int? PeriodKey,
    string Status,
    bool IsCurrent,
    int RowCount,
    string? ContentHash,
    DateTime BuiltAt);
