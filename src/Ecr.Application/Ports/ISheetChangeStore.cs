// src/Ecr.Application/Ports/ISheetChangeStore.cs
namespace Ecr.Application.Ports;

/// <summary>Рядок, змінений після маркера: екземпляр таблиці, її опис і сам рядок.</summary>
/// <param name="TableInstanceId">Екземпляр таблиці за період.</param>
/// <param name="TableDefId">Таблиця версії шаблону.</param>
/// <param name="TableRowId">Рядок (<c>doc.TableRow.Id</c>).</param>
public sealed record SheetChangedRow(long TableInstanceId, int TableDefId, long TableRowId);

/// <summary>Результат проби: поточний маркер і рядки, змінені в напівінтервалі <c>[since, marker)</c>.</summary>
/// <param name="Marker">Нова межа — <c>MIN_ACTIVE_ROWVERSION()</c>, прочитана ДО знімка рядків.</param>
/// <param name="Rows">Змінені рядки (не більше стелі виклику).</param>
/// <param name="HasMore">Змінених рядків більше за стелю.</param>
public sealed record SheetChangeProbe(ulong Marker, IReadOnlyList<SheetChangedRow> Rows, bool HasMore);

/// <summary>Авторство змін рядків за журналом <c>aud.CellChange</c>.</summary>
/// <param name="ForeignRowIds">
/// Рядки, ОСТАННЮ не-перерахункову зміну яких зробив ХТОСЬ ІНШИЙ у видимій колонці (не читач, не перерахунок).
/// </param>
/// <param name="RecalculatedRowIds">Рядки, де є зміна перерахунком.</param>
/// <param name="AttributedRowIds">Рядки, для яких у вікні знайдено бодай одну зміну журналу.</param>
/// <param name="LastForeignUserId">Автор найсвіжішої чужої зміни.</param>
/// <param name="LastForeignDisplayName">Його ім'я (<c>null</c> — системний запис без користувача).</param>
/// <param name="LastForeignAt">Час найсвіжішої чужої зміни (UTC).</param>
public sealed record SheetChangeAttribution(
    IReadOnlySet<long> ForeignRowIds,
    IReadOnlySet<long> RecalculatedRowIds,
    IReadOnlySet<long> AttributedRowIds,
    int? LastForeignUserId,
    string? LastForeignDisplayName,
    DateTime? LastForeignAt);

/// <summary>
/// Дешевий маркер змін даних аркуша за період (живе оновлення сітки): «чи змінив хтось дані, відколи я читав».
/// </summary>
/// <remarks>
/// ⛔ Порт нічого не знає про права: таблиці (лише видимі читачеві) і сховані колонки називає обробник.
/// ⚠ Маркер — <c>rowversion</c> рядків <c>doc.TableRow</c> (кожна зміна комірки «торкається» рядка, B04 §2.4), а межа —
/// <c>MIN_ACTIVE_ROWVERSION()</c>: усе нижче неї вже зафіксовано, тож довга транзакція (імпорт) не губиться між двома
/// опитуваннями. Час зміни (<c>aud.CellChange.ChangedAt</c>) для цього не годиться: його ставлять до коміту.
/// </remarks>
public interface ISheetChangeStore
{
    /// <summary>Поточний маркер без проби рядків (перше опитування).</summary>
    public Task<ulong> CurrentMarkerAsync(CancellationToken ct);

    /// <summary>
    /// Маркер і рядки таблиць <paramref name="tableDefIds"/> документа за період, чия версія в <c>[since, marker)</c>.
    /// Одна команда (два оператори: межа, потім знімок рядків).
    /// </summary>
    public Task<SheetChangeProbe> ProbeAsync(
        long documentId, int periodKey, IReadOnlyCollection<int> tableDefIds, ulong since, int cap, CancellationToken ct);

    /// <summary>
    /// Хто змінив рядки <paramref name="rowIds"/> від <paramref name="fromUtc"/>: зміни читача
    /// <paramref name="viewerUserId"/> і сховані колонки <paramref name="hiddenColumnIds"/> не рахуються.
    /// </summary>
    public Task<SheetChangeAttribution> AttributeAsync(
        long documentId, IReadOnlyCollection<long> rowIds, IReadOnlyCollection<int> hiddenColumnIds, DateTime fromUtc,
        int viewerUserId, CancellationToken ct);
}
