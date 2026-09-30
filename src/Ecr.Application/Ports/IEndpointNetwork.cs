using System.Net;

namespace Ecr.Application.Ports;

/// <summary>
/// Мережеві відомості, потрібні політиці адреси джерела
/// (<see cref="Integration.DataSourceEndpointPolicy"/>): список дозволених
/// хостів з конфігурації й розв'язання імені хоста в IP-адреси.
/// </summary>
/// <remarks>
/// Порт, а не прямі <c>Dns</c>/<c>IConfiguration</c> в Application: політика
/// лишається чистою й перевіряється в тесті з підробленим розв'язувачем.
/// </remarks>
public interface IEndpointNetwork
{
    /// <summary>
    /// <c>PiWebApi:AllowedHosts</c>: порожній — обмеження немає (діє лише
    /// блок-лист); інакше хост адреси має збігатися точно або як <c>*.domain</c>.
    /// </summary>
    public IReadOnlyList<string> AllowedHosts { get; }

    /// <summary>Усі A/AAAA-адреси імені; порожній список — не розв'язалось.</summary>
    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct);
}

