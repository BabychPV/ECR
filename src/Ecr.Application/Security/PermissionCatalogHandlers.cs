// src/Ecr.Application/Security/PermissionCatalogHandlers.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;

namespace Ecr.Application.Security;

/// <summary>Право з ПОВНОГО каталогу системи (<c>sec.Permission</c>).</summary>
/// <param name="Code">Код права; саме він перевіряється в обробниках.</param>
/// <param name="Group">Група для угруповання в UI: `Template`, `Document`, `Calculation` тощо.</param>
/// <param name="IsDangerous">
/// Небезпечне право (<c>ФВ-6.12</c>, <c>D-40</c>): у складені ролі не входить
/// за seed і видається поіменно, а не через матрицю.
/// </param>
public sealed record PermissionCatalogItem(string Code, string Group, bool IsDangerous);

/// <summary>
/// Повний каталог системних прав — усі, а не лише вже оголошені в наявних
/// ролях. Право <c>Security.ManageRoles</c>.
/// </summary>
/// <remarks>
/// ⛔ До появи цього ендпоінта клієнт (<c>SecurityPage.tsx</c>) складав перелік
/// прав перетином того, що вже оголошено в наявних ролях —
/// <c>[...new Set(roles.flatMap(r =&gt; r.permissions))]</c>. Право, якого ще
/// жодна роль не отримала, для форми створення ролі не існувало взагалі: щоб
/// уперше видати нове право, довелося б спершу видати його якійсь ролі іншим
/// шляхом (прямим записом у базу) — контур, який сам собі суперечить
/// (директива №11, трек T2, `#19`, `[звірка]`).
///
/// ⚠ Джерело каталогу — та сама таблиця <c>sec.Permission</c>, яку вже читають
/// <see cref="IUserStore.FilterUnknownAsync"/> і
/// <see cref="IUserStore.FilterDangerousAsync"/>: каталог прав закритий і
/// приходить із seed, а не з рантайму (<c>Permission.cs</c>), тож окремої
/// сутності чи статичного переліку в коді заводити не варто — це була б друга,
/// розбіжна копія того самого каталогу.
/// </remarks>
public sealed class ListPermissionsHandler(
    IUserStore users, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право, без якого каталог не віддається.</summary>
    public const string Permission = "Security.ManageRoles";

    /// <summary>Повертає ВЕСЬ каталог прав, включно з тими, яких не має жодна роль.</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<PermissionCatalogItem>> HandleAsync(CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        return await users.ListPermissionsAsync(ct).ConfigureAwait(false);
    }
}
