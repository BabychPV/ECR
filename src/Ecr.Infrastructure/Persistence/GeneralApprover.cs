// src/Ecr.Infrastructure/Persistence/GeneralApprover.cs
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// ✎ B155: вбудована загальна роль погоджувача (<c>sec.Role</c> з кодом <c>Approver</c> і <c>IsBuiltIn = 1</c>, сід
/// <c>00-prelude.sql</c>).
/// </summary>
/// <remarks>
/// ⛔ Лише ВБУДОВАНА (U1-04): власна роль, яку адміністратор назвав <c>Approver</c> до сіду, загальною не стає — інакше
/// код ролі роздавав би право погоджувати всі маршрути шаблонів.
/// </remarks>
internal static class GeneralApprover
{
    /// <summary>Код вбудованої ролі.</summary>
    public const string Code = "Approver";

    /// <summary>Ідентифікатор вбудованої загальної ролі; <c>null</c> — її немає.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="ct">Токен скасування.</param>
    public static Task<int?> RoleIdAsync(EcrDbContext db, CancellationToken ct)
        => db.Roles
            .AsNoTracking()
            .Where(r => r.Code == Code && r.IsBuiltIn)
            .Select(r => (int?)r.Id)
            .FirstOrDefaultAsync(ct);
}
