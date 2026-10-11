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

        if (!IsWellFormed(text))
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

    /// <summary>Чи рядок має форму SID (<c>S-1-…</c>); регістр не важливий — канонізація піднімає його вгору.</summary>
    /// <param name="text">Рядок після обрізання пробілів.</param>
    /// <remarks>
    /// ⛔ Z4-02/S1-06: (а) <c>[0-9]</c>, а не <c>\d</c> — <c>\d</c> у .NET збігається з цифрами Unicode («٣»), і такий
    /// «SID» проходив форму та падав далі (500 замість 422); (б) кінець — <c>\z</c>, а не <c>$</c> (кінцевий LF);
    /// (в) довжина обмежена: орган — до 15 цифр (48 біт), підорган — до 10 (32 біти), до 14 підорганів — тож
    /// найдовший SID ≈ 170 знаків, а не необмежений рядок. Регістр — явно <c>[Ss]</c>, без <c>IgnoreCase</c>
    /// (його таблиці рівності ширші за ASCII).
    /// </remarks>
    public static bool IsWellFormed(string text) => SidPattern().IsMatch(text);

    [GeneratedRegex(@"^[Ss]-1-[0-9]{1,15}(-[0-9]{1,10}){1,14}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SidPattern();
}
