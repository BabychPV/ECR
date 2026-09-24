// src/Ecr.Infrastructure/Security/SimulationAwareAccessDecisionService.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.ValueObjects;

namespace Ecr.Infrastructure.Security;

/// <summary>
/// Профіль доступу з урахуванням сеансу симуляції «очима користувача»
/// (<c>ФВ-6.16a</c>, <c>D-96</c>).
/// </summary>
/// <remarks>
/// ⛔ V-06 (UX-прохід, третій раунд). Сеанс відкривався й писався в
/// <c>aud.SimulationSession</c>, але профіль суб'єкта будувався РІВНО ОДИН раз —
/// у <c>StartSimulationHandler</c> — і нікуди не потрапляв: кожен наступний
/// запит, зокрема <c>/me</c>, будував профіль за <c>userId</c> із cookie, тобто
/// адміністратора з усіма правами. «Симуляція» нічого не симулювала.
///
/// ⚠ Одна точка замість сотні: кожен обробник питає профіль через
/// <see cref="IAccessDecisionService.BuildProfileAsync"/> із
/// <c>currentUser.UserId</c>. Тут, і лише для ПОТОЧНОГО користувача з
/// відкритим сеансом (<see cref="ICurrentUser.SimulationSessionId"/>),
/// повертається профіль суб'єкта з <c>IsSimulation</c> — і його бачать усі
/// обробники однаково, включно з <c>EditRules</c> (<c>SimulationReadOnly</c>).
/// Решта методів — прозоре делегування.
///
/// ⚠ Сеанс, який уже закрито (або якого немає), — не аварія: запит іде зі
/// звичайним профілем. Cookie з ідентифікатором сеансу переживає його
/// закриття лише доти, доки клієнт не отримає нову (завершення, вихід).
/// </remarks>
public sealed class SimulationAwareAccessDecisionService(
    IAccessDecisionService inner, ISimulationService simulation, ICurrentUser currentUser)
    : IAccessDecisionService
{
    /// <inheritdoc />
    public async Task<AccessProfile> BuildProfileAsync(int userId, CancellationToken ct)
    {
        if (currentUser.SimulationSessionId is { } sessionId && currentUser.UserId == userId)
        {
            try
            {
                var simulated = await simulation.BuildProfileAsync(sessionId, ct).ConfigureAwait(false);

                // ⛔ Сеанс мусить належати САМЕ цьому користувачеві: cookie
                // підписана сервером, але правило не спирається на те, що
                // ідентифікатор у ній не міг опинитися від іншого сеансу.
                if (simulated.SimulationActorUserId == userId)
                {
                    return simulated;
                }
            }
            catch (NotFoundException)
            {
                // Сеанс закрито — звичайний профіль.
            }
        }

        return await inner.BuildProfileAsync(userId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task InvalidateProfileAsync(int userId, CancellationToken ct)
        => inner.InvalidateProfileAsync(userId, ct);

    /// <inheritdoc />
    public Task<EditDecision> CanReadDocumentAsync(AccessProfile profile, long documentId, CancellationToken ct)
        => inner.CanReadDocumentAsync(profile, documentId, ct);

    /// <inheritdoc />
    public Task<EditDecision> CanEditCellAsync(
        AccessProfile profile, long documentId, CellAddress address, CancellationToken ct)
        => inner.CanEditCellAsync(profile, documentId, address, ct);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<CellAddress, EditDecision>> CanEditSliceAsync(
        AccessProfile profile, long tableInstanceId, CancellationToken ct)
        => inner.CanEditSliceAsync(profile, tableInstanceId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, NewRowAccess>> CanCreateRowsAsync(
        AccessProfile profile, long tableInstanceId, IReadOnlyCollection<string> rowKeys, CancellationToken ct)
        => inner.CanCreateRowsAsync(profile, tableInstanceId, rowKeys, ct);

    /// <inheritdoc />
    public Task<EditDecision> CanSubmitAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        => inner.CanSubmitAsync(profile, documentId, sheetDefId, periodKey, ct);

    /// <inheritdoc />
    public Task<EditDecision> CanApproveAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        => inner.CanApproveAsync(profile, documentId, sheetDefId, periodKey, ct);

    /// <inheritdoc />
    public Task<EditDecision> CanReopenAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        => inner.CanReopenAsync(profile, documentId, sheetDefId, periodKey, ct);

    /// <inheritdoc />
    public Task<ApprovalStepView?> CurrentApprovalStepAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        => inner.CurrentApprovalStepAsync(documentId, sheetDefId, periodKey, ct);
}
