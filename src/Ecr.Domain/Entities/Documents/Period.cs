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
    /// <param name="policy">Політика зі зсувами відкриття, grace і жорсткого закриття.</param>
    /// <param name="siteTimeZone">Пояс майданчика.</param>
    /// <remarks>
    /// ⚠ Межі рахуються в ПОЯСІ МАЙДАНЧИКА, а не в UTC (D-68). «Останній день
    /// періоду» для користувача закінчується опівночі його часу; у UTC це вже
    /// наступна доба, і період закрився б на кілька годин раніше, ніж усі
    /// очікують — рівно тоді, коли всі дозаповнюють форми.
    /// </remarks>
    public void RecomputeBoundaries(PeriodPolicy policy, TimeZoneInfo siteTimeZone)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(siteTimeZone);

        ComputedOpenAt = ToUtc(PeriodStart.AddDays(policy.OpenOffsetDays), siteTimeZone);

        // Grace починається наступної доби після кінця періоду: сам останній
        // день ще належить періоду цілком.
        ComputedGraceAt = ToUtc(PeriodEnd.AddDays(1), siteTimeZone);
        ComputedCloseAt = ToUtc(PeriodEnd.AddDays(policy.HardCloseOffsetDays), siteTimeZone);
    }

    /// <summary>Опівніч указаної дати в поясі майданчика, переведена в UTC.</summary>
    private static DateTime ToUtc(DateOnly date, TimeZoneInfo siteTimeZone)
        => TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified),
            siteTimeZone);

    /// <summary>Переводить у новий стан. Викликає лише <c>PeriodStateJob</c>.</summary>
    /// <param name="state">Новий стан.</param>
    /// <param name="utcNow">Момент переходу.</param>
    /// <exception cref="DomainException">Перехід недопустимий.</exception>
    public void TransitionTo(PeriodState state, DateTime utcNow)
    {
        if (state == State)
        {
            return;
        }

        // ⚠ Дозволені лише переходи вперед. Назад — виключно `Closed → Grace`
        // і виключно через Reopen: інакше збій задачі станів міг би тихо
        // «відкрити» закритий період, а це право адміністратора з причиною.
        var allowed = State switch
        {
            PeriodState.Scheduled => state is PeriodState.Open,
            PeriodState.Open => state is PeriodState.Grace or PeriodState.Closed,
            PeriodState.Grace => state is PeriodState.Closed,
            _ => false,
        };

        if (!allowed)
        {
            throw new DomainException(
                "ECR-PRD-0409",
                $"Перехід періоду {PeriodKeyValue} зі стану {State} у {state} не допускається.");
        }

        State = state;
        StateChangedAt = utcNow;
    }

    /// <summary>Відкриває закритий період до вказаного моменту. Причина обов'язкова.</summary>
    /// <param name="until">До якого моменту діє тимчасове відкриття.</param>
    /// <param name="reason">Причина; зберігається і потрапляє в аудит.</param>
    /// <param name="utcNow">Момент операції.</param>
    /// <exception cref="DomainException">Період не закритий або причина порожня.</exception>
    public void Reopen(DateTime until, string reason, DateTime utcNow)
    {
        if (State != PeriodState.Closed)
        {
            throw new DomainException(
                "ECR-PRD-0409",
                $"Відкривати можна лише закритий період; поточний стан — {State}.");
        }

        // Причина обов'язкова і тут, і в базі: відкриття закритого періоду —
        // подія, за яку хтось відповідає, а без причини в журналі лишиться
        // сам факт без відповіді на «чому».
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("ECR-PRD-0422", "Причина відкриття періоду обов'язкова.");
        }

        State = PeriodState.Grace;
        ReopenedUntil = until;
        ReopenReason = reason;
        StateChangedAt = utcNow;
    }
}
