// src/Ecr.Application/Integration/RawData/IRawDataReader.cs
namespace Ecr.Application.Integration.RawData;

/// <summary>
/// Читання вже зібраних точок PI (<c>ext.RawDataPoint</c>) — «Дані з PI» і «Звідки це значення» (<c>D-463</c>).
/// </summary>
/// <remarks>
/// ⛔ Кожен метод, що торкається <c>ext.RawDataPoint</c>, адресує РІВНО ОДИН ряд — пару
/// «сутність + шлях» (<see cref="RawSeriesKey"/>) у напіввідкритому вікні часу. Це префікс
/// <c>UQ_RawDataPoint (SourceEntityId, SourcePath, Timestamp)</c>, тобто seek, а не скан таблиці,
/// у якій за рік лежать мільйони точок усіх тегів (R2: 1–16 млн/рік). Метода «усі точки сутності»
/// тут немає навмисно.
/// <para>
/// ⚠ Результат кожного методу обмежений аргументом-стелею або числом кошиків: «рік щохвилини» —
/// 525 600 точок, і ні сторінка, ні графік не матеріалізують їх у пам'яті.
/// </para>
/// </remarks>
public interface IRawDataReader
{
    /// <summary>Сутність збору; <c>null</c> — немає (вимкнена теж показується: її історія лишається).</summary>
    public Task<RawSourceEntity?> FindEntityAsync(int sourceEntityId, CancellationToken ct);

    /// <summary>Теги сутності — поля її мапінгів (діючі й призупинені), за шляхом.</summary>
    /// <param name="sourceEntityId">Сутність.</param>
    /// <param name="max">Стеля.</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyList<RawDataTag>> ListTagsAsync(int sourceEntityId, int max, CancellationToken ct);

    /// <summary>Сторінка точок ряду, новіші першими (keyset за <c>Timestamp</c>).</summary>
    /// <param name="key">Ряд і вікно.</param>
    /// <param name="before">Курсор: лише точки, раніші за цю мітку; <c>null</c> — від кінця вікна.</param>
    /// <param name="take">Скільки рядків прочитати (сторінка + 1 для ознаки «є ще»).</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyList<RawPoint>> PageAsync(RawSeriesKey key, DateTime? before, int take, CancellationToken ct);

    /// <summary>Точки ряду за зростанням часу, з мітки <paramref name="after"/> (виключно).</summary>
    /// <remarks>Для потокового експорту: сторінка за сторінкою, без утримання курсора БД між ними.</remarks>
    public Task<IReadOnlyList<RawPoint>> PageAscendingAsync(
        RawSeriesKey key, DateTime? after, int take, CancellationToken ct);

    /// <summary>Скільки точок у ряді за вікно (лише індекс, без читання значень).</summary>
    public Task<long> CountAsync(RawSeriesKey key, CancellationToken ct);

    /// <summary>
    /// Згортка ряду в кошики по <paramref name="bucketSeconds"/> секунд від початку вікна: кількість, мін/макс/середнє.
    /// </summary>
    /// <remarks>Кошиків не більше <c>ceil(вікно / bucketSeconds)</c>; порожні кошики не повертаються.</remarks>
    public Task<IReadOnlyList<RawBucket>> BucketsAsync(RawSeriesKey key, long bucketSeconds, CancellationToken ct);

    /// <summary>
    /// Успішно покриті збором інтервали сутності, що перетинають вікно, за зростанням кінця
    /// (<c>itg.CollectionCoverage</c>, <c>Status IS NULL</c>).
    /// </summary>
    /// <param name="sourceEntityId">Сутність.</param>
    /// <param name="fromUtc">Початок вікна.</param>
    /// <param name="toUtc">Кінець вікна.</param>
    /// <param name="take">Стеля рядків (стеля + 1 — ознака урізання).</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyList<RawInterval>> CoveredAsync(
        int sourceEntityId, DateTime fromUtc, DateTime toUtc, int take, CancellationToken ct);

    /// <summary>Останній прогін збору сутності й момент завершення останнього успішного.</summary>
    public Task<RawCollectionStatus> LastCollectionAsync(int sourceEntityId, CancellationToken ct);
}

