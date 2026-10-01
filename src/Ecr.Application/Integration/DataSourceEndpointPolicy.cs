using System.Data.Common;
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
    /// Вердикт для PiSqlClient. Адреса — або рядок з'єднання ODBC
    /// (<c>Driver={PI SQL Client};Server=…</c> — саме його читає адаптер), або ім'я/адреса сервера
    /// (<c>server</c>, <c>server\instance</c>, <c>server,port</c>, <c>host:port</c>, <c>tcp:host,port</c>).
    /// Відмова за схему (<c>xxx://</c>), за рядок, що не розбирається, і за link-local/metadata
    /// у БУДЬ-ЯКОМУ ключі сервера рядка; loopback і приватні — легітимні.
    /// </summary>
    /// <remarks>
    /// ⛔ ent4 P2-1: раніше політика брала весь рядок з'єднання за «ім'я сервера», тож
    /// <c>Driver=…;Server=169.254.169.254</c> і <c>tcp:169.254.169.254,80</c> проходили.
    /// Тепер рядок розбирається тими самими правилами ODBC, що й в адаптері
    /// (<c>OdbcConnectionStringBuilder</c>), і перевіряється кожен ключ, що називає сервер.
    /// </remarks>
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

        if (SqlHostsOf(address) is not { } hosts)
        {
            return EndpointVerdict.Malformed;
        }

        foreach (var host in hosts)
        {
            if (IsCloudMetadataName(host) || (IPAddress.TryParse(host, out var ip) && IsLinkLocal(ip)))
            {
                return EndpointVerdict.HostForbidden;
            }
        }

        return EndpointVerdict.Allowed;
    }

    /// <summary>
    /// Хости, на які піде з'єднання PiSqlClient: з кожного ключа сервера рядка ODBC або з голого
    /// імені сервера. <c>null</c> — рядок з'єднання не розбирається. Рядок без ключа сервера
    /// (лише <c>DSN=…</c>) дає порожній перелік: сервер тоді задає адміністратор машини в DSN.
    /// </summary>
    public static IReadOnlyList<string>? SqlHostsOf(string address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (!address.Contains('=', StringComparison.Ordinal))
        {
            return [SqlHostOf(address)];
        }

        var builder = new DbConnectionStringBuilder(useOdbcRules: true);

        try
        {
            builder.ConnectionString = address;
        }
        catch (ArgumentException)
        {
            return null;
        }

        var hosts = new List<string>();

        foreach (string key in builder.Keys)
        {
            if (IsServerKey(key) && builder[key]?.ToString() is { Length: > 0 } value)
            {
                hosts.Add(SqlHostOf(value));
            }
        }

        return hosts;
    }

    /// <summary>
    /// Хост сервера з <c>server\instance</c>/<c>server,port</c>/<c>host:port</c>/<c>[v6]:port</c>,
    /// з префіксом протоколу (<c>tcp:</c>, <c>np:</c>, <c>lpc:</c>, <c>admin:</c>) чи без,
    /// і з іменованого каналу <c>\\host\pipe\…</c>.
    /// </summary>
    public static string SqlHostOf(string address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var s = address.Trim().Trim('{', '}').Trim();

        // `tcp:169.254.169.254,80`: без цього хостом ставав `tcp` (одна `:` = host:port).
        foreach (var prefix in ProtocolPrefixes)
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..].TrimStart();
                break;
            }
        }

        // Іменований канал: `\\host\pipe\sql\query`.
        if (s.StartsWith(@"\\", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        var cut = s.IndexOfAny(['\\', ',']);

        if (cut >= 0)
        {
            s = s[..cut];
        }

        if (s.StartsWith('[') && s.IndexOf(']', StringComparison.Ordinal) is var close and > 0)
        {
            return s[1..close];
        }

        // Один `:` — host:port; кілька — голий IPv6.
        if (s.IndexOf(':', StringComparison.Ordinal) is var colon and > 0
            && s.IndexOf(':', colon + 1) < 0)
        {
            s = s[..colon];
        }

        return s.Trim().TrimEnd('.');
    }

    private static readonly string[] ProtocolPrefixes = ["tcp:", "np:", "lpc:", "admin:"];

    /// <summary>
    /// Чи називає ключ рядка з'єднання сервер: <c>Server</c>, <c>Data Source</c>, <c>Address</c>,
    /// <c>Addr</c>, <c>Network Address</c>, <c>Host</c>, <c>AF Server</c>, <c>Failover Partner</c> тощо.
    /// ⚠ Навмисно широко (за частиною імені): невідомий драйверу ключ він проігнорує, а пропущений
    /// політикою ключ сервера — це обхід.
    /// </summary>
    private static bool IsServerKey(string key)
    {
        var k = key.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

        return k.Contains("SERVER", StringComparison.Ordinal)
               || k.Contains("HOST", StringComparison.Ordinal)
               || k.Contains("ADDR", StringComparison.Ordinal)
               || k.Contains("PARTNER", StringComparison.Ordinal)
               || k is "DATASOURCE" or "SOURCE";
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
    /// <para>
    /// ⚠ Голого <c>metadata</c> тут немає навмисно (ent4 P3-4): у корпоративній мережі це цілком
    /// легітимне коротке ім'я хоста. На GCP воно розв'язується в 169.254.169.254, і його ловить
    /// перевірка КОЖНОЇ розв'язаної адреси в обробнику збереження.
    /// </para>
    /// </summary>
    public static bool IsCloudMetadataName(string host)
        => CloudMetadataNames.Contains(host.Trim('[', ']').TrimEnd('.'), StringComparer.OrdinalIgnoreCase);

    private static readonly string[] CloudMetadataNames =
        ["metadata.google.internal", "metadata.goog", "metadata.azure.internal"];

    private static bool IsLocalName(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
}

