using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Звітний період. Стан — **збережене значення**, яке рахує
/// <c>PeriodStateJob</c>, а не функція від <c>now()</c> у запиті (ФВ-1.12).
/// </summary>
public sealed class Period : Entity<int>
{
    private Period() { }

    public Period(int projectId, PeriodKey periodKey, byte sequence, DateOnly start, DateOnly end)
    {
        ProjectId = projectId;
        PeriodKeyValue = periodKey.Value;
        Sequence = sequence;
        PeriodStart = start;
        PeriodEnd = end;
        State = PeriodState.Scheduled;
    }

    public int ProjectId { get; private set; }

    /// <summary><c>Year*100 + Sequence</c> — ключ партиціонування (R-A6).</summary>
    public int PeriodKeyValue { get; private set; }

    public byte Sequence { get; private set; }
    public DateOnly PeriodStart { get; private set; }
    public DateOnly PeriodEnd { get; private set; }
    public PeriodState State { get; private set; }

    /// <summary>Обчислені межі — денормалізація, щоб перевірка доступу не рахувала offsets щоразу.</summary>
    public DateTime ComputedOpenAt { get; private set; }
    public DateTime ComputedGraceAt { get; private set; }
    public DateTime ComputedCloseAt { get; private set; }

    /// <summary>Тимчасове відкриття адміністратором.</summary>
    public DateTime? ReopenedUntil { get; private set; }
    public string? ReopenReason { get; private set; }
    public DateTime StateChangedAt { get; private set; }

    /// <summary>Ключ періоду як значеннєвий тип.</summary>
    public PeriodKey Key => new(PeriodKeyValue);

    /// <summary>Чи дозволене редагування в поточному стані.</summary>
    public bool AllowsEditing => State is PeriodState.Open or PeriodState.Grace;

    /// <summary>Чи є зміна пізньою — потребує позначки <c>IsLateEdit</c> (D-70).</summary>
    public bool IsLateEditWindow => State == PeriodState.Grace;

    /// <summary>Перераховує межі за політикою в поясі майданчика.</summary>
    public void RecomputeBoundaries(PeriodPolicy policy, TimeZoneInfo siteTimeZone)
        => throw new NotImplementedException(
            "TODO: обчислити ComputedOpenAt = PeriodStart + OpenOffsetDays, " +
            "ComputedGraceAt = PeriodEnd + 1 день, ComputedCloseAt = PeriodEnd + HardCloseOffsetDays; " +
            "усі — опівночі В ПОЯСІ МАЙДАНЧИКА, потім TimeZoneInfo.ConvertTimeToUtc (D-68). " +
            "DateTime.Now використовувати заборонено.");

    /// <summary>Переводить у новий стан. Викликає лише <c>PeriodStateJob</c>.</summary>
    public void TransitionTo(PeriodState state, DateTime utcNow)
        => throw new NotImplementedException(
            "TODO: перевірити допустимість переходу (Scheduled→Open→Grace→Closed, " +
            "назад лише Closed→Grace через Reopen); присвоїти State і StateChangedAt.");

    /// <summary>Відкриває закритий період до вказаного моменту. Причина обов'язкова.</summary>
    public void Reopen(DateTime until, string reason, DateTime utcNow)
        => throw new NotImplementedException(
            "TODO: перевірити State == Closed і непорожню причину; State = Grace, " +
            "ReopenedUntil = until, ReopenReason = reason.");
}
