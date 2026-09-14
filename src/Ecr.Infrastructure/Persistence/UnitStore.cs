using Ecr.Application.Ports;
using Ecr.Domain.Entities.Units;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IUnitStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class UnitStore(EcrDbContext db) : IUnitStore
{
    /// <inheritdoc />
    public Task<Unit?> FindUnitByCodeAsync(string code, CancellationToken ct)
        => db.Units.FirstOrDefaultAsync(u => u.Code == code, ct);

    /// <inheritdoc />
    public Task<bool> DimensionExistsAsync(byte dimensionId, CancellationToken ct)
        => db.Dimensions.AnyAsync(d => d.Id == dimensionId, ct);

    /// <inheritdoc />
    public void AddUnit(Unit unit) => db.Units.Add(unit);
}
