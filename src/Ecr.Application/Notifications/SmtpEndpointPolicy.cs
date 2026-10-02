// src/Ecr.Application/Notifications/SmtpEndpointPolicy.cs
using System.Net;
using Ecr.Application.Integration;
using Ecr.Application.Ports;

namespace Ecr.Application.Notifications;

/// <summary>Відмова політики напрямку пошти: порт чи хост поза дозволеним. Текст не розкриває мережу.</summary>
public sealed class SmtpEndpointForbiddenException : InvalidOperationException
{
    /// <summary>Створює відмову.</summary>
    public SmtpEndpointForbiddenException()
        : base("SMTP endpoint is not allowed by policy (port or host).")
    {
    }
}

/// <summary>
/// <see cref="ISmtpEndpointPolicy"/> поверх <see cref="IEndpointNetwork"/> (DNS і <c>Smtp:AllowedPorts</c>).
/// </summary>
/// <remarks>
/// ⛔ ent6 S4. Порти — лише <see cref="StandardPorts"/> та конфігурація: інакше проба SMTP на довільний
/// порт — сканер внутрішньої мережі. Хост: ті самі допоміжні методи, що й у політики джерел
/// (<see cref="DataSourceEndpointPolicy.IsBlocked"/>, <see cref="DataSourceEndpointPolicy.IsCloudMetadataName"/>).
/// ⚠ ПРИВАТНІ адреси дозволені свідомо: корпоративний relay — майже завжди 10.x/172.16.x/192.168.x;
/// заборона зламала б основний сценарій. Захист від DNS-rebinding на loopback/link-local — перевіркою
/// кожної розв'язаної адреси. ⚠ Залишок: між цією перевіркою й власним розв'язанням <c>SmtpClient</c> є
/// вікно; пінити IP не можна (ламає перевірку імені сертифіката при STARTTLS).
/// </remarks>
public sealed class SmtpEndpointPolicy(
    IEndpointNetwork network, Microsoft.Extensions.Logging.ILogger<SmtpEndpointPolicy>? logger = null) : ISmtpEndpointPolicy
{
    private static readonly Action<Microsoft.Extensions.Logging.ILogger, Exception?> FailOpenLog =
        Microsoft.Extensions.Logging.LoggerMessage.Define(
            Microsoft.Extensions.Logging.LogLevel.Warning, new Microsoft.Extensions.Logging.EventId(1, nameof(FailOpenLog)),
            "SMTP: DNS-перевірка хоста не дала відповіді вчасно чи пуста; відправлення дозволено (fail-open), з'єднання все одно потребує розв'язання імені.");

    /// <summary>Стандартні поштові порти: SMTP, SMTPS, submission, альтернативний submission.</summary>
    public static readonly IReadOnlyList<int> StandardPorts = [25, 465, 587, 2525];

    /// <summary>Скільки чекати на DNS (для тесту задається ініціалізатором).</summary>
    public TimeSpan ResolveTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    public bool IsPortAllowed(int port)
        => StandardPorts.Contains(port) || network.SmtpAllowedPorts.Contains(port);

    /// <inheritdoc />
    public async Task<bool> IsHostAllowedAsync(string host, bool failClosed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);

        var raw = host.Trim();

        // ⛔ P1 (рев'ю sec-s4-2): літерал розбирається з СИРОГО рядка тими самими правилами, що в Dns/SmtpClient
        // (`[::1]:25`, `[::ffff:127.0.0.1]`, `127.1`, `0x7f.1`, `2130706433`), ДО зняття дужок. Інакше `::1]:25`
        // не розбиралось, DNS падав у «дозволено», а SmtpClient з'єднувався з loopback.
        if (IPAddress.TryParse(raw, out var literal))
        {
            // Дужки лише навколо цілого [v6]: [v6]:25 (порт у полі Server) відхиляється, порт — окреме поле.
            return !(raw.Contains(']', StringComparison.Ordinal) && !raw.EndsWith(']'))
                   && !IsBlockedAddress(literal);
        }

        // Усе, що не IP-літерал, — звичайне DNS-ім'я: без дужок, портів, схем і шляхів (порт — окреме поле).
        if (!IsPlainName(raw))
        {
            return false;
        }

        var name = raw.TrimEnd('.');

        if (name.Length == 0 || DataSourceEndpointPolicy.IsCloudMetadataName(name)
            || string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        IReadOnlyList<IPAddress> resolved;

        try
        {
            resolved = await network.ResolveAsync(name, ct).WaitAsync(ResolveTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or System.Net.Sockets.SocketException)
        {
            return Unresolved(failClosed);
        }

        // ⛔ P2-1: помилка/тайм-аут/порожня відповідь DNS — відмова для проби й збереження (failClosed), дозвіл із
        // Warning-логом для черги. Вікно DNS-rebinding між цією перевіркою і розв'язанням SmtpClient лишається
        // (IP пінити не можна: STARTTLS перевіряє ім'я сертифіката).
        return resolved.Count == 0 ? Unresolved(failClosed) : !resolved.Any(IsBlockedAddress);
    }

    private bool Unresolved(bool failClosed)
    {
        if (!failClosed && logger is not null)
        {
            FailOpenLog(logger, null);
        }

        return !failClosed;
    }

    private static readonly IReadOnlyList<IPAddress> MetadataAddresses =
        [IPAddress.Parse("fd00:ec2::254"), IPAddress.Parse("169.254.170.2"), IPAddress.Parse("169.254.169.254")];

    /// <summary>Ім'я з літер, цифр, <c>. - _</c> і нічого іншого (дужки, двокрапка, косі, пробіли — ні).</summary>
    internal static bool IsPlainName(string name)
        => name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary>
    /// Блок-лист для однієї адреси: спільний <see cref="DataSourceEndpointPolicy.IsBlocked"/> плюс гігієна IPv6
    /// (P3-1): зона <c>%scope</c> знімається (інакше <c>::1%1</c> не loopback), а IPv4-сумісний <c>::/96</c> закритий цілком.
    /// </summary>
    internal static bool IsBlockedAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        // Явно: metadata хмар, що не вкладаються в link-local — AWS IMDS по IPv6 (fd00:ec2::254, решта fc00::/7 — приватні,
        // дозволені) і ECS task metadata (169.254.170.2 — уже в 169.254/16, лишено для читача).
        if (MetadataAddresses.Contains(ip))
        {
            return true;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();

            if (bytes.Take(12).All(b => b == 0))
            {
                return true;
            }

            if (ip.ScopeId != 0)
            {
                ip = new IPAddress(bytes);
            }
        }

        return DataSourceEndpointPolicy.IsBlocked(ip);
    }
}