using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// ✎ B75: блокування рядка таблиці документа на проміжок часу (<c>doc.RowLock</c>; рішення людини 13.11: «власник
/// обирає рядки й дати — їх ніхто, крім нього, не змінює; знімається само після кінцевої дати або власником»).
/// </summary>
/// <remarks>
/// ⛔ Рядок ідентифікується <c>(DocumentId, PeriodKey, SheetCopyId, TableCode, RowKey)</c>, а не <c>TableRow.Id</c>:
/// ключ переживає правку комірок, переупорядкування й перенос версії шаблону (код таблиці й ключ рядка зберігаються).
/// <para>
/// ⚠ Діє, поки <c>LockFrom ≤ now &lt; LockUntil</c> і не знято (<see cref="IsActiveAt"/>) — рахується на читанні, як
/// строк передачі; задачі немає. Зняте й прострочене лишається історією.
/// </para>
/// </remarks>
public sealed class RowLock : Entity<long>
{
    /// <summary>Найбільша довжина коду таблиці — як у <c>cfg.TableDef.Code</c>.</summary>
    public const int MaxTableCodeLength = 64;

    /// <summary>Найбільша довжина ключа рядка — як у <c>doc.TableRow.RowKey</c>.</summary>
    public const int MaxRowKeyLength = 100;

    /// <summary>Найбільша довжина примітки.</summary>
    public const int MaxNoteLength = 400;

    private RowLock() { }

    /// <summary>Документ.</summary>
    public long DocumentId { get; private set; }

    /// <summary>Період екземпляра таблиці (<c>PeriodKey</c>).</summary>
    public int PeriodKeyValue { get; private set; }

    /// <summary>Копія аркуша (RS); <c>null</c> — оригінал.</summary>
    public long? SheetCopyId { get; private set; }

    /// <summary>Код таблиці шаблону.</summary>
    public string TableCode { get; private set; } = string.Empty;

    /// <summary>Ключ рядка.</summary>
    public string RowKey { get; private set; } = string.Empty;

    /// <summary>Початок дії (UTC, включно).</summary>
    public DateTime LockFrom { get; private set; }

    /// <summary>Кінець дії (UTC, виключно).</summary>
    public DateTime LockUntil { get; private set; }

    /// <summary>Хто заблокував — власник блокування (лише він править рядок, поки блокування діє, Q1).</summary>
    public int OwnerUserId { get; private set; }

    /// <summary>Примітка; <c>null</c> — немає.</summary>
    public string? Note { get; private set; }

    /// <summary>Коли створено (UTC).</summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>Коли знято (UTC); <c>null</c> — не знято.</summary>
    public DateTime? ReleasedAt { get; private set; }

    /// <summary>Хто зняв; <c>null</c> — не знято.</summary>
    public int? ReleasedByUserId { get; private set; }

    /// <summary>Версія рядка.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Чи діє блокування на момент <paramref name="utcNow"/>.</summary>
    /// <param name="utcNow">Момент (UTC).</param>
    public bool IsActiveAt(DateTime utcNow) => ReleasedAt is null && LockFrom <= utcNow && utcNow < LockUntil;

    /// <summary>Створює блокування.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період екземпляра.</param>
    /// <param name="sheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
    /// <param name="tableCode">Код таблиці.</param>
    /// <param name="rowKey">Ключ рядка.</param>
    /// <param name="lockFrom">Початок (UTC, включно).</param>
    /// <param name="lockUntil">Кінець (UTC, виключно); пізніший за початок і за <paramref name="utcNow"/>.</param>
    /// <param name="ownerUserId">Хто блокує.</param>
    /// <param name="note">Примітка.</param>
    /// <param name="utcNow">Поточний момент.</param>
    /// <exception cref="DomainException">Порожній проміжок або кінець у минулому (<c>ECR-REQ-0422</c>, <c>lockRangeInvalid</c>).</exception>
    public static RowLock Create(
        long documentId, int periodKey, long? sheetCopyId, string tableCode, string rowKey,
        DateTime lockFrom, DateTime lockUntil, int ownerUserId, string? note, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(rowKey);

        if (lockUntil <= lockFrom || lockUntil <= utcNow)
        {
            throw new DomainException(
                "ECR-REQ-0422",
                $"Проміжок блокування [{lockFrom:O}, {lockUntil:O}) порожній або вже минув.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.lockRangeInvalid" });
        }

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is { Length: > MaxNoteLength })
        {
            throw new DomainException(
                "ECR-REQ-0422",
                $"Примітка довша за {MaxNoteLength} символів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.lockNoteTooLong",
                    ["max"] = MaxNoteLength,
                });
        }

        return new RowLock
        {
            DocumentId = documentId,
            PeriodKeyValue = periodKey,
            SheetCopyId = sheetCopyId,
            TableCode = tableCode,
            RowKey = rowKey,
            LockFrom = DateTime.SpecifyKind(lockFrom, DateTimeKind.Utc),
            LockUntil = DateTime.SpecifyKind(lockUntil, DateTimeKind.Utc),
            OwnerUserId = ownerUserId,
            Note = trimmed,
            CreatedAt = utcNow,
        };
    }

    /// <summary>Знімає блокування (не видаляє: історія лишається).</summary>
    /// <param name="userId">Хто знімає.</param>
    /// <param name="utcNow">Момент.</param>
    /// <returns><c>false</c> — вже знято раніше.</returns>
    public bool Release(int userId, DateTime utcNow)
    {
        if (ReleasedAt is not null)
        {
            return false;
        }

        ReleasedAt = utcNow;
        ReleasedByUserId = userId;
        return true;
    }
}
