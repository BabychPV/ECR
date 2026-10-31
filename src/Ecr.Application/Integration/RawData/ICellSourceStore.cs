// src/Ecr.Application/Integration/RawData/ICellSourceStore.cs
namespace Ecr.Application.Integration.RawData;

/// <summary>
/// Звідки комірка отримує значення з PI (<c>D-463</c>): мапінги, що цілять у пару «колонка + рядок»,
/// межі періоду документа і події журналу покриття сутності за період.
/// </summary>
/// <remarks>
/// ⛔ Порт нічого не вирішує про доступ: його кличе <see cref="CellSourceHandler"/> лише ПІСЛЯ того,
/// як довів, що користувач читає саме цю комірку (документ, проєкт, Deny, колонка, Q-17).
/// </remarks>
public interface ICellSourceStore
{
    /// <summary>
    /// Мапінги збору, що цілять у колонку <paramref name="columnDefId"/> і рядок <paramref name="rowKey"/>
    /// (діючі й призупинені), за ідентифікатором.
    /// </summary>
    /// <param name="columnDefId">Колонка версії шаблону екземпляра.</param>
    /// <param name="rowKey">Ключ рядка.</param>
    /// <param name="max">Стеля.</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyList<CellSourceMap>> MapsAsync(int columnDefId, string rowKey, int max, CancellationToken ct);

    /// <summary>Межі періоду документа і пояс його проєкту; <c>null</c> — періоду немає.</summary>
    public Task<CellPeriodWindow?> PeriodAsync(long documentId, int periodKey, CancellationToken ct);

    /// <summary>Події журналу покриття сутності за період (рядки зі статусом), новіші першими.</summary>
    /// <param name="sourceEntityId">Сутність збору.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="take">Стеля.</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyList<CellSourceEvent>> PeriodEventsAsync(
        int sourceEntityId, int periodKey, int take, CancellationToken ct);

    /// <summary>
    /// Значення за правилом згортки мапінгу над точками вікна — ТИМ САМИМ кодом, що пише матеріалізація
    /// (<c>CollectedFold</c>: якість, межові точки, змішані одиниці, <c>PeriodFold</c>, конверсія після згортки).
    /// </summary>
    /// <param name="map">Мапінг комірки.</param>
    /// <param name="window">Вікно згортки (<c>Period.FoldBounds</c>).</param>
    /// <param name="maxPoints">Стеля точок у пам'яті; понад неї — <see cref="CellFoldState.TooManyPoints"/>, а не часткове число.</param>
    /// <param name="ct">Скасування.</param>
    public Task<CellFold> FoldAsync(CellSourceMap map, RawSeriesKey window, int maxPoints, CancellationToken ct);

    /// <summary>
    /// Останній запис журналу змін комірки (рядок + колонка) у документі — в екземплярах таблиці за
    /// <paramref name="periodKeys"/> (для Q-17 — і період-власник); <c>null</c> — змін не записано (або вони в архіві).
    /// </summary>
    public Task<CellLastWrite?> LastWriteAsync(
        long documentId, int tableDefId, string rowKey, int columnDefId, IReadOnlyList<int> periodKeys, CancellationToken ct);
}

/// <summary>Стан згортки для комірки.</summary>
public enum CellFoldState
{
    /// <summary>Є значення.</summary>
    Value,

    /// <summary>Придатних точок немає — матеріалізація комірку не чіпає.</summary>
    NoData,

    /// <summary>У вікні не лишилося сирих точок — матеріалізація комірку очищує.</summary>
    Cleared,

    /// <summary>У вікні точки різних одиниць джерела — не згортається (X3-02).</summary>
    MixedUnits,

    /// <summary>Згорнуте не переводиться в одиницю документа.</summary>
    UnitFailure,

    /// <summary>Точок у вікні більше за стелю перегляду — не обчислено (а не часткове число).</summary>
    TooManyPoints,

    /// <summary>Мапінг не називає згортки — не матеріалізується.</summary>
    NoAggregation,
}

/// <summary>Згортка мапінгу за вікно комірки.</summary>
/// <param name="State">Стан.</param>
/// <param name="Value">Значення в одиниці ДОКУМЕНТА, до масштабу сховища (лише <see cref="CellFoldState.Value"/>).</param>
/// <param name="PercentGood">Частка придатних точок / покритого часу, якщо згортка її дала.</param>
public sealed record CellFold(CellFoldState State, decimal? Value, decimal? PercentGood);

/// <summary>Останній запис журналу змін комірки.</summary>
/// <param name="Origin">Походження (<c>CellChangeOrigins</c>).</param>
/// <param name="NewValue">Записане значення (канонічний рядок).</param>
/// <param name="ChangedAt">Коли (UTC).</param>
public sealed record CellLastWrite(string Origin, string? NewValue, DateTime ChangedAt);

/// <summary>Мапінг, що живить комірку.</summary>
/// <param name="MapId">Ідентифікатор мапінгу.</param>
/// <param name="SourceEntityId">Сутність збору.</param>
/// <param name="SourceEntityCode">Код сутності в джерелі.</param>
/// <param name="SourceEntityName">Підпис сутності; <c>null</c> — не задано.</param>
/// <param name="SourcePath">Тег — шлях атрибута в джерелі.</param>
/// <param name="Aggregation">Згортка точок періоду (<c>Sum</c>, <c>Avg</c>, <c>Last</c>, …).</param>
/// <param name="SourceUnitCode">Одиниця джерела.</param>
/// <param name="TargetUnitCode">Одиниця, в якій значення лягає в документ.</param>
/// <param name="IsActive">Мапінг діє; <c>false</c> — призупинений: нові точки в комірку не пишуться.</param>
/// <param name="Kind">Згортка як перелік (для обчислення); <c>null</c> — не задана.</param>
/// <param name="IsStep">Ряд ступінчастий (HSE301 §4.1).</param>
/// <param name="SourceUnitId">Одиниця джерела.</param>
/// <param name="TargetUnitId">Одиниця документа.</param>
public sealed record CellSourceMap(
    int MapId,
    int SourceEntityId,
    string SourceEntityCode,
    string? SourceEntityName,
    string SourcePath,
    string? Aggregation,
    string? SourceUnitCode,
    string? TargetUnitCode,
    bool IsActive,
    Ecr.Domain.Entities.External.AggregationKind? Kind = null,
    bool IsStep = false,
    int? SourceUnitId = null,
    int? TargetUnitId = null);

/// <summary>Період документа.</summary>
/// <param name="PeriodStart">Перший день.</param>
/// <param name="PeriodEnd">Останній день, включно.</param>
/// <param name="TimeZoneId">Пояс проєкту (IANA/Windows); <c>null</c> — типовий.</param>
public sealed record CellPeriodWindow(DateOnly PeriodStart, DateOnly PeriodEnd, string? TimeZoneId);

/// <summary>Подія журналу покриття.</summary>
/// <param name="Id">Ідентифікатор рядка.</param>
/// <param name="Status">Статус (<c>CollectionCoverage.KnownStatuses</c>).</param>
/// <param name="Details">Конверт <c>{"k":…,"p":{…}}</c> або старий текст.</param>
/// <param name="At">Коли записано (UTC).</param>
public sealed record CellSourceEvent(long Id, string Status, string? Details, DateTime At);
