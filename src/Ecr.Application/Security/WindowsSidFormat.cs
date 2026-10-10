using System.Text.RegularExpressions;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;

namespace Ecr.Application.Security;

/// <summary>
/// Форма SID облікового запису, який адміністратор набирає РУКАМИ
/// (<c>POST /users</c>, <c>PUT /users/{id}/windows-sid</c>).
/// </summary>
/// <remarks>
/// ⛔ X5-01: <c>CreateUserHandler</c> приймав будь-який непорожній рядок, а порожній
/// (<c>""</c>) проходив <c>?? throw</c> і падав <c>ArgumentException</c> у домені — 500.
/// Формат той самий, що в <c>AssignGroupRoleHandler.Resolve</c>.
///
/// ⚠ Резолву через каталог тут НЕМАЄ навмисно: домен може бути недоступний саме тоді,
/// коли запис заводять, і перевірка форми не має від нього залежати. Друкарську помилку
/// в цифрах форма не ловить — для неї є виправлення непідтвердженого SID.
/// </remarks>
internal static partial class WindowsSidFormat
{
    /// <summary>Обрізає пробіли, перевіряє форму і зводить до верхнього регістру.</summary>
    /// <param name="sid">Те, що набрав адміністратор.</param>
    /// <returns>Канонічний SID (<c>S-1-…</c>).</returns>
    /// <exception cref="BusinessRuleException">
    /// Порожньо — <c>windowsSidRequired</c>; не SID — <c>windowsSidMalformed</c> (обидва 422 <c>ECR-USR-0422</c>).
    /// </exception>
    public static string Canonicalize(string? sid)
    {
        var text = sid?.Trim() ?? string.Empty;

        if (text.Length == 0)
        {
            throw new BusinessRuleException(
                ErrorCodes.UserInvalid, "Для доменного запису потрібен SID.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-USR-0422.windowsSidRequired" });
        }

        if (!SidPattern().IsMatch(text))
        {
            throw new BusinessRuleException(
                ErrorCodes.UserInvalid, $"«{text}» не є SID.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-USR-0422.windowsSidMalformed",
                    ["sid"] = text,
                });
        }

        return text.ToUpperInvariant();
    }

    /// <summary>SID не прив'язаний до іншого запису; інакше 409 <c>windowsSidTaken</c>.</summary>
    /// <param name="users">Сховище облікових записів.</param>
    /// <param name="sid">Канонічний SID.</param>
    /// <param name="exceptUserId">Запис, якому SID дозволено мати (виправлення власного SID).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>Без цієї перевірки дубль доїжджав до <c>UX_User_Sid</c> і повертався як 500.</remarks>
    public static async Task EnsureFreeAsync(IUserStore users, string sid, int? exceptUserId, CancellationToken ct)
    {
        if (await users.FindByWindowsSidAsync(sid, ct).ConfigureAwait(false) is { } holder
            && holder.Id != exceptUserId)
        {
            throw new BusinessRuleException(
                ErrorCodes.UserDuplicate, $"SID {sid} уже прив'язаний до запису «{holder.UserName}».",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-USR-0409.windowsSidTaken",
                    ["sid"] = sid,
                    ["userName"] = holder.UserName,
                });
        }
    }

    [GeneratedRegex(@"^S-1-\d+(-\d+){1,14}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SidPattern();
}
