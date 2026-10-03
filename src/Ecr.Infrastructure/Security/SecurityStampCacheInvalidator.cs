using System.Runtime.CompilerServices;
using Ecr.Domain.Entities.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;

namespace Ecr.Infrastructure.Security;

/// <summary>
/// Скидає закешований штамп (<c>stamp:{id}</c>) кожного користувача, чий
/// <c>SecurityStamp</c> щойно збережено.
/// </summary>
/// <remarks>
/// ⚠ L1-01 (аудит 2026-10-03): ключ кешу <see cref="SecurityStampValidator"/>
/// ніде не скидався, тож після блокування, скидання пароля чи «вийти з усіх»
/// старий штамп ще до 5 с відповідав «так» на старій cookie. Тепер на цьому
/// вузлі відкликання діє з наступного запиту; інші вузли — у межах
/// задокументованого строку кешу, як і раніше.
///
/// Перехоплювач, а не виклик у кожному місці ротації: ротацій багато
/// (<c>SetPassword</c>, <c>LockByAdministrator</c>, вихід, ролі, гранти,
/// bootstrap), і забути одну було б легко й непомітно.
/// </remarks>
public sealed class SecurityStampCacheInvalidator(IMemoryCache cache) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, List<int>> _pending = [];

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Flush(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Flush(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var ids = context.ChangeTracker.Entries<User>()
            .Where(e => e.State == EntityState.Modified
                        && e.Property(u => u.SecurityStamp).IsModified)
            .Select(e => e.Entity.Id)
            .ToList();

        if (ids.Count > 0)
        {
            _pending.AddOrUpdate(context, ids);
        }
    }

    private void Flush(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var ids))
        {
            return;
        }

        _pending.Remove(context);
        foreach (var id in ids)
        {
            cache.Remove(SecurityStampValidator.CacheKey(id));
        }
    }
}
