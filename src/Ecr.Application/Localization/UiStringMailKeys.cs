// src/Ecr.Application/Localization/UiStringMailKeys.cs
namespace Ecr.Application.Localization;

/// <summary>
/// Ключі ТЕКСТІВ ЛИСТІВ і сповіщень (<c>notifications.&lt;подія&gt;.subject|body</c>), які сервер розсилає від довіреної
/// адреси (S7 ent6).
/// </summary>
/// <remarks>
/// ⛔ Право <c>System.ManageLocalization</c> не вважається небезпечним, а текст листа з посиланням, зашитим
/// редактором перекладів, — фішинг від довіреного відправника. Тому правка цих ключів вимагає ще й
/// <c>System.ManageNotifications</c>. ⚠ Клієнтські підписи сторінки (<c>notifications.title</c>,
/// <c>notifications.kind.*</c>, <c>notifications.test.*</c>) цього не стосується: розрізняє суфікс
/// <c>.subject</c>/<c>.body</c>.
/// </remarks>
public static class UiStringMailKeys
{
    /// <summary>Додаткове право на правку тексту листів.</summary>
    public const string Permission = "System.ManageNotifications";

    /// <summary>Чи є ключ текстом (темою або тілом) листа.</summary>
    /// <param name="key">Ключ каталогу.</param>
    /// <remarks>
    /// ⛔ Порівняння ЛОЯЛЬНЕ: регістр, кінцеві пробіли й невидимі символи (U+00AD…) ігноруються, як і в колації
    /// бази (<c>CI_AS</c>). Ordinal-порівняння пропускало <c>Notifications.x.Subject</c> повз право, а UPDATE
    /// усе одно влучав у справжній ключ. Додатково обробник звіряє КАНОНІЧНИЙ ключ із бази.
    /// </remarks>
    public static bool IsMailTemplate(string? key)
    {
        if (key is null)
        {
            return false;
        }

        var visible = new string([.. key.Where(c => char.GetUnicodeCategory(c) is not System.Globalization.UnicodeCategory.Format)])
            .Trim();

        return visible.StartsWith("notifications.", StringComparison.OrdinalIgnoreCase)
               && (visible.EndsWith(".subject", StringComparison.OrdinalIgnoreCase)
                   || visible.EndsWith(".body", StringComparison.OrdinalIgnoreCase));
    }
}