// src/Ecr.Application/Security/EditDecision.cs
namespace Ecr.Application.Security;

using Ecr.Domain.Enums;

/// <summary>
/// Рішення про доступ. Повертає <b>причину</b>, а не <c>bool</c>: користувач має
/// розуміти, чому комірка сіра, інакше він піде до адміністратора, а той —
/// до розробника.
/// </summary>
/// <param name="IsAllowed">Чи дозволена дія.</param>
/// <param name="Reason">Причина відмови; <see cref="EditDenyReason.None"/> при дозволі.</param>
/// <param name="Detail">Уточнення для UI (напр. дата закриття періоду). Не для логіки.</param>
public readonly record struct EditDecision(bool IsAllowed, EditDenyReason Reason, string? Detail = null)
{
    public static EditDecision Allow() => new(true, EditDenyReason.None);
    public static EditDecision Deny(EditDenyReason reason, string? detail = null) => new(false, reason, detail);
}
