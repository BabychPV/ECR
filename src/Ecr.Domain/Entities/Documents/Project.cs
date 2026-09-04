using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>Проєкт — звітний рік або інший діапазон.</summary>
public sealed class Project : Entity<int>
{
    private readonly List<Period> _periods = [];

    private Project() { }

    public Project(EcrCode code, LocalizedText name, DateOnly periodStart, DateOnly periodEnd,
                   int templateVersionId, PeriodKind periodKind, int periodPolicyId, string timeZoneId)
    {
        Code = code.Value;
        NameL10n = name;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        TemplateVersionId = templateVersionId;
        PeriodKind = periodKind;
        PeriodPolicyId = periodPolicyId;
        TimeZoneId = timeZoneId;
        Status = ProjectStatus.Draft;
        CurrentPeriodMode = CurrentPeriodMode.Auto;
        YearGraceOffsetDays = 45;
    }

    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>Джерело істини про межі проєкту.</summary>
    public DateOnly PeriodStart { get; private set; }
    public DateOnly PeriodEnd { get; private set; }

    /// <summary>Лише підпис для UI. **Не ідентичність.**</summary>
    public short? Year { get; private set; }

    public string? TagsJson { get; private set; }
    public int TemplateVersionId { get; private set; }
    public PeriodKind PeriodKind { get; private set; }
    public int PeriodPolicyId { get; private set; }
    public int YearGraceOffsetDays { get; private set; }

    /// <summary>
    /// Пояс майданчика. У ньому рахуються межі періодів, offsets і
    /// <c>IsLateEdit</c> — не в UTC (D-68).
    /// </summary>
    public string TimeZoneId { get; private set; } = null!;

    /// <summary>Режим визначення поточного періоду (D-77).</summary>
    public CurrentPeriodMode CurrentPeriodMode { get; private set; }

    /// <summary>Поточний звітний період — **наша конфігурація**, а не значення з AF.</summary>
    public int? CurrentPeriodId { get; private set; }

    public string? CurrentPeriodPinnedReason { get; private set; }
    public DateTime? CurrentPeriodChangedAt { get; private set; }
    public int? CurrentPeriodChangedByUserId { get; private set; }

    /// <summary>Разові налаштування, зібрані ззовні при створенні. **Не постійна залежність.**</summary>
    public string? ExternalSettingsJson { get; private set; }

    public ProjectStatus Status { get; private set; }

    /// <summary>Прапорець виконання архівації: читання має брати джерело за станом, не за датою.</summary>
    public bool IsArchiving { get; private set; }

    public DateTime? ClosedAt { get; private set; }
    public int? ClosedByUserId { get; private set; }

    public IReadOnlyList<Period> Periods => _periods;

    /// <summary>
    /// Фіксує поточний період вручну. Причина обов'язкова: стан неочевидний
    /// і має бути видимим в UI.
    /// </summary>
    public void PinCurrentPeriod(int periodId, string reason, int userId, DateTime utcNow)
        => throw new NotImplementedException(
            "TODO: перевірити, що period належить цьому проєкту і reason не порожній; " +
            "виставити CurrentPeriodMode = Pinned, CurrentPeriodId, причину і аудит-поля.");

    /// <summary>Повертає автоматичне визначення поточного періоду.</summary>
    public void UnpinCurrentPeriod(int userId, DateTime utcNow)
        => throw new NotImplementedException(
            "TODO: CurrentPeriodMode = Auto; CurrentPeriodPinnedReason = null; " +
            "CurrentPeriodId лишити — його перерахує PeriodStateJob.");

    /// <summary>Оновлює поточний період у режимі <c>Auto</c>. Викликає лише <c>PeriodStateJob</c>.</summary>
    public void SetCurrentPeriodAutomatically(int? periodId, DateTime utcNow)
        => throw new NotImplementedException(
            "TODO: якщо CurrentPeriodMode = Pinned — нічого не робити (ручний пін має пріоритет); " +
            "інакше присвоїти CurrentPeriodId і CurrentPeriodChangedAt.");

    /// <summary>Перевіряє, що дата належить проєкту (ФВ-1.11).</summary>
    public bool ContainsDate(DateOnly date) => date >= PeriodStart && date <= PeriodEnd;
}
