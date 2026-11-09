using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Виняток «закритий місяць відкрито для ОДНОГО документа» (<c>doc.DocumentPeriodReopen</c>; приймальна 08.11 (i),
/// слово людини: «якщо мені потрібно відкрити тільки для одного документу?»).
/// </summary>
/// <remarks>
/// ⛔ Період лишається <see cref="PeriodState.Closed"/>: решта документів місяця закрита, як і була. Діючий виняток
/// (<see cref="IsActiveAt"/>) лише піднімає ЕФЕКТИВНИЙ стан періоду для цього документа до
/// <see cref="PeriodState.Grace"/> (<see cref="Services.DocumentPeriodReopenRule"/>) — тим самим правилом, що й
/// перевідкритий період (D-70: правка пізня, <c>IsLateEdit</c>).
/// <para>
/// ⚠ Строк діє сам: після <see cref="OpenUntil"/> виняток уже не діє в жодному рішенні, навіть до того, як
/// <c>PeriodStateJob</c> позначить його закритим (<see cref="Close"/>, запис у журнал).
/// </para>
/// </remarks>
public sealed class DocumentPeriodReopen : Entity<int>
{
    /// <summary>Найбільша довжина причини — як у <c>ReopenReason</c> стану аркуша.</summary>
    public const int MaxReasonLength = 400;

    /// <summary>Найбільша довжина причини закриття.</summary>
    public const int MaxCloseReasonLength = 100;

    /// <summary>Причина закриття: строк минув (закриває <c>PeriodStateJob</c>).</summary>
    public const string ClosedExpired = "Expired";

    /// <summary>Причина закриття: відкрито новий виняток того самого документа й місяця.</summary>
    public const string ClosedReplaced = "Replaced";

    /// <summary>Причина закриття: людина закрила виняток до строку («Close now» на вкладці Periods, приймальна i4).</summary>
    public const string ClosedManually = "Closed";

    private DocumentPeriodReopen() { }

    /// <summary>Документ.</summary>
    public long DocumentId { get; private set; }

    /// <summary>Місяць (<c>PeriodKey</c>, РРРРММ).</summary>
    public int PeriodKeyValue { get; private set; }

    /// <summary>До якого моменту (UTC) документ відкрито.</summary>
    public DateTime OpenUntil { get; private set; }

    /// <summary>Причина відкриття; обов'язкова.</summary>
    public string Reason { get; private set; } = string.Empty;

    /// <summary>Хто відкрив.</summary>
    public int OpenedByUserId { get; private set; }

    /// <summary>Коли відкрито (UTC).</summary>
    public DateTime OpenedAt { get; private set; }

    /// <summary>Коли закрито (UTC); <c>null</c> — ще не позначено закритим.</summary>
    public DateTime? ClosedAt { get; private set; }

    /// <summary>Хто закрив; <c>null</c> — система (строк минув) або ще не закрито.</summary>
    public int? ClosedByUserId { get; private set; }

    /// <summary>Чому закрито: <see cref="ClosedExpired"/>, <see cref="ClosedReplaced"/>, <see cref="ClosedManually"/>.</summary>
    public string? CloseReason { get; private set; }

    /// <summary>Версія рядка.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Відкриває закритий місяць для документа.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Місяць.</param>
    /// <param name="openUntil">До якого моменту; має бути в майбутньому.</param>
    /// <param name="reason">Причина; обов'язкова.</param>
    /// <param name="userId">Хто відкриває.</param>
    /// <param name="utcNow">Поточний момент.</param>
    /// <exception cref="DomainException">Причина порожня чи задовга, або строк не в майбутньому (<c>ECR-PRD-0422</c>).</exception>
    public static DocumentPeriodReopen Open(
        long documentId, int periodKey, DateTime openUntil, string reason, int userId, DateTime utcNow)
    {
        // Ті самі відмови й ключі, що в `Period.Reopen`: дія та сама, лише вужча.
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException(
                "ECR-PRD-0422", "Причина відкриття обов'язкова.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-PRD-0422.reopenReasonRequired" });
        }

        var trimmed = reason.Trim();
        if (trimmed.Length > MaxReasonLength)
        {
            throw new DomainException(
                "ECR-PRD-0422", $"Причина довша за {MaxReasonLength} символів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-0422.reopenReasonTooLong",
                    ["max"] = MaxReasonLength,
                });
        }

        var deadline = AsUtc(openUntil);
        if (deadline <= utcNow)
        {
            throw new DomainException(
                "ECR-PRD-0422",
                $"Строк відкриття {deadline:O} не пізніший за поточний момент {utcNow:O}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-0422.reopenUntilInPast",
                    ["until"] = deadline.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        return new DocumentPeriodReopen
        {
            DocumentId = documentId,
            PeriodKeyValue = periodKey,
            OpenUntil = deadline,
            Reason = trimmed,
            OpenedByUserId = userId,
            OpenedAt = utcNow,
        };
    }

    /// <summary>Чи діє виняток у момент <paramref name="utcNow"/>: не закритий і строк ще не минув.</summary>
    /// <param name="utcNow">Момент.</param>
    public bool IsActiveAt(DateTime utcNow) => ClosedAt is null && OpenUntil > utcNow;

    /// <summary>Позначає виняток закритим.</summary>
    /// <param name="utcNow">Момент закриття.</param>
    /// <param name="userId">Хто закрив; <c>null</c> — система.</param>
    /// <param name="reason">Причина закриття.</param>
    /// <remarks>Повторне закриття нічого не змінює: перша відмітка — доказ, і друга її не переписує.</remarks>
    public void Close(DateTime utcNow, int? userId, string reason)
    {
        if (ClosedAt is not null)
        {
            return;
        }

        ClosedAt = utcNow;
        ClosedByUserId = userId;
        CloseReason = reason.Length > MaxCloseReasonLength ? reason[..MaxCloseReasonLength] : reason;
    }

    /// <summary>Приводить момент до UTC за його <see cref="DateTime.Kind"/> — як <c>Period.Reopen</c>.</summary>
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
