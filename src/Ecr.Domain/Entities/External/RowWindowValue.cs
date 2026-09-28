// src/Ecr.Domain/Entities/External/RowWindowValue.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.External;

/// <summary>Хто порахував значення вікна (як <c>Ecr.Application.Ports.WindowComputedBy</c>).</summary>
public enum RowWindowComputedBy : byte
{
    /// <summary>Локальна згортка сирих точок — еталон (V-3).</summary>
    Local = 0,

    /// <summary>Summary джерела за налаштованим запитом.</summary>
    Server = 1,
}

/// <summary>Чим закінчилося одне підтягування вікна рядка (HSE301 §4.4).</summary>
/// <remarks>
/// ⚠ У базі — текстом (<c>nvarchar(32)</c>, <c>CK_RWV_Status</c>): журнал читають
/// і люди запитом із SSMS, а число статусу там нічого не каже.
/// </remarks>
public enum RowWindowValueStatus : byte
{
    /// <summary>Значення підтягнуто й записано, покриття не нижче порога.</summary>
    Fetched = 0,

    /// <summary>Записано, але покриття нижче <c>MinPercentGood</c>.</summary>
    Partial = 1,

    /// <summary>У вікні немає придатних точок; комірка не змінюється.</summary>
    NoData = 2,

    /// <summary>Комірку востаннє правила людина — значення не перезаписано (D-118).</summary>
    KeptManual = 3,

    /// <summary>Джерело відмовило; код — у <see cref="RowWindowValue.ErrorCode"/>.</summary>
    SourceError = 4,

    /// <summary><c>End ≤ Start</c>, порожня межа чи вікно довше 32 діб; до джерела не йшли.</summary>
    InvalidWindow = 5,

    /// <summary>Для значення селектора немає джерела (пілот без PI).</summary>
    NotApplicable = 6,
}

/// <summary>
/// Провенанс одного підтягування значення за вікном рядка
/// (<c>ext.RowWindowValue</c>, HSE301 §4.4, §8.3).
/// </summary>
/// <remarks>
/// ⛔ Таблиця партиційована за <see cref="PeriodKey"/> (<c>ps_ByPeriodKey</c>,
/// <c>07-partition-tables.sql</c>): рядків тут стільки ж, скільки подій × колонок
/// × повторів, і вони належать періоду так само, як комірки, які пояснюють.
///
/// ⚠ Зовнішнього ключа на <c>doc.TableInstance</c> НЕМАЄ навмисно:
/// <c>arc.usp_ArchiveYear</c> звільняє партиції <c>doc.*</c> через
/// <c>TRUNCATE … WITH (PARTITIONS)</c>, а SQL Server відмовляє в <c>TRUNCATE</c>
/// таблиці, на яку посилається будь-який ключ. Процедура знімає й повертає
/// лише ключі, які знає (<c>FK_TableRow_Instance</c>, <c>FK_CellValue_Row</c>).
///
/// ⚠ Запис лише додається: нове підтягування тієї самої комірки знімає з
/// попереднього <see cref="IsCurrent"/> (<see cref="Supersede"/>), а не
/// переписує його — інакше історія «чому в комірці це число» губилася б.
/// </remarks>
public sealed class RowWindowValue : Entity<long>
{
    private RowWindowValue() { }

    /// <summary>Фіксує підтягування.</summary>
    /// <param name="periodKey">Період екземпляра таблиці — він же ключ партиції.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="rowKey">Ключ рядка, з якого взято вікно.</param>
    /// <param name="columnDefId">Колонка-ціль.</param>
    /// <param name="rowWindowMapId">Прив'язка.</param>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="sourceField">Шлях атрибута.</param>
    /// <param name="fromUtc">Початок вікна, UTC, включно.</param>
    /// <param name="toUtc">Кінець вікна, UTC, виключно.</param>
    /// <param name="summary">Спосіб згортки.</param>
    /// <param name="targetUnitId">Одиниця колонки.</param>
    /// <param name="retrievedAt">Коли прочитано.</param>
    public RowWindowValue(
        int periodKey,
        long tableInstanceId,
        string rowKey,
        int columnDefId,
        int rowWindowMapId,
        int sourceEntityId,
        string sourceField,
        DateTime fromUtc,
        DateTime toUtc,
        RowWindowSummaryKind summary,
        int targetUnitId,
        DateTime retrievedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rowKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceField);

