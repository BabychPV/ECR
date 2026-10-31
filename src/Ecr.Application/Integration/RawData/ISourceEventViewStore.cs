// src/Ecr.Application/Integration/RawData/ISourceEventViewStore.cs
using Ecr.Application.Integration.SourceEvents;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;

namespace Ecr.Application.Integration.RawData;

/// <summary>
/// Читання подій PI (Event Frames), уже синхронізованих у рядки документів, — «Дані з PI» для подій і «Звідки це
/// значення» для комірки рядка події (<c>D-465</c>).
/// </summary>
/// <remarks>
/// ⛔ Порт нічого не вирішує про доступ: обробник входу доводить право (адміністратор інтеграції) чи доступ до
/// комірки ДО виклику й сам маскує те, що читачеві не видно (документ, колонку).
/// <para>
/// ⛔ Лише сховище: у PI наживо порт не ходить. Значення атрибутів — останнє прочитання синку
/// (<c>ext.SourceEventAttributeValue</c>), тож недоступний PI перегляду не ламає, а відкриття екрана не навантажує PI.
/// </para>
/// <para>
/// ⚠ Кожен метод обмежений аргументом-стелею, а кількість звернень — сталою на сторінку (не на подію): зв'язки — seek
/// <c>IX_SEL_MapStart</c>, значення — префікс ключа <c>PK_SourceEventAttributeValue</c>, комірки — ключ рядка й
/// <c>PK_CellValue</c>, журнал змін — <c>IX_CellChange_Cell</c>, видалення — <c>IX_StructureChange_Entity</c>.
/// </para>
/// </remarks>
public interface ISourceEventViewStore
{
    /// <summary>Мапінги подій сутності (діючі й призупинені) з полями, за Id.</summary>
    public Task<IReadOnlyList<PiEventMap>> MapsAsync(int sourceEntityId, CancellationToken ct);

    /// <summary>Один мапінг; <c>null</c> — немає.</summary>
    public Task<PiEventMap?> MapAsync(int mapId, CancellationToken ct);

    /// <summary>
    /// Усе, що потрібно, щоб порахувати «що записав би синк» (поля тим самим завантажувачем, що й синк), і періоди
    /// проєкту мапінгу з поданими/затвердженими аркушами таблиці.
    /// </summary>
    public Task<PiEventMapContext> ContextAsync(PiEventMap map, CancellationToken ct);

    /// <summary>Періоди проєкту документа мапінгу з межами в UTC (без полів і довідників).</summary>
    public Task<IReadOnlyList<SourceEventPeriod>> PeriodsAsync(PiEventMap map, CancellationToken ct);

    /// <summary>Сторінка зв'язків мапінгу за вікном початку, новіші першими.</summary>
    /// <param name="mapId">Мапінг.</param>
    /// <param name="filter">Вікно й фільтри.</param>
    /// <param name="before">Курсор: лише раніші за (початок, Id); <c>null</c> — від кінця вікна.</param>
    /// <param name="take">Скільки прочитати (сторінка + 1).</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyList<PiEventLink>> LinksAsync(
        int mapId, PiEventFilter filter, PiEventCursor? before, int take, CancellationToken ct);

    /// <summary>Скільки зв'язків мапінгу під фільтром.</summary>
    public Task<int> CountLinksAsync(int mapId, PiEventFilter filter, CancellationToken ct);

    /// <summary>Зв'язки рядка документа (рядок події; у нормі один).</summary>
    public Task<IReadOnlyList<PiEventLink>> LinksOfRowAsync(long tableInstanceId, string rowKey, int take, CancellationToken ct);

    /// <summary>Значення атрибутів за останнім прочитанням для зв'язків.</summary>
    public Task<IReadOnlyList<PiObservedValue>> ValuesAsync(IReadOnlyCollection<long> linkIds, CancellationToken ct);

    /// <summary>Комірки рядків (значення й останнє походження за журналом змін) у названих колонках.</summary>
    public Task<IReadOnlyList<PiCellValue>> CellsAsync(
        IReadOnlyList<PiRowAddress> rows, IReadOnlyCollection<int> columnDefIds, CancellationToken ct);

    /// <summary>
    /// Журнал видалень рядків подій сутності (<c>aud.StructureChange</c>, <c>SourceEventRowRemoved</c>) за вікном
    /// моменту видалення, новіші першими.
    /// </summary>
    public Task<IReadOnlyList<PiEventRemoval>> RemovalsAsync(
        int sourceEntityId, DateTime fromUtc, DateTime toUtc, int take, CancellationToken ct);
}

/// <summary>Мапінг подій сутності.</summary>
/// <param name="MapId">Мапінг.</param>
/// <param name="SourceEntityId">Сутність-шаблон подій.</param>
/// <param name="SourceEntityCode">Код шаблону в джерелі.</param>
/// <param name="SourceEntityName">Підпис сутності.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="DocumentKey">Бізнес-ключ документа — лише для тих, хто документ бачить.</param>
/// <param name="TableDefId">Таблиця.</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="TimeZoneId">Пояс проєкту документа.</param>
/// <param name="IsActive">Мапінг діє.</param>
/// <param name="Fields">Поля «атрибут → колонка».</param>
public sealed record PiEventMap(
    int MapId,
    int SourceEntityId,
    string SourceEntityCode,
    string? SourceEntityName,
    long DocumentId,
    string DocumentKey,
    int TableDefId,
    string TableCode,
    string TimeZoneId,
    bool IsActive,
    IReadOnlyList<PiEventMapField> Fields);

