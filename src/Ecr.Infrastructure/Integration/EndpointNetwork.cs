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
