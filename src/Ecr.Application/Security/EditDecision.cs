// src/Ecr.Application/Security/EditDecision.cs

using Ecr.Domain.Enums;

namespace Ecr.Application.Security;

/// <summary>
/// Рішення про доступ. Повертає <b>причину</b>, а не <c>bool</c>: користувач має
/// розуміти, чому комірка сіра, інакше він піде до адміністратора, а той —
/// до розробника.
/// </summary>
/// <param name="IsAllowed">Чи дозволена дія.</param>
/// <param name="Reason">Причина відмови; <see cref="EditDenyReason.None"/> при дозволі.</param>
/// <param name="Detail">Уточнення для UI (напр. дата закриття періоду). Не для логіки.</param>
/// <param name="RequiresConfirmation">
/// Дозволено, але лише після ЯВНОГО підтвердження оператора
/// (<c>ФВ-2.16</c>, <c>AllowWithConfirmation</c>, <c>#43</c>). Завжди
/// <c>false</c> при відмові: підтверджувати нема чого, коли дія й так
/// заборонена.
/// </param>
public readonly record struct EditDecision(
    bool IsAllowed, EditDenyReason Reason, string? Detail = null, bool RequiresConfirmation = false)
{
    public static EditDecision Allow() => new(true, EditDenyReason.None);
    public static EditDecision Deny(EditDenyReason reason, string? detail = null) => new(false, reason, detail);

    /// <summary>
    /// Дозволено за умови підтвердження (<c>ФВ-2.16</c>, <c>#43</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Не <see cref="Deny"/>: комірка лишається редаговною і без цього
    /// підтвердження — заборона тут перетворила б <c>AllowWithConfirmation</c>
    /// на <c>ReadOnly</c>, і дві з трьох поведінок <c>ФВ-2.16</c> знову
    /// злилися б в одну.
    /// </remarks>
    public static EditDecision AllowWithConfirmation(string? detail)
        => new(true, EditDenyReason.None, detail, RequiresConfirmation: true);
}
