// src/Ecr.Api/Security/ForwardedClientAddress.cs

using System.Net;

namespace Ecr.Api.Security;

/// <summary>
/// Хто саме «один клієнт» для обмежувачів за адресою (вхід, зміна пароля без сеансу,
/// звіти CSP, <c>/health/ready</c>).
/// </summary>
/// <remarks>
/// ⛔ Типово — адреса СОКЕТА, і <c>X-Forwarded-For</c> ігнорується. Заголовок пише
/// клієнт, а не мережа: якби ключ брався з нього без умов, нападник міняв би його
/// щозапиту і межа не спрацювала б ЖОДНОГО разу.
///
/// ⚠ Зворотний бік: за зворотним проксі адреса сокета — адреса проксі, одна на всіх,
/// і перший же користувач вичерпує межу для решти. Для цього існує ручка
/// <c>Security:RateLimit:TrustForwardedFor</c> (типово ВИМКНЕНА: інсталятор піднімає
/// Kestrel напряму, <c>R-01</c>).
///
/// ⛔ S1-01 (AUDIT-2026-10-09b). Раніше за увімкненого прапорця бралося НАЙЛІВІШЕ
/// значення. Але IIS ARR і nginx (<c>$proxy_add_x_forwarded_for</c>) ДОПИСУЮТЬ адресу
/// клієнта праворуч до того, що прислав сам клієнт, тож ліве значення ставить нападник:
/// <c>X-Forwarded-For: &lt;випадкова адреса&gt;</c> у кожному запиті — новий розділ із
/// повною межею, тобто межі входу немає взагалі. Тепер — так само, як
/// <c>ForwardedHeadersMiddleware</c> ASP.NET: ланцюг читається СПРАВА НАЛІВО, записи
/// довірених проксі (<c>Security:RateLimit:KnownProxies</c>) пропускаються, і клієнтом
/// є перший запис, що НЕ належить довіреному проксі. Без переліку довірених — найправіший
/// запис (його дописав наш єдиний проксі). Запис, що не є IP-адресою, — не клієнт, а
/// сміття: тоді ключем лишається адреса сокета, а не довільний рядок.
///
/// ⚠ Якщо перелік <c>KnownProxies</c> задано, заголовок читається лише від них: запит,
/// що прийшов на Kestrel напряму (повз проксі), рахується за адресою сокета, хоч би що
/// було в заголовку.
///
/// ⚠ IPv4, відображений в IPv6 (<c>::ffff:10.0.0.1</c>), зводиться до IPv4: інакше той
/// самий клієнт мав би два розділи залежно від того, як саме стек прийняв з'єднання.
/// </remarks>
public sealed class ForwardedClientAddress
{
    /// <summary>Ключ конфігурації: чи довіряти <c>X-Forwarded-For</c>.</summary>
    public const string TrustForwardedForKey = "Security:RateLimit:TrustForwardedFor";

    /// <summary>Ключ конфігурації: адреси довірених зворотних проксі через кому або крапку з комою.</summary>
    public const string KnownProxiesKey = "Security:RateLimit:KnownProxies";

    /// <summary>Заголовок зворотного проксі з ланцюгом адрес клієнта й проксі.</summary>
    public const string ForwardedForHeader = "X-Forwarded-For";

    /// <summary>Ключ розділу, коли адреси клієнта немає (наприклад, у тестовому хості).</summary>
    public const string UnknownClient = "unknown";

    private static readonly char[] ProxySeparators = [',', ';'];

    private readonly bool _trustForwardedFor;
    private readonly HashSet<IPAddress> _knownProxies;

    /// <summary>Створює визначник адреси клієнта.</summary>
    /// <param name="trustForwardedFor">Чи читати <c>X-Forwarded-For</c>.</param>
    /// <param name="knownProxies">Адреси довірених проксі; порожньо — довіряється лише найправіший запис.</param>
    public ForwardedClientAddress(bool trustForwardedFor, IEnumerable<IPAddress>? knownProxies = null)
    {
        _trustForwardedFor = trustForwardedFor;
        _knownProxies = (knownProxies ?? []).Select(Normalize).ToHashSet();
    }

    /// <summary>Визначник із конфігурації застосунку.</summary>
    /// <param name="configuration">Конфігурація.</param>
    /// <remarks>
    /// ⚠ Недійсний запис переліку тут пропускається мовчки — старт однаково зупинить
    /// <c>EcrConfigurationValidation</c> з ім'ям ключа (<c>U19</c>).
    /// </remarks>
    public static ForwardedClientAddress FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var trust = configuration.GetValue(TrustForwardedForKey, defaultValue: false);
        var proxies = ParseProxies(configuration[KnownProxiesKey], out _);
        return new ForwardedClientAddress(trust, proxies);
    }

    /// <summary>Розбирає перелік адрес проксі.</summary>
    /// <param name="text">Значення ключа <see cref="KnownProxiesKey"/>.</param>
    /// <param name="invalid">Записи, що не є IP-адресами.</param>
    /// <returns>Розібрані адреси.</returns>
    public static IReadOnlyList<IPAddress> ParseProxies(string? text, out IReadOnlyList<string> invalid)
    {
        var parsed = new List<IPAddress>();
        var bad = new List<string>();

        foreach (var entry in (text ?? string.Empty).Split(
                     ProxySeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(entry, out var address))
            {
                parsed.Add(address);
            }
            else
            {
                bad.Add(entry);
            }
        }

        invalid = bad;
        return parsed;
    }

    /// <summary>Ключ розділу для запиту.</summary>
    /// <param name="context">Запит.</param>
    /// <returns>IP-адреса клієнта рядком або <see cref="UnknownClient"/>.</returns>
    public string KeyOf(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var socket = context.Connection.RemoteIpAddress is { } remote ? Normalize(remote) : null;

        if (_trustForwardedFor
            && (_knownProxies.Count == 0 || (socket is not null && _knownProxies.Contains(socket)))
            && FromForwardedFor(context.Request.Headers[ForwardedForHeader].ToString()) is { } forwarded)
        {
            return forwarded.ToString();
        }

        return socket?.ToString() ?? UnknownClient;
    }

    /// <summary>Перший справа запис, що не належить довіреному проксі; <c>null</c> — немає придатного.</summary>
    private IPAddress? FromForwardedFor(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var entries = header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = entries.Length - 1; i >= 0; i--)
        {
            // ⚠ IIS ARR типово дописує адресу З ПОРТОМ (`203.0.113.7:51234`, `[2001:db8::1]:443`):
            // `IPEndPoint.TryParse` приймає обидві форми і голу адресу теж.
            if (!IPEndPoint.TryParse(entries[i], out var endpoint))
            {
                // Сміття праворуч від довірених проксі: ні йому, ні тому, що лівіше
                // (це писав клієнт), не віримо.
                return null;
            }

            var address = Normalize(endpoint.Address);

            if (!_knownProxies.Contains(address))
            {
                return address;
            }
        }

        return null;
    }

    private static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