        PeriodKey = periodKey;
        TableInstanceId = tableInstanceId;
        RowKey = rowKey;
        ColumnDefId = columnDefId;
        RowWindowMapId = rowWindowMapId;
        SourceEntityId = sourceEntityId;
        SourceField = sourceField;
        FromUtc = fromUtc;
        ToUtc = toUtc;
        Summary = summary;
        TargetUnitId = targetUnitId;
        RetrievedAt = retrievedAt;
        IsCurrent = true;
    }

    /// <summary>Ключ партиції; первинний ключ — <c>(PeriodKey, Id)</c>.</summary>
    public int PeriodKey { get; private set; }

    public long TableInstanceId { get; private set; }
    public string RowKey { get; private set; } = null!;
    public int ColumnDefId { get; private set; }
    public int RowWindowMapId { get; private set; }
    public int SourceEntityId { get; private set; }
    public string SourceField { get; private set; } = null!;
    public DateTime FromUtc { get; private set; }
    public DateTime ToUtc { get; private set; }
    public RowWindowSummaryKind Summary { get; private set; }
    public RowWindowComputedBy ComputedBy { get; private set; }

    /// <summary>Згорнуте значення в одиниці джерела; <c>null</c> — згортати не було чого.</summary>
    public decimal? ValueSource { get; private set; }

    public string? SourceUnitSymbol { get; private set; }

    /// <summary>Значення в одиниці колонки — те, що лягло (чи мало лягти) в комірку.</summary>
    public decimal? ValueTarget { get; private set; }

    public int TargetUnitId { get; private set; }

    /// <summary>Множник межі (§4.2); для <c>Total</c> — разом із переходом «× с».</summary>
    public decimal? ConversionFactor { get; private set; }

    public int PointCount { get; private set; }

    /// <summary>Покрита даними частка вікна, 0–100; <c>null</c> — для згорток точок.</summary>
    public decimal? PercentGood { get; private set; }

    public RowWindowValueStatus Status { get; private set; }
    public string? ErrorCode { get; private set; }
    public DateTime RetrievedAt { get; private set; }

    /// <summary>Чинний запис комірки; попередні лишаються історією.</summary>
    public bool IsCurrent { get; private set; }

    /// <summary>Записує результат згортки й конверсії.</summary>
    /// <param name="status">Підсумок.</param>
    /// <param name="computedBy">Хто порахував.</param>
    /// <param name="valueSource">Значення в одиниці джерела.</param>
    /// <param name="sourceUnitSymbol">UOM джерела.</param>
    /// <param name="valueTarget">Значення в одиниці колонки.</param>
    /// <param name="conversionFactor">Множник межі.</param>
    /// <param name="pointCount">Придатних точок у вікні.</param>
    /// <param name="percentGood">Покриття 0–100.</param>
    /// <param name="errorCode">Код відмови джерела.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Покриття поза 0–100 або від'ємна кількість точок.
    /// </exception>
    public void Record(
        RowWindowValueStatus status,
        RowWindowComputedBy computedBy,
        decimal? valueSource,
        string? sourceUnitSymbol,
        decimal? valueTarget,
        decimal? conversionFactor,
        int pointCount,
        decimal? percentGood,
        string? errorCode)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pointCount);
        if (percentGood is < 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(percentGood), percentGood, "Покриття — частка 0–100.");
        }

        Status = status;
        ComputedBy = computedBy;
        ValueSource = valueSource;
        SourceUnitSymbol = sourceUnitSymbol;
        ValueTarget = valueTarget;
        ConversionFactor = conversionFactor;
        PointCount = pointCount;

        // Колонка — decimal(5,2): округлення тут, явно, а не мовчки драйвером.
        PercentGood = percentGood is { } good ? decimal.Round(good, 2, MidpointRounding.AwayFromZero) : null;
        ErrorCode = errorCode;
    }

    /// <summary>Знімає з запису статус чинного: комірку пояснює новіше підтягування.</summary>
    public void Supersede() => IsCurrent = false;
}
