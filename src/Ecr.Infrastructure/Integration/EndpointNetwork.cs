using System.Net;
using System.Net.Sockets;
using Ecr.Application.Ports;
using Microsoft.Extensions.Configuration;

namespace Ecr.Infrastructure.Integration;

/// <summary><see cref="IEndpointNetwork"/>: <c>PiWebApi:AllowedHosts</c> з конфігурації й системний DNS.</summary>
public sealed class EndpointNetwork(IConfiguration configuration) : IEndpointNetwork
{
    /// <inheritdoc />
    public IReadOnlyList<string> AllowedHosts
        => configuration.GetSection("PiWebApi:AllowedHosts").Get<string[]>() is { } hosts
            ? [.. hosts.Where(h => !string.IsNullOrWhiteSpace(h))]
            : [];

    /// <inheritdoc />
    public IReadOnlyList<int> SmtpAllowedPorts
        => configuration.GetSection("Smtp:AllowedPorts").GetChildren()
            .Select(c => int.TryParse(c.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 0)
            .Where(p => p is >= 1 and <= 65535)
            .ToList();

    /// <inheritdoc />
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // Не розв'язалось зараз — не привід відмовляти: перевірка повторюється при з'єднанні.
            return [];
        }
    }
}
