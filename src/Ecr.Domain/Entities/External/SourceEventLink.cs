// src/Ecr.Domain/Entities/External/SourceEventLink.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.External;

/// <summary>Що джерело сказало про подію в цьому прогоні (§4.7.1, <c>SourceEvent</c>).</summary>
/// <param name="SourceEventId">ID події в джерелі — ключ синхронізації.</param>
/// <param name="EventName">Назва події; <c>null</c> — джерело не дало.</param>
/// <param name="StartUtc">Початок, UTC.</param>
/// <param name="EndUtc">Кінець, UTC, виключно; <c>null</c> — подія ще триває.</param>
/// <param name="SourceModifiedUtc">Остання зміна події в джерелі.</param>
public sealed record SourceEventObservation(
    string SourceEventId,
    string? EventName,
    DateTime StartUtc,
    DateTime? EndUtc,
    DateTime? SourceModifiedUtc);

/// <summary>Рядок документа, у який лягла подія.</summary>
/// <param name="PeriodKey">Період екземпляра таблиці.</param>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="RowKey">Ключ рядка (<c>EF-…</c>, §4.7.4 крок 2).</param>
public sealed record SourceEventRowRef(int PeriodKey, long TableInstanceId, string RowKey);

/// <summary>
/// Зв'язок «подія джерела ↔ рядок»: провенанс і стан синхронізації
/// (<c>ext.SourceEventLink</c>, FEATURE-HSE301-VIEW §4.7.3–4.7.4).
/// </summary>
/// <remarks>
/// ⛔ Переходи стану тримає одне правило — <see cref="IsTransitionAllowed"/>, —
/// і воно спирається на те, чи є в зв'язку РЯДОК:
/// <list type="bullet">
/// <item><see cref="SourceEventLinkStatus.Open"/>, <see cref="SourceEventLinkStatus.PeriodNotOpen"/>,
/// <see cref="SourceEventLinkStatus.RowLimit"/> описують подію, рядка якої ще немає, — зв'язку з
/// рядком вони недоступні. Звідси <c>Missing → Open</c> заборонено: рядок уже записано з кінцем
/// події, і «знову триває» синхронізація виразити не може — видалити рядок документа в системі
/// нічим (§4.7.4, крок 4).</item>
/// <item><see cref="SourceEventLinkStatus.Missing"/> і <see cref="SourceEventLinkStatus.PeriodChanged"/>
/// кажуть про рядок, що вже є, — зв'язку без рядка вони недоступні (зникла подія, якої реєстр
/// ніколи не показував, позначки не отримує).</item>
/// <item><see cref="SourceEventLinkStatus.Synced"/> і <see cref="SourceEventLinkStatus.Unmapped"/> —
/// рядок записано; із будь-якого стану, якщо рядок той самий. Звідси <c>Open → Synced</c> і
/// <c>Missing → Synced</c> (подія повернулася).</item>
/// <item><see cref="SourceEventLinkStatus.PeriodClosed"/> — «нічого не пишемо»; доступний завжди.</item>
/// </list>
///
/// ⛔ Рядок прив'язується один раз. Інший рядок для тієї самої події —
/// відмова: подія в двох рядках подвоїла б викиди (§4.7.4, крок 4).
///
/// ⚠ Зовнішнього ключа на <c>doc.TableInstance</c> немає — з тієї самої причини,
/// що в <see cref="RowWindowValue"/>: архівація звільняє партиції <c>doc.*</c>
/// через <c>TRUNCATE</c>.
/// </remarks>
public sealed class SourceEventLink : Entity<long>
{
    /// <summary>Довжина ID події — як <c>ext.EntityFieldMap.SourceField</c>.</summary>
    public const int MaxSourceEventIdLength = 200;

    /// <summary>Довжина назви події; довша обрізається — це підпис, а не ключ.</summary>
    public const int MaxEventNameLength = 400;

    /// <summary>Довжина ключа рядка — як <c>doc.TableRow.RowKey</c>.</summary>
    public const int MaxRowKeyLength = 100;

    // Лише в пам'яті: зв'язок, щойно створений FirstSeen…, ще не має стану, з
    // якого переходить. Прочитаний з бази (конструктор без параметрів) — має.
    private bool _isNew;

    private SourceEventLink() { }

    private SourceEventLink(int sourceEventMapId, SourceEventObservation observation, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.SourceEventId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            observation.SourceEventId.Length, MaxSourceEventIdLength, nameof(observation));

