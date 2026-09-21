using Ecr.Application.Ports;
using Ecr.Domain.Entities.Security;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IUserPreferenceStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class UserPreferenceStore(EcrDbContext db) : IUserPreferenceStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<UserPreference>> ListAsync(int userId, CancellationToken ct)
        => await db.UserPreferences.AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderBy(p => p.Key)
            .ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<UserPreference?> FindAsync(int userId, string key, CancellationToken ct)
        => db.UserPreferences.FirstOrDefaultAsync(p => p.UserId == userId && p.Key == key, ct);

    /// <inheritdoc />
    public Task<int> CountAsync(int userId, CancellationToken ct)
        => db.UserPreferences.CountAsync(p => p.UserId == userId, ct);

    /// <inheritdoc />
    public void Add(UserPreference preference) => db.UserPreferences.Add(preference);

    /// <inheritdoc />
    public void Remove(UserPreference preference) => db.UserPreferences.Remove(preference);
}
