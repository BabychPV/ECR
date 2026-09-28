using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IRecalculationApprovalStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class RecalculationApprovalStore(EcrDbContext db) : IRecalculationApprovalStore
{
    /// <inheritdoc />
    public void Add(RecalculationApproval approval) => db.RecalculationApprovals.Add(approval);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecalculationApprovalDto>> ListActiveAsync(
        int projectId, DateTime utcNow, CancellationToken ct)
        => await Project(db.RecalculationApprovals
                .Where(a => a.ProjectId == projectId && a.UsedAt == null && a.ExpiresAt > utcNow)
                .OrderByDescending(a => a.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<RecalculationApprovalDto?> FindAsync(long id, int projectId, CancellationToken ct)
        => Project(db.RecalculationApprovals.Where(a => a.Id == id && a.ProjectId == projectId))
            .SingleOrDefaultAsync(ct);

    /// <inheritdoc />
    public async Task<bool> TryConfirmAsync(
        long id, int projectId, int confirmedByUserId, DateTime utcNow, CancellationToken ct)
        => await db.RecalculationApprovals
            .Where(a => a.Id == id && a.ProjectId == projectId
                        && a.ConfirmedByUserId == null && a.UsedAt == null && a.ExpiresAt > utcNow)
            .ExecuteUpdateAsync(
                s => s.SetProperty(a => a.ConfirmedByUserId, confirmedByUserId)
                      .SetProperty(a => a.ConfirmedAt, utcNow),
                ct)
            .ConfigureAwait(false) == 1;

    /// <inheritdoc />
    public async Task<RecalculationApprovalDto?> TryConsumeAsync(
        long id, int projectId, int periodKey, int requestedByUserId, DateTime utcNow, CancellationToken ct)
    {
        var used = await db.RecalculationApprovals
            .Where(a => a.Id == id
                        && a.ProjectId == projectId
                        && a.PeriodKey == periodKey
                        && a.RequestedByUserId == requestedByUserId
                        && a.ConfirmedByUserId != null
                        && a.UsedAt == null
                        && a.ExpiresAt > utcNow)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.UsedAt, utcNow), ct)
            .ConfigureAwait(false);

        return used == 1 ? await FindAsync(id, projectId, ct).ConfigureAwait(false) : null;
    }

    private IQueryable<RecalculationApprovalDto> Project(IQueryable<RecalculationApproval> query)
        => query.AsNoTracking().Select(a => new RecalculationApprovalDto(
            a.Id,
            a.PeriodKey,
            a.Reason,
            a.RequestedByUserId,
            db.Users.Where(u => u.Id == a.RequestedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
            a.RequestedAt,
            a.ExpiresAt,
            a.ConfirmedByUserId,
            db.Users.Where(u => u.Id == a.ConfirmedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
            a.ConfirmedAt));
}
