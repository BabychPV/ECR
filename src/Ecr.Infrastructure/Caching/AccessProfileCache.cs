using Ecr.Application.Security;
using Microsoft.Extensions.Caching.Memory;

namespace Ecr.Infrastructure.Caching;

/// <summary>
/// Кеш профілів доступу. Профіль будується **раз на сесію**: резолвити права
/// на кожну комірку — гарантована смерть продуктивності, бо бюджет відкриття
/// таблиці дає на права 50 мс на весь запит (ФВ-6.10).
/// </summary>
public sealed class AccessProfileCache(IMemoryCache memory)
{
    /// <summary>Повертає профіль із кешу або будує його.</summary>
    /// <param name="userId">Користувач.</param>
    /// <param name="securityStamp">Штамп безпеки — частина ключа.</param>
    /// <param name="factory">Побудова профілю при промаху.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<AccessProfile> GetOrCreateAsync(
        int userId, string securityStamp, Func<CancellationToken, Task<AccessProfile>> factory, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: ключ = $\"access:{userId}:{securityStamp}\". Зміна ролей або пароля змінює " +
            "SecurityStamp, тому старий запис просто перестає використовуватися — явна " +
            "інвалідація не потрібна.");
}
