// src/Ecr.Application/Ports/IStepApproverStore.cs
namespace Ecr.Application.Ports;

/// <summary>Поданий (<c>Submitted</c>) аркуш або копія документа й роль його ПОТОЧНОГО кроку маршруту.</summary>
/// <param name="SheetDefId">Аркуш шаблону.</param>
/// <param name="SheetCopyId">Копія аркуша (RS); <c>null</c> — оригінал.</param>
/// <param name="RoleId">Роль поточного кроку; <c>null</c> — маршруту немає (затвердження одноетапне).</param>
public sealed record SubmittedStepRef(int SheetDefId, long? SheetCopyId, int? RoleId);

/// <summary>Кандидат у погоджувачі: користувач і роль, через яку він потрапив у вибірку.</summary>
/// <param name="UserId">Користувач (активний).</param>
/// <param name="DisplayName">Відображуване ім'я (<c>D-86</c>).</param>
/// <param name="RoleId">Роль-носій; <c>null</c> — кандидат як учасник документа з наданням Approve (D-444).</param>
public sealed record StepApproverCandidate(int UserId, string DisplayName, int? RoleId);

/// <summary>Усе, що потрібно для визначення «хто може діяти на поточному кроці» одного документа за період.</summary>
/// <param name="Steps">Подані аркуші й копії з роллю поточного кроку.</param>
/// <param name="Candidates">Передфільтр кандидатів (рядки «користувач × роль»), не більше ліміту.</param>
/// <param name="Capped">Кандидатів було більше за ліміт — вибірку обрізано.</param>
public sealed record StepApproverFacts(
    IReadOnlyList<SubmittedStepRef> Steps, IReadOnlyList<StepApproverCandidate> Candidates, bool Capped);

/// <summary>
/// AN-59 (варіант C, D-466): вибірка кандидатів у погоджувачі поточного кроку для картки документа.
/// </summary>
/// <remarks>
/// ⚠ ЛИШЕ передфільтр: активні користувачі з роллю кроку, чий грант охоплює проєкт чи аркуш, та учасники документа
/// з наданням Approve. Хто з них справді може затвердити, вирішує <c>IAccessDecisionService.CanApproveAsync</c>
/// (те саме рішення, що й у затвердженні) — друга копія правил тут заборонена.
/// ⛔ Звернень до бази — стала кількість (не залежить від числа користувачів): стан+проєкт+версія, маршрут,
/// кандидати з іменами.
/// </remarks>
public interface IStepApproverStore
{
    /// <summary>Подані аркуші документа за період і кандидати в погоджувачі.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період (<c>YYYYMM</c>).</param>
    /// <param name="maxCandidates">Ліміт рядків кандидатів (детермінований порядок за Id користувача).</param>
    /// <param name="ct">Скасування.</param>
    /// <returns><c>null</c> — немає поданих аркушів (або документа).</returns>
    public Task<StepApproverFacts?> LoadAsync(long documentId, int periodKey, int maxCandidates, CancellationToken ct);
}
