// src/Ecr.Application/Documents/StepApproversResolver.cs
using Ecr.Application.Errors;
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
/// ⚠ Ціна обмежена: не більше <see cref="MaxCandidates"/> кандидатів, кожен — за профілем (кеш) і рішенням лише для
/// тих кроків, роль яких він несе; перший дозвіл зупиняє перебір його кроків.
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

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var user in facts.Candidates.GroupBy(c => c.UserId))
        {
            var roles = user.Where(c => c.RoleId is not null).Select(c => c.RoleId!.Value).ToHashSet();
            var participant = user.Any(c => c.RoleId is null);

            // Ролі, якої крок вимагає, кандидат не несе і не учасник — рішення відмовило б; не платимо за нього.
            var steps = facts.Steps
                .Where(s => s.RoleId is null || participant || roles.Contains(s.RoleId.Value))
                .ToList();
            if (steps.Count == 0)
            {
                continue;
            }

            AccessProfile profile;
            try
            {
                profile = await access.BuildProfileAsync(user.Key, ct).ConfigureAwait(false);
            }
            catch (AccessDeniedException)
            {
                // Обліковий запис вимкнено між вибіркою і профілем — погоджувачем бути не може.
                continue;
            }

            foreach (var step in steps)
            {
                var decision = await access
                    .CanApproveOfAsync(profile, documentId, step.SheetDefId, key, step.SheetCopyId, ct)
                    .ConfigureAwait(false);
                if (decision.IsAllowed)
                {
                    names.Add(user.First().DisplayName);
                    break;
                }
            }
        }

        var ordered = names.OrderBy(n => n, StringComparer.InvariantCulture).ToList();

        return (ordered.Count == 0 ? [] : [.. ordered.Take(MaxNames)], facts.Capped || ordered.Count > MaxNames);
    }
}