/// <summary>Ряд точок і напіввідкрите вікно <c>[FromUtc, ToUtc)</c>.</summary>
/// <param name="SourceEntityId">Сутність збору.</param>
/// <param name="SourcePath">Шлях атрибута в джерелі (<c>EntityFieldMap.SourceField</c>).</param>
/// <param name="FromUtc">Початок, включно.</param>
/// <param name="ToUtc">Кінець, виключно.</param>
public sealed record RawSeriesKey(int SourceEntityId, string SourcePath, DateTime FromUtc, DateTime ToUtc);

/// <summary>Сутність збору для заголовка екрана.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Code">Код у джерелі.</param>
/// <param name="DisplayName">Підпис; <c>null</c> — не задано.</param>
/// <param name="EntityPath">Шлях сутності — тег, який читається, коли мапінгів немає.</param>
/// <param name="IsActive">Чи сутність діє.</param>
public sealed record RawSourceEntity(int Id, string Code, string? DisplayName, string? EntityPath, bool IsActive);

/// <summary>Тег сутності — поле мапінгу.</summary>
/// <param name="SourcePath">Шлях атрибута в джерелі.</param>
/// <param name="MapId">Мапінг; <c>null</c> — тег сутності без мапінгу (сама сутність).</param>
/// <param name="TargetColumnCode">Колонка документа; <c>null</c> — не матеріалізується чи колонку видалено.</param>
/// <param name="TargetRowKey">Рядок документа; <c>null</c> — точки лишаються сирими.</param>
/// <param name="Aggregation">Згортка періоду (<c>Sum</c>, <c>Avg</c>, …); <c>null</c> — не задана.</param>
/// <param name="SourceUnitCode">Одиниця джерела; <c>null</c> — не оголошена.</param>
/// <param name="IsActive">Мапінг діє; <c>false</c> — призупинений.</param>
public sealed record RawDataTag(
    string SourcePath,
    int? MapId,
    string? TargetColumnCode,
    string? TargetRowKey,
    string? Aggregation,
    string? SourceUnitCode,
    bool IsActive);

/// <summary>Точка <c>ext.RawDataPoint</c> як є (одиниця — джерела).</summary>
/// <param name="Timestamp">Мітка часу точки (UTC).</param>
/// <param name="ValueNumeric">Число в одиниці джерела.</param>
/// <param name="ValueString">Текст для нечислових атрибутів.</param>
/// <param name="UnitId">Одиниця джерела; <c>null</c> — не повідомлена.</param>
/// <param name="Quality">Якість за класифікацією джерела.</param>
/// <param name="RetrievedAt">Коли точку отримано (UTC).</param>
/// <param name="CollectionRunId">Прогін збору, що її приніс.</param>
public sealed record RawPoint(
    DateTime Timestamp,
    decimal? ValueNumeric,
    string? ValueString,
    int? UnitId,
    string? Quality,
    DateTime RetrievedAt,
    long CollectionRunId);

/// <summary>Кошик згортки.</summary>
/// <param name="Index">Номер кошика від початку вікна.</param>
/// <param name="Points">Усіх точок у кошику.</param>
/// <param name="NumericPoints">З них числових.</param>
/// <param name="Min">Мінімум числових; <c>null</c> — числових немає.</param>
/// <param name="Max">Максимум числових.</param>
/// <param name="Avg">Середнє арифметичне числових (не зважене за часом).</param>
public sealed record RawBucket(long Index, long Points, long NumericPoints, decimal? Min, decimal? Max, decimal? Avg);

/// <summary>Інтервал часу <c>[FromUtc, ToUtc)</c>.</summary>
/// <param name="FromUtc">Початок.</param>
/// <param name="ToUtc">Кінець.</param>
public sealed record RawInterval(DateTime FromUtc, DateTime ToUtc);

/// <summary>Стан збору сутності.</summary>
/// <param name="LastRunId">Останній прогін; <c>null</c> — збору ще не було.</param>
/// <param name="LastRunStatus"><c>Running</c>/<c>Succeeded</c>/<c>Degraded</c>/<c>Failed</c>.</param>
/// <param name="LastRunStartedAt">Початок останнього прогону (UTC).</param>
/// <param name="LastRunFinishedAt">Завершення останнього прогону; <c>null</c> — триває.</param>
/// <param name="LastSucceededAt">Завершення останнього успішного (<c>Succeeded</c>/<c>Degraded</c>) прогону.</param>
public sealed record RawCollectionStatus(
    long? LastRunId,
    string? LastRunStatus,
    DateTime? LastRunStartedAt,
    DateTime? LastRunFinishedAt,
    DateTime? LastSucceededAt);
