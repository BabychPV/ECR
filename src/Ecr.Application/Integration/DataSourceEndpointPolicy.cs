using System.Net;
using System.Net.Sockets;

namespace Ecr.Application.Integration;

/// <summary>Результат перевірки адреси джерела: <c>null</c> — дозволено.</summary>
public enum EndpointVerdict
{
    /// <summary>Адресу дозволено.</summary>
    Allowed,

    /// <summary>Порожня чи некоректна адреса, або userinfo.</summary>
    Malformed,

    /// <summary>Схема не http/https.</summary>
    Scheme,

    /// <summary>Loopback, link-local/metadata, unspecified, multicast чи (для Negotiate) приватний IP-літерал.</summary>
    HostForbidden,

    /// <summary>Хост поза <c>PiWebApi:AllowedHosts</c>.</summary>
    HostNotAllowed,
}

/// <summary>
/// Політика адреси джерела PI Web API: захист від SSRF і від відправки
/// службових облікових даних на чужий хост.
/// </summary>
/// <remarks>
/// ⛔ Право <c>Integration.Manage</c> інакше означало б «ходи службовим
/// обліковим записом (NTLM) куди скажу» й сканування внутрішньої мережі
/// проба-ендпоінтами. Блок-лист діє для ВСІХ режимів автентифікації;
/// приватні IP-ЛІТЕРАЛИ забороняються лише для Negotiate (Kerberos усе одно
/// потребує імені хоста для SPN). Приватні діапазони за DNS-ім'ям дозволені:
/// корпоративний AF-сервер — легітимний, але loopback/link-local/metadata
/// перевіряється по КОЖНІЙ розв'язаній A/AAAA-адресі.
/// </remarks>
public static class DataSourceEndpointPolicy
{
    /// <summary>Вердикт за адресою без розв'язання імені (чиста функція).</summary>
    public static EndpointVerdict CheckAddress(string? address, bool negotiate, IReadOnlyList<string>? allowedHosts)
    {
        if (!TryParse(address, out var uri, out var verdict))
        {
            return verdict;
        }

        var host = HostOf(uri);

        if (IsLocalName(host) || IsCloudMetadataName(host))
        {
            return EndpointVerdict.HostForbidden;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            var blocked = IsBlocked(ip) || (negotiate && IsPrivate(ip));

            if (blocked)
            {
                return EndpointVerdict.HostForbidden;
            }
        }

        return IsHostAllowed(host, allowedHosts) ? EndpointVerdict.Allowed : EndpointVerdict.HostNotAllowed;
    }

