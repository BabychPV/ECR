using Ecr.Application.Calculations;
using Ecr.Domain.Entities.Calculations;

namespace Ecr.Application.Ports;

/// <summary>Погодження перерахунку закритого періоду (<c>calc.RecalculationApproval</c>, аудит S1).</summary>
/// <remarks>
/// ⛔ Підтвердження й використання — умовні <c>UPDATE</c> з одним рядком
/// результату: «ще не підтверджено / ще не використано» перевіряє сама база в
/// момент запису, тож два паралельні запити не проходять обидва.
/// </remarks>
public interface IRecalculationApprovalStore
{
    /// <summary>Додає новий запит (зберігає <c>IUnitOfWork</c>).</summary>
    public void Add(RecalculationApproval approval);

    /// <summary>Живі погодження проєкту: не використані й не прострочені.</summary>
    public Task<IReadOnlyList<RecalculationApprovalDto>> ListActiveAsync(int projectId, DateTime utcNow, CancellationToken ct);

    /// <summary>Погодження проєкту за ідентифікатором; чуже чи відсутнє — <c>null</c>.</summary>
    public Task<RecalculationApprovalDto?> FindAsync(long id, int projectId, CancellationToken ct);

    /// <summary>
    /// Підтверджує погодження, якщо воно ще чекає й не прострочене; <c>false</c> — ні.
    /// </summary>
    public Task<bool> TryConfirmAsync(long id, int projectId, int confirmedByUserId, DateTime utcNow, CancellationToken ct);

    /// <summary>
    /// Атомарно позначає погодження використаним, якщо воно підтверджене, не
    /// використане, не прострочене й видане саме цьому ініціатору на цей проєкт
    /// і період. Повертає використане погодження або <c>null</c>.
    /// </summary>
    public Task<RecalculationApprovalDto?> TryConsumeAsync(
        long id, int projectId, int periodKey, int requestedByUserId, DateTime utcNow, CancellationToken ct);
}
