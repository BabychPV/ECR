// src/Ecr.Domain/Entities/Calculations/RecalculationApproval.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Погодження перерахунку закритого періоду (ФВ-9.7, <c>calc.RecalculationApproval</c>).
/// </summary>
/// <remarks>
/// ⛔ Аудит безпеки S1. Доти «друга людина» була полем <c>approvedByUserId</c>
/// у тілі запиту перерахунку: ініціатор вписував будь-кого. Тепер погодження —
/// окремий запис: створює ініціатор, підтверджує ІНША людина власною сесією,
/// використати можна один раз, лише ініціатору, лише для цього проєкту й
/// періоду, і не пізніше <see cref="ExpiresAt"/>.
/// <para>
/// ⚠ Підтвердження й використання — умовні <c>UPDATE</c> у сховищі
/// (<c>WHERE … IS NULL</c>), а не методи сутності: два паралельні запити інакше
/// обидва прочитали б «ще не використано» і обидва пройшли б.
/// </para>
/// </remarks>
public sealed class RecalculationApproval : Entity<long>
{
    /// <summary>Скільки живе погодження від створення.</summary>
    /// <remarks>ТЗ строку не задає; доба — судження: погодження на «колись» — це вже не погодження.</remarks>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>Найбільша довжина причини; та сама, що в стовпці.</summary>
    public const int ReasonMaxLength = 1000;

    private RecalculationApproval() { }

    /// <summary>Створює запит на погодження.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKey">Єдиний період, який погодження відкриває.</param>
    /// <param name="reason">Причина; обов'язкова.</param>
    /// <param name="requestedByUserId">Ініціатор — єдиний, хто зможе запустити перерахунок.</param>
    /// <param name="utcNow">Момент створення.</param>
    public RecalculationApproval(int projectId, int periodKey, string reason, int requestedByUserId, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(reason.Length, ReasonMaxLength);

        ProjectId = projectId;
        PeriodKey = periodKey;
        Reason = reason;
        RequestedByUserId = requestedByUserId;
        RequestedAt = utcNow;
        ExpiresAt = utcNow + Lifetime;
    }

    public int ProjectId { get; private set; }
    public int PeriodKey { get; private set; }
    public string Reason { get; private set; } = null!;
    public int RequestedByUserId { get; private set; }
    public DateTime RequestedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }

    /// <summary>Хто підтвердив; <c>null</c> — ще чекає. Ніколи не ініціатор (CHECK у базі).</summary>
    public int? ConfirmedByUserId { get; private set; }

    public DateTime? ConfirmedAt { get; private set; }

    /// <summary>Коли використано для перерахунку; після цього погодження мертве.</summary>
    public DateTime? UsedAt { get; private set; }
}