/// <summary>Поле мапінгу подій.</summary>
/// <param name="ColumnDefId">Колонка.</param>
/// <param name="ColumnCode">Код колонки; <c>null</c> — колонку видалено.</param>
/// <param name="SourceAttribute">Атрибут або <c>$start</c>/<c>$end</c>/<c>$name</c>.</param>
/// <param name="Scope">Де лежить атрибут.</param>
/// <param name="IsMonthColumn">Колонка місяця (D-465: такі поля збереження відхиляє; наявні — попередження).</param>
public sealed record PiEventMapField(
    int ColumnDefId, string? ColumnCode, string SourceAttribute, SourceEventAttributeScope Scope, bool IsMonthColumn);

/// <summary>Контекст обчислення «що записав би синк» для мапінгу.</summary>
/// <param name="Fields">Поля (тим самим завантажувачем, що й синк).</param>
/// <param name="ColumnScales">Масштаб колонок за Id.</param>
/// <param name="Units">Довідник одиниць; <c>null</c> — жодне поле не має одиниці джерела.</param>
/// <param name="Periods">Періоди проєкту з межами в UTC.</param>
/// <param name="LockedPeriods">Періоди, у яких аркуш таблиці поданий чи затверджений.</param>
public sealed record PiEventMapContext(
    IReadOnlyList<SourceEventFieldPlan> Fields,
    IReadOnlyDictionary<int, byte?> ColumnScales,
    UnitCatalogSnapshot? Units,
    IReadOnlyList<SourceEventPeriod> Periods,
    IReadOnlySet<int> LockedPeriods);

/// <summary>Вікно й фільтри списку подій.</summary>
/// <param name="FromUtc">Початок події не раніше (включно).</param>
/// <param name="ToUtc">Початок події раніше (виключно).</param>
/// <param name="Statuses">Лише ці стани зв'язку; порожньо — усі.</param>
/// <param name="ElementPrefix">Первинний елемент починається з (ключовий вигляд, верхній регістр); <c>null</c> — будь-який.</param>
/// <param name="NameContains">Назва події містить; <c>null</c> — будь-яка.</param>
public sealed record PiEventFilter(
    DateTime FromUtc,
    DateTime ToUtc,
    IReadOnlyCollection<SourceEventLinkStatus> Statuses,
    string? ElementPrefix,
    string? NameContains);

/// <summary>Курсор keyset: (початок, Id) останнього рядка сторінки.</summary>
/// <param name="StartUtc">Початок події.</param>
/// <param name="LinkId">Зв'язок.</param>
public sealed record PiEventCursor(DateTime StartUtc, long LinkId);

/// <summary>Зв'язок «подія ↔ рядок», як його бачить перегляд.</summary>
public sealed record PiEventLink(
    long Id,
    int MapId,
    string SourceEventId,
    string? EventName,
    DateTime StartUtc,
    DateTime? EndUtc,
    DateTime? SourceModifiedUtc,
    string? PrimaryElement,
    SourceEventLinkStatus Status,
    int? PeriodKey,
    long? TableInstanceId,
    string? RowKey,
    string? KeptManualJson,
    string? UnmappedJson,
    DateTime FirstSeenAt,
    DateTime LastSeenAt,
    DateTime LastSyncAt);

/// <summary>Значення атрибута за останнім прочитанням синку.</summary>
public sealed record PiObservedValue(
    long LinkId,
    SourceEventAttributeScope Scope,
    string Attribute,
    decimal? Numeric,
    string? Text,
    bool Truncated,
    string? Unit,
    DateTime? SourceModifiedUtc,
    DateTime ChangedAt);

/// <summary>Адреса рядка події в документі.</summary>
public sealed record PiRowAddress(int PeriodKey, long TableInstanceId, string RowKey, long DocumentId);

/// <summary>Комірка рядка події: значення (типізоване) й останнє походження за журналом змін.</summary>
/// <param name="PeriodKey">Період рядка.</param>
/// <param name="TableInstanceId">Екземпляр.</param>
/// <param name="RowKey">Рядок.</param>
/// <param name="ColumnDefId">Колонка.</param>
/// <param name="HasValue">Комірка існує й не порожня.</param>
/// <param name="Numeric">Число.</param>
/// <param name="Text">Текст.</param>
/// <param name="Date">Дата (пояс проєкту).</param>
/// <param name="RegistryEntryId">Запис довідника.</param>
/// <param name="LastOrigin">Походження останнього запису журналу змін; <c>null</c> — записів немає.</param>
/// <param name="LastChangedAt">Коли (UTC).</param>
public sealed record PiCellValue(
    int PeriodKey,
    long TableInstanceId,
    string RowKey,
    int ColumnDefId,
    bool HasValue,
    decimal? Numeric,
    string? Text,
    DateTime? Date,
    long? RegistryEntryId,
    string? LastOrigin,
    DateTime? LastChangedAt);

/// <summary>Запис журналу видалення рядка події (склад — із <c>OldJson</c>, без значень комірок).</summary>
public sealed record PiEventRemoval(
    DateTime RemovedAt,
    string? EventId,
    string? EventName,
    DateTime? EventStartUtc,
    int? PeriodKey,
    int? MapId,
    long? DocumentId,
    long? TableInstanceId,
    string? RowKey,
    string? RunId);
