// src/Ecr.Application/Notifications/WebhookUrlPolicy.cs
namespace Ecr.Application.Notifications;

/// <summary>
/// Захист від SSRF: куди серверу дозволено слати вебхук (<c>BE-33</c>).
/// </summary>
/// <remarks>
/// ⛔ Перелік суфіксів — із конфігурації ПРОЦЕСУ
/// (<c>Notifications:WebhookAllowedHostSuffixes</c>), не з інтерфейсу: інакше
/// той, хто має право керувати каналами, сам собі дозволив би будь-який хост.
/// Порожній перелік — жоден вебхук не приймається.
/// </remarks>
public sealed class WebhookUrlPolicy(IEnumerable<string> allowedHostSuffixes)
{
    // ⚠ Суфікс завжди з крапкою попереду: «webhook.office.com» без неї пустив
    // би `evilwebhook.office.com`.
    private readonly string[] _suffixes =
    [
        .. (allowedHostSuffixes ?? [])
            .Select(s => (s ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant())
            .Where(s => s.Length > 0)
            .Select(s => "." + s),
    ];

    /// <summary>Чи можна зберегти цей URL як адресу вебхука.</summary>
    public bool IsAllowed(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.HostNameType != UriHostNameType.Dns
            || uri.UserInfo.Length > 0)
        {
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();

        return Array.Exists(_suffixes, suffix => host.EndsWith(suffix, StringComparison.Ordinal));
    }
}
