// src/Ecr.Domain/Entities/Security/RoleAssignment.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Security;

/// <summary>
/// Призначення ролі. Основний спосіб для доменних користувачів — **на
/// AD-групу** (ФВ-6.15), для локальних — на користувача.
/// </summary>
/// <remarks>
/// Членство в групі береться з токена входу, а не запитом до каталогу на
/// кожну перевірку (ФВ-6.15a): недоступність каталогу не має розривати сеанс
/// уже автентифікованого користувача.
/// </remarks>
public sealed class RoleAssignment : Entity<int>
{
    private RoleAssignment() { }

    public RoleAssignment(int roleId, int? userId, string? principalSid)
    {
        RoleId = roleId;
        UserId = userId;
        PrincipalSid = principalSid;
    }

    public int RoleId { get; private set; }

    /// <summary>Призначення на особу. Взаємовиключне з <see cref="PrincipalSid"/>.</summary>
    public int? UserId { get; private set; }

    /// <summary>SID AD-групи. Це **не** авторство — воно завжди `UserId` (D-86).</summary>
    public string? PrincipalSid { get; private set; }

    /// <summary>Область дії: проєкт, аркуш, період (ФВ-6.14).</summary>
    public string? ScopeJson { get; private set; }
}
