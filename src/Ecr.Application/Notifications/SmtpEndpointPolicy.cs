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
public sealed class SmtpEndpointPolicy(IEndpointNetwork network) : ISmtpEndpointPolicy
{
    /// <summary>Стандартні поштові порти: SMTP, SMTPS, submission, альтернативний submission.</summary>
    public static readonly IReadOnlyList<int> StandardPorts = [25, 465, 587, 2525];

    /// <summary>Скільки чекати на DNS; не розв'язалося вчасно — не підстава відмовляти (з'єднання теж не вийде).</summary>
    public static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    public bool IsPortAllowed(int port)
        => StandardPorts.Contains(port) || network.SmtpAllowedPorts.Contains(port);

    /// <inheritdoc />
    public async Task<bool> IsHostAllowedAsync(string host, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);

        var name = host.Trim().Trim('[', ']').TrimEnd('.');

        if (name.Length == 0 || DataSourceEndpointPolicy.IsCloudMetadataName(name)
            || string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IPAddress.TryParse(name, out var literal))
        {
            return !DataSourceEndpointPolicy.IsBlocked(literal);
        }

        IReadOnlyList<IPAddress> resolved;

        try
        {
            resolved = await network.ResolveAsync(name, ct).WaitAsync(ResolveTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return true;
        }

        return !resolved.Any(DataSourceEndpointPolicy.IsBlocked);
    }
}