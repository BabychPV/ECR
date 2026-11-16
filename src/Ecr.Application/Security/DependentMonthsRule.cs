using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Domain.Enums;

namespace Ecr.Application.Security;

/// <summary>
/// ✎ B79 (рішення людини 13.11): загальний (не місячний) стовпець вводу річної таблиці, від якого залежать розрахунки
/// місяців, не змінюється, поки хоч один із цих місяців ЗАТВЕРДЖЕНО.
/// </summary>
/// <remarks>
/// <para>
/// Блокує лише <see cref="DocumentStatus.Approved"/>. <c>Submitted</c>, <c>Draft</c> і <c>Rejected</c> не блокують: подані
/// місяці перераховуються з позначкою (окрема lane), чернетки й відхилені — звичайним каскадом.
/// </para>
/// <para>
/// ⛔ Правило стоїть ПІСЛЯ <see cref="EditRules.CanEdit"/> і правил періоду шаблону: будь-яка наявна причина (архів,
/// закритий період, <c>RowLocked</c>, <c>NoGrant</c>, вікно доступу) перемагає — людина без права не дізнається зі
/// стану місяців те, чого їй не видно. Одне правило на два місця: рішення поза транзакцією
/// (<c>AccessDecisionService.Decide</c>) і повторну перевірку під блокуваннями аркушів року
/// (<c>PatchCellsHandler.CheckLockedColumnPeriods</c>).
/// </para>
/// </remarks>
public static class DependentMonthsRule
{
    /// <summary>Ім'я подробиці відмови з ключами періодів місяців, що блокують (масив чисел).</summary>
    public const string MonthsDetail = "months";

    /// <summary>Ім'я подробиці з тими самими ключами одним рядком — для підстановки в текст каталогу.</summary>
    public const string MonthsTextDetail = "monthsText";

    private static readonly IReadOnlyList<int> NoMonths = [];

    /// <summary>Ключі періодів місяців року, стан аркуша яких блокує правку; за зростанням.</summary>
    /// <param name="periods">Ключі періодів календаря року.</param>
    /// <param name="statusOf">Стан аркуша місяця за ключем періоду.</param>
    public static IReadOnlyList<int> BlockingMonths(IEnumerable<int> periods, Func<int, DocumentStatus> statusOf)
    {
        ArgumentNullException.ThrowIfNull(periods);
        ArgumentNullException.ThrowIfNull(statusOf);

        List<int>? blocking = null;
        foreach (var period in periods)
        {
            if (Blocks(statusOf(period)))
            {
                (blocking ??= []).Add(period);
            }
        }

        if (blocking is null)
        {
            return NoMonths;
        }

        blocking.Sort();
        return blocking;
    }

    /// <summary>Чи блокує стан аркуша місяця правку загального стовпця: лише <c>Approved</c>.</summary>
    /// <param name="status">Стан аркуша місяця.</param>
    public static bool Blocks(DocumentStatus status) => status == DocumentStatus.Approved;

    /// <summary>
    /// Рішення з урахуванням правила: дозвіл на стовпець із залежними розрахунками стає відмовою
    /// <see cref="EditDenyReason.DependentMonthsLocked"/>, якщо є місяці, що блокують. Відмову не чіпає.
    /// </summary>
    /// <param name="decision">Рішення решти правил.</param>
    /// <param name="blockingMonths">Місяці, що блокують (<see cref="BlockingMonths"/>).</param>
    public static EditDecision Apply(EditDecision decision, IReadOnlyList<int> blockingMonths)
    {
        ArgumentNullException.ThrowIfNull(blockingMonths);

        if (!decision.IsAllowed || blockingMonths.Count == 0)
        {
            return decision;
        }

        // ⚠ Перелік місяців — у `BlockingMonths` (структуровано), не в тексті: текст відмови не несе нічого, крім
        // причини (сторож `DenyLeakGuardTests`).
        return EditDecision.Deny(
                EditDenyReason.DependentMonthsLocked,
                "Від стовпця залежать розрахунки затверджених місяців.")
            with
            {
                BlockingMonths = blockingMonths,
            };
    }

    /// <summary>Відмова <c>ECR-ACCS-0403</c> з причиною <c>DependentMonthsLocked</c> і переліком місяців.</summary>
    /// <param name="deniedCount">Скільки комірок відхилено.</param>
    /// <param name="blockingMonths">Місяці, що блокують.</param>
    /// <remarks>
    /// Одна фабрика на обидва місця кидка (рішення в PATCH і повторна перевірка під замками): ті самі подробиці —
    /// <c>messageKey</c>, <c>deniedCount</c>, <c>reason</c>, <c>months</c>, <c>monthsText</c>. Місяці — ключі періодів
    /// аркуша, який користувач уже читає й має право правити (правило останнє, після гранта), тож нового про схований
    /// стовпець чи значення вони не розкривають.
    /// </remarks>
    public static AccessDeniedException Denied(int deniedCount, IReadOnlyCollection<int> blockingMonths)
    {
        ArgumentNullException.ThrowIfNull(blockingMonths);

        var details = new Dictionary<string, object?>
        {
            ["messageKey"] = "err.ECR-ACCS-0403.deniedCells",
            ["deniedCount"] = deniedCount.ToString(CultureInfo.InvariantCulture),
            ["reason"] = nameof(EditDenyReason.DependentMonthsLocked),
            [MonthsDetail] = blockingMonths.ToArray(),
            [MonthsTextDetail] = MonthsText(blockingMonths),
        };

        return new AccessDeniedException(
            "ECR-ACCS-0403",
            $"Заборонених комірок у батчі: {deniedCount}. Причина першої: {EditDenyReason.DependentMonthsLocked}.",
            details);
    }

    /// <summary>
    /// Повторна перевірка під блокуваннями аркушів року: місяць, затверджений між рішенням і комітом, відхиляє запис.
    /// </summary>
    /// <param name="annualPeriods">Ключі періодів календаря року, блокування яких уже взято.</param>
    /// <param name="lockedStatuses">Стани аркушів, прочитані під блокуваннями.</param>
    /// <param name="deniedCount">Скільки комірок відхилено (для <c>deniedCount</c>).</param>
    /// <exception cref="AccessDeniedException"><c>ECR-ACCS-0403</c>, <c>reason = DependentMonthsLocked</c>, <c>months</c>.</exception>
    public static void ThrowIfLocked(
        IEnumerable<int> annualPeriods, IReadOnlyDictionary<int, DocumentStatus> lockedStatuses, int deniedCount)
    {
        ArgumentNullException.ThrowIfNull(lockedStatuses);

        var blocking = BlockingMonths(annualPeriods, period => lockedStatuses[period]);
        if (blocking.Count == 0)
        {
            return;
        }

        throw Denied(deniedCount, blocking);
    }

    private static string MonthsText(IEnumerable<int> months)
        => string.Join(", ", months.Select(m => m.ToString(CultureInfo.InvariantCulture)));
}
