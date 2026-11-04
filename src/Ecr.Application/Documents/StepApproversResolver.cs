// src/Ecr.Application/Documents/StepApproversResolver.cs
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// AN-59 (варіант C, D-466): імена тих, хто МОЖЕ діяти на поточному кроці погодження поданих аркушів документа
/// (<c>DocumentSummary.StepApproverNames</c>).
/// </summary>
/// <remarks>
/// ⛔ Одне правило «хто може затвердити крок»: кожного кандидата з передфільтра (<see cref="IStepApproverStore"/>)
/// підтверджує те саме рішення, що й <c>ApproveSheetHandler</c> — <see cref="SheetCopyAccess.CanApproveOfAsync"/>
/// (видимість, стан <c>Submitted</c>, роль ПОТОЧНОГО кроку, грант <c>Approve</c>, область ролі, надання учасника).
/// Заборона самопогодження (<c>D-278</c>) скасована <c>D-440</c>: подавач у списку, якщо рішення його пропускає, —
/// рівно як і при затвердженні. Друга копія предиката розійшлася б із першою мовчки.
/// ⚠ Ціна обмежена й НЕ росте з числом кандидатів (≤ <see cref="MaxCandidates"/>): профілі читаються пакетом
/// (<see cref="IAccessDecisionService.BuildProfilesAsync"/>), рішення — пакетом на крок
/// (<see cref="IAccessDecisionService.CanApproveManyAsync"/>: те саме правило, умови аркуша й маршрут — раз).
/// ⛔ Звужений читач цього класу не викликає (обробник віддає <c>null</c>): ім'я погоджувача — розкриття людини.
/// </remarks>
public sealed class StepApproversResolver(IStepApproverStore store, IAccessDecisionService access)
{
    /// <summary>Скільки імен віддається найбільше.</summary>
    public const int MaxNames = 20;

    /// <summary>Скільки кандидатів (рядків «користувач × роль») перевіряється рішенням.</summary>
    public const int MaxCandidates = 100;

    /// <summary>Імена погоджувачів поточного кроку; <c>null</c> — немає поданих аркушів.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<(IReadOnlyList<string>? Names, bool Truncated)> ResolveAsync(
        long documentId, int periodKey, CancellationToken ct)
    {
        var key = new PeriodKey(periodKey);
        if (!key.IsValid)
        {
            return (null, false);
        }

        var facts = await store.LoadAsync(documentId, periodKey, MaxCandidates, ct).ConfigureAwait(false);
        if (facts is null || facts.Steps.Count == 0)
        {
            return (null, false);
        }

        // Профілі всіх кандидатів — одним пакетом (вимкнений між вибіркою і профілем у результаті відсутній).
        var users = facts.Candidates.GroupBy(c => c.UserId).ToList();
        var profiles = await access.BuildProfilesAsync([.. users.Select(u => u.Key)], ct).ConfigureAwait(false);

        var names = new HashSet<string>(StringComparer.Ordinal);
        var listed = new HashSet<int>();
        foreach (var step in facts.Steps)
        {
            // Кандидати цього кроку: несуть роль кроку (або це учасник документа / маршруту немає) і ще не названі.
            // Ролі, якої крок вимагає, кандидат не несе і не учасник — рішення відмовило б; не платимо за нього.
            var pending = users
                .Where(u => !listed.Contains(u.Key) && profiles.ContainsKey(u.Key)
                            && (step.RoleId is null || u.Any(c => c.RoleId is null || c.RoleId == step.RoleId)))
                .ToList();
            if (pending.Count == 0)
            {
                continue;
            }

            // Одне пакетне рішення на крок: умови аркуша й маршрут читаються раз, а не на кожного кандидата.
            var decisions = await access
                .CanApproveManyAsync(
                    [.. pending.Select(u => profiles[u.Key])], documentId, step.SheetDefId, key, step.SheetCopyId, new KnownStepRole(step.RoleId), ct)
                .ConfigureAwait(false);
            for (var i = 0; i < pending.Count; i++)
            {
                if (decisions[i].IsAllowed)
                {
                    listed.Add(pending[i].Key);
                    names.Add(pending[i].First().DisplayName);
                }
            }
        }

        var ordered = names.OrderBy(n => n, StringComparer.InvariantCulture).ToList();

        return (ordered.Count == 0 ? [] : [.. ordered.Take(MaxNames)], facts.Capped || ordered.Count > MaxNames);
    }
}