    /// <summary>
    /// Вердикт для PiSqlClient: адреса — ІМ'Я/адреса сервера (<c>server</c>, <c>server\instance</c>,
    /// <c>server,port</c>, <c>host:port</c>), а не URL. Відмова лише за схему (<c>xxx://</c>)
    /// і link-local/metadata IP-літерал; loopback і приватні — легітимні.
    /// </summary>
    public static EndpointVerdict CheckSqlServerAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return EndpointVerdict.Malformed;
        }

        if (address.Contains("://", StringComparison.Ordinal))
        {
            return EndpointVerdict.Scheme;
        }

        var host = SqlHostOf(address).TrimEnd('.');

        return IsCloudMetadataName(host) || (IPAddress.TryParse(host, out var ip) && IsLinkLocal(ip))
            ? EndpointVerdict.HostForbidden
            : EndpointVerdict.Allowed;
    }

    /// <summary>Хост сервера з <c>server\instance</c>/<c>server,port</c>/<c>host:port</c>/<c>[v6]:port</c>.</summary>
    public static string SqlHostOf(string address)
    {
        var s = address.Trim();
        var cut = s.IndexOfAny(['\\', ',']);

        if (cut >= 0)
        {
            s = s[..cut];
        }

        if (s.StartsWith('[') && s.IndexOf(']') is var close and > 0)
        {
            return s[1..close];
        }

        // Один `:` — host:port; кілька — голий IPv6.
        if (s.IndexOf(':') is var colon and > 0 && s.IndexOf(':', colon + 1) < 0)
        {
            s = s[..colon];
        }

        return s.TrimEnd('.');
    }

    /// <summary>Link-local/metadata: 169.254.0.0/16, fe80::/10.</summary>
    public static bool IsLinkLocal(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal;
        }

        var b = ip.GetAddressBytes();

        return b[0] == 169 && b[1] == 254;
    }

    /// <summary>Хост адреси, якщо це ім'я (не IP-літерал) — його треба розв'язати; інакше <c>null</c>.</summary>
    public static string? HostNeedingResolution(string? address)
    {
        if (!TryParse(address, out var uri, out _))
        {
            return null;
        }

        var host = HostOf(uri);

        return IPAddress.TryParse(host, out _) ? null : host;
    }

    /// <summary>Чи розв'язана адреса в блок-листі (loopback/link-local/metadata/unspecified/multicast).</summary>
    public static bool IsBlocked(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)
            || ip.Equals(IPAddress.Broadcast) || ip.Equals(IPAddress.IPv6None))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast;
        }

        var b = ip.GetAddressBytes();

        // 0.0.0.0/8, 169.254.0.0/16 (link-local, хмарний metadata 169.254.169.254), 224.0.0.0/4 (multicast).
        return b[0] == 0 || (b[0] == 169 && b[1] == 254) || b[0] >= 224;
    }

    /// <summary>Приватний діапазон: 10/8, 172.16/12, 192.168/16, fc00::/7.</summary>
    public static bool IsPrivate(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        var b = ip.GetAddressBytes();

        return ip.AddressFamily == AddressFamily.InterNetworkV6
            ? (b[0] & 0xFE) == 0xFC
            : b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>Точний збіг або <c>*.domain</c> (піддомен; сам <c>domain</c> не збігається).</summary>
    public static bool IsHostAllowed(string host, IReadOnlyList<string>? allowedHosts)
    {
        if (allowedHosts is not { Count: > 0 })
        {
            return true;
        }

        foreach (var raw in allowedHosts)
        {
            var rule = raw?.Trim().TrimEnd('.');

            if (string.IsNullOrEmpty(rule))
            {
                continue;
            }

            if (rule.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = rule[1..];

                if (host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(host, rule, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParse(string? address, out Uri uri, out EndpointVerdict verdict)
    {
        uri = null!;
        verdict = EndpointVerdict.Malformed;

        if (string.IsNullOrWhiteSpace(address)
            || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var parsed)
            || string.IsNullOrEmpty(parsed.Host))
        {
            // `file:///x`, `c:\x` чи `mailto:` — не http, а не «некоректна».
            verdict = address is not null && address.Contains(':', StringComparison.Ordinal)
                      && Uri.TryCreate(address.Trim(), UriKind.Absolute, out var other)
                      && other.Scheme is not ("http" or "https")
                ? EndpointVerdict.Scheme
                : EndpointVerdict.Malformed;
            return false;
        }

        if (parsed.Scheme is not ("http" or "https"))
        {
            verdict = EndpointVerdict.Scheme;
            return false;
        }

        // `http://allowed.host@evil/`: Uri бере ХОСТОМ те, що після `@`, але
        // userinfo в адресі джерела все одно не має сенсу.
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            return false;
        }

        uri = parsed;
        verdict = EndpointVerdict.Allowed;
        return true;
    }

    private static string HostOf(Uri uri) => uri.Host.Trim('[', ']').TrimEnd('.');

    /// <summary>
    /// Імена хмарних служб метаданих (GCP <c>metadata.google.internal</c>/<c>metadata.goog</c>,
    /// Azure/OCI <c>metadata.azure.internal</c>). Розв'язуються в 169.254.169.254, але за іменем
    /// перевірку IP-літерала оминають — тому відмова за самим іменем.
    /// </summary>
    public static bool IsCloudMetadataName(string host)
        => CloudMetadataNames.Contains(host.Trim('[', ']').TrimEnd('.'), StringComparer.OrdinalIgnoreCase);

    private static readonly string[] CloudMetadataNames =
        ["metadata.google.internal", "metadata.goog", "metadata.azure.internal", "metadata"];

    private static bool IsLocalName(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
}

