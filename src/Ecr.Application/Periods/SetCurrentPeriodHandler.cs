// src/Ecr.Application/Periods/SetCurrentPeriodHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Application.Common;

namespace Ecr.Application.Periods;

/// <summary>
/// Поточний період проєкту: `Auto` або `Pinned` (ФВ-1.13, D-77).
/// </summary>
/// <remarks>
/// ⚠ Використовується **лише як значення за замовчуванням** — які період
/// підставити в документ, розклад чи параметр звіту. **На рішення про доступ
/// не впливає ніколи** (ФВ-1.14): інакше `Pinned` став би способом обійти
/// закриття періоду.
/// </remarks>
public sealed class SetCurrentPeriodHandler(
    IUnitOfWork uow, IAuditWriter audit, ICurrentUser currentUser, IClock clock)
{
    public Task HandleAsync(int projectId, int? pinnedPeriodId, string? reason, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) pinnedPeriodId = null → режим Auto, веде PeriodStateJob;\n" +
            "2) інакше режим Pinned: reason ОБОВ'ЯЗКОВИЙ, період має належати проєкту;\n" +
            "3) запис у aud.StructureChange: хто, коли, з чого на що, чому;\n" +
            "4) ⛔ жодних перевірок доступу на основі цього значення — ані тут, " +
            "   ані деінде (ФВ-1.14).");
}
