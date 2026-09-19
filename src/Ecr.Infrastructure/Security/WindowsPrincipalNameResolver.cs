using System.Security.Principal;
using Ecr.Application.Ports;

namespace Ecr.Infrastructure.Security;

/// <summary>
/// <see cref="IPrincipalNameResolver"/> на <see cref="NTAccount"/> /
/// <see cref="SecurityIdentifier"/>: питає ОС, а та — локальну базу чи домен.
/// </summary>
/// <remarks>
/// ⚠ API лише для Windows. На іншій ОС (CI йде на Linux) і при недоступному
/// каталозі відповідь — <c>null</c>: «не резолвиться» — штатний стан порту.
/// </remarks>
public sealed class WindowsPrincipalNameResolver : IPrincipalNameResolver
{
    /// <inheritdoc />
    public string? ResolveSid(string accountName)
        => Translate(() => OperatingSystem.IsWindows()
            ? new NTAccount(accountName).Translate(typeof(SecurityIdentifier)).Value
            : null);

    /// <inheritdoc />
    public string? ResolveName(string sid)
        => Translate(() => OperatingSystem.IsWindows()
            ? new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value
            : null);

    private static string? Translate(Func<string?> translate)
    {
        try
        {
            return translate();
        }
        catch (Exception e) when (e is IdentityNotMappedException or ArgumentException or SystemException)
        {
            // IdentityNotMapped — такого запису немає; SystemException — каталог
            // недоступний (Win32-помилка довіри/мережі). Обидва — «не резолвиться».
            return null;
        }
    }
}