        SourceEventMapId = sourceEventMapId;
        SourceEventId = observation.SourceEventId;
        FirstSeenAt = nowUtc;
        _isNew = true;
    }

    /// <summary>Перша поява події, рядка якої ще немає.</summary>
    /// <param name="sourceEventMapId">Мапінг.</param>
    /// <param name="observation">Подія, як її дало джерело.</param>
    /// <param name="status">
    /// <see cref="SourceEventLinkStatus.Open"/>, <see cref="SourceEventLinkStatus.PeriodNotOpen"/>,
    /// <see cref="SourceEventLinkStatus.PeriodClosed"/> або <see cref="SourceEventLinkStatus.RowLimit"/>.
    /// </param>
    /// <param name="nowUtc">Час прогону.</param>
    /// <returns>Новий зв'язок без рядка.</returns>
    public static SourceEventLink FirstSeenUnwritten(
        int sourceEventMapId, SourceEventObservation observation, SourceEventLinkStatus status, DateTime nowUtc)
    {
        var link = new SourceEventLink(sourceEventMapId, observation, nowUtc);
        link.RecordUnwritten(observation, status, nowUtc);
        return link;
    }

    /// <summary>Перша поява події, для якої одразу записано рядок.</summary>
    /// <param name="sourceEventMapId">Мапінг.</param>
    /// <param name="observation">Подія; кінець обов'язковий.</param>
    /// <param name="row">Записаний рядок.</param>
    /// <param name="keptManualJson">Коди колонок, лишених за людиною; <c>null</c> — таких немає.</param>
    /// <param name="unmappedJson">Незіставлені значення; <c>null</c> — усе зіставлено.</param>
    /// <param name="nowUtc">Час прогону.</param>
    /// <returns>Новий зв'язок із рядком.</returns>
    public static SourceEventLink FirstSeenWritten(
        int sourceEventMapId,
        SourceEventObservation observation,
        SourceEventRowRef row,
        string? keptManualJson,
        string? unmappedJson,
        DateTime nowUtc)
    {
        var link = new SourceEventLink(sourceEventMapId, observation, nowUtc);
        link.RecordWritten(observation, row, keptManualJson, unmappedJson, nowUtc);
        return link;
    }

    public int SourceEventMapId { get; private set; }

    /// <summary>ID події в джерелі; унікальний у межах мапінгу (<c>UQ_SEL_Event</c>).</summary>
    public string SourceEventId { get; private set; } = null!;

    public int? PeriodKey { get; private set; }

    public long? TableInstanceId { get; private set; }

    public string? RowKey { get; private set; }

    public string? EventName { get; private set; }

    public DateTime StartUtc { get; private set; }

    public DateTime? EndUtc { get; private set; }

    public DateTime? SourceModifiedUtc { get; private set; }

    public SourceEventLinkStatus Status { get; private set; }

    /// <summary>Коди колонок, останню зміну яких зробила людина (D-118), — JSON-масив.</summary>
    public string? KeptManualJson { get; private set; }

    /// <summary>Значення без відповідника в довіднику — JSON <c>[{column, value}]</c>.</summary>
    public string? UnmappedJson { get; private set; }

    public DateTime FirstSeenAt { get; private set; }

    public DateTime LastSeenAt { get; private set; }

    public DateTime LastSyncAt { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Чи прив'язано до зв'язку рядок документа.</summary>
    public bool HasRow => TableInstanceId is not null;

    /// <summary>Чи дозволено перейти в <paramref name="to"/> (див. remarks класу).</summary>
    /// <param name="from">Поточний стан.</param>
    /// <param name="to">Цільовий стан.</param>
    /// <param name="hasRow">Чи має зв'язок рядок.</param>
    /// <returns><c>true</c> — перехід дозволено.</returns>
    public static bool IsTransitionAllowed(SourceEventLinkStatus from, SourceEventLinkStatus to, bool hasRow)
    {
        // Стани «рядок є» без рядка не бувають — такий вхід означає зіпсований запис.
        if (!hasRow && from is SourceEventLinkStatus.Missing or SourceEventLinkStatus.PeriodChanged
                        or SourceEventLinkStatus.Synced or SourceEventLinkStatus.Unmapped)
        {
            return false;
        }

        return IsTargetAllowed(to, hasRow);
    }

    private static bool IsTargetAllowed(SourceEventLinkStatus to, bool hasRow) => to switch
    {
        SourceEventLinkStatus.Open or SourceEventLinkStatus.PeriodNotOpen or SourceEventLinkStatus.RowLimit => !hasRow,
        SourceEventLinkStatus.Missing or SourceEventLinkStatus.PeriodChanged => hasRow,
        SourceEventLinkStatus.Synced or SourceEventLinkStatus.Unmapped or SourceEventLinkStatus.PeriodClosed => true,
        _ => false,
    };

    /// <summary>Подію побачено, рядка не записано.</summary>
    /// <param name="observation">Подія, як її дало джерело.</param>
    /// <param name="status">Чому рядка немає.</param>
    /// <param name="nowUtc">Час прогону.</param>
    /// <exception cref="DomainException">
    /// <c>ECR-INT-0422</c> <c>.eventLinkTransitionInvalid</c> — стан недоступний (див. remarks класу).
    /// </exception>
    public void RecordUnwritten(SourceEventObservation observation, SourceEventLinkStatus status, DateTime nowUtc)
    {
        if (status is not (SourceEventLinkStatus.Open or SourceEventLinkStatus.PeriodNotOpen
                or SourceEventLinkStatus.PeriodClosed or SourceEventLinkStatus.RowLimit))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Стан, у якому рядок не записується.");
        }

        // Відкрита подія — рівно та, що без кінця (§4.7.4, крок 3): «Open» із
        // кінцем чи «ще не записано» без кінця сплутали б реєстрові стан події.
        ArgumentNullException.ThrowIfNull(observation);
        if ((status == SourceEventLinkStatus.Open) != (observation.EndUtc is null))
        {
            throw new ArgumentException("Open — це подія без кінця, і лише вона.", nameof(observation));
        }

        MoveTo(status);
        Observe(observation, nowUtc);
        LastSyncAt = nowUtc;
    }

    /// <summary>Рядок записано (створено чи оновлено).</summary>
    /// <param name="observation">Подія; кінець обов'язковий.</param>
    /// <param name="row">Рядок; для зв'язку з рядком — той самий.</param>
    /// <param name="keptManualJson">Колонки, лишені за людиною; <c>null</c> — немає.</param>
    /// <param name="unmappedJson">Незіставлені значення; <c>null</c> — стан <c>Synced</c>, інакше <c>Unmapped</c>.</param>
    /// <param name="nowUtc">Час прогону.</param>
    /// <exception cref="DomainException">
    /// <c>ECR-INT-0422</c> <c>.eventLinkTransitionInvalid</c> — інший рядок для прив'язаної події.
    /// </exception>
    public void RecordWritten(
        SourceEventObservation observation,
        SourceEventRowRef row,
        string? keptManualJson,
        string? unmappedJson,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.RowKey);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(row.RowKey.Length, MaxRowKeyLength, nameof(row));

        // Подія без кінця не матеріалізується (§4.7.4, крок 3).
        if (observation.EndUtc is null)
        {
            throw new ArgumentException("Рядок записується лише для завершеної події.", nameof(observation));
        }

        var target = unmappedJson is null ? SourceEventLinkStatus.Synced : SourceEventLinkStatus.Unmapped;

        if (HasRow && (PeriodKey != row.PeriodKey || TableInstanceId != row.TableInstanceId
                       || !string.Equals(RowKey, row.RowKey, StringComparison.Ordinal)))
        {
            throw TransitionInvalid(target);
        }

        MoveTo(target);
        PeriodKey = row.PeriodKey;
        TableInstanceId = row.TableInstanceId;
        RowKey = row.RowKey;
        KeptManualJson = keptManualJson;
        UnmappedJson = unmappedJson;
        Observe(observation, nowUtc);
        LastSyncAt = nowUtc;
    }

    /// <summary>Початок прив'язаної події переїхав в інший період; рядок не переноситься.</summary>
    /// <param name="observation">Подія, як її дало джерело.</param>
    /// <param name="nowUtc">Час прогону.</param>
    /// <exception cref="DomainException">
    /// <c>ECR-INT-0422</c> <c>.eventLinkTransitionInvalid</c> — у зв'язку немає рядка.
    /// </exception>
    public void RecordPeriodChanged(SourceEventObservation observation, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(observation);

        MoveTo(SourceEventLinkStatus.PeriodChanged);
        Observe(observation, nowUtc);
        LastSyncAt = nowUtc;
    }

    /// <summary>
    /// Джерело при повному прочитанні вікна подію не повернуло; рядок не
    /// змінюється і не видаляється (§4.7.4, крок 6).
    /// </summary>
    /// <param name="nowUtc">Час прогону.</param>
    /// <exception cref="DomainException">
    /// <c>ECR-INT-0422</c> <c>.eventLinkTransitionInvalid</c> — у зв'язку немає рядка.
    /// </exception>
    public void MarkMissing(DateTime nowUtc)
    {
        MoveTo(SourceEventLinkStatus.Missing);
        LastSyncAt = nowUtc;
    }

    private void MoveTo(SourceEventLinkStatus target)
    {
        // Новий зв'язок (FirstSeen…) ще не має стану, з якого переходить: його
        // перший стан перевіряється лише правилом цілі для зв'язку без рядка.
        var allowed = _isNew
            ? IsTargetAllowed(target, hasRow: false)
            : IsTransitionAllowed(Status, target, HasRow);
        if (!allowed)
        {
            throw TransitionInvalid(target);
        }

        Status = target;
        _isNew = false;
    }

    private void Observe(SourceEventObservation observation, DateTime nowUtc)
    {
        EventName = observation.EventName is { Length: > MaxEventNameLength } name
            ? name[..MaxEventNameLength]
            : observation.EventName;
        StartUtc = observation.StartUtc;
        EndUtc = observation.EndUtc;
        SourceModifiedUtc = observation.SourceModifiedUtc;
        LastSeenAt = nowUtc;
    }

    // ⚠ Стани — рядками: резолвер каталогу підставляє лише string.
    private DomainException TransitionInvalid(SourceEventLinkStatus target)
    {
        var from = _isNew ? "None" : Status.ToString();

        return new(
            "ECR-INT-0422",
            $"Зв'язок події «{SourceEventId}» не може перейти зі стану {from} у {target}"
            + (HasRow ? $" (рядок {RowKey})." : " (рядка немає)."),
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-INT-0422.eventLinkTransitionInvalid",
                ["sourceEventId"] = SourceEventId,
                ["from"] = from,
                ["to"] = target.ToString(),
            });
    }
}
