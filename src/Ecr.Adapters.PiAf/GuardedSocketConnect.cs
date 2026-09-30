using System.Net;
using System.Net.Sockets;
using Ecr.Application.Integration;

namespace Ecr.Adapters.PiAf;

/// <summary>
/// Перевірка IP у МОМЕНТ підключення (захист від DNS-rebinding): ім'я хоста
/// може вказувати на дозволену адресу під час збереження джерела, а потім —
/// на loopback/link-local/metadata. Блок-лист застосовується до кожної
/// розв'язаної A/AAAA-адреси, яку справді використає сокет.
/// </summary>
/// <remarks>
/// Підміняється лише сокет: URL, заголовок <c>Host</c> і SPN для
/// Kerberos/NTLM лишаються за іменем хоста з адреси (SocketsHttpHandler бере їх
/// з URI, а не з кінцевої точки сокета). Приватні IP для DNS-імен дозволені
/// (корпоративний AF-сервер), лише блок-лист. Ліміт: запит через системний
/// проксі перевіряє адресу проксі, а не кінцевого хоста.
/// </remarks>
public static class GuardedSocketConnect
{
    /// <summary>Перевіряє розв'язані адреси: порожній набір чи будь-яка заблокована адреса — відмова.</summary>
    public static IReadOnlyList<IPAddress> Validate(string host, IReadOnlyList<IPAddress> resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        if (resolved.Count == 0)
        {
            throw new HttpRequestException($"Хост «{host}» не розв'язався в жодну адресу.");
        }

        foreach (var ip in resolved)
        {
            if (DataSourceEndpointPolicy.IsBlocked(ip))
            {
                throw new HttpRequestException(
                    $"Хост «{host}» розв'язався в заборонену адресу ({ip}): loopback/link-local/metadata/unspecified.");
            }
        }

        return resolved;
    }

    /// <summary>Колбек <see cref="SocketsHttpHandler.ConnectCallback"/> із перевіркою IP.</summary>
    public static ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        return ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ResolveAsync, OpenAsync, ct);
    }

    /// <summary>Тестований варіант: резолвер і «відкривач» з'єднання підставляються.</summary>
    public static async ValueTask<Stream> ConnectAsync(
        string host,
        int port,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPAddress, int, CancellationToken, Task<Stream>> open,
        CancellationToken ct)
    {
        var addresses = Validate(host, await resolve(host, ct).ConfigureAwait(false));
        Exception? last = null;

        foreach (var ip in addresses)
        {
            try
            {
                return await open(ip, port, ct).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                last = ex;
            }
        }

        throw new HttpRequestException($"Не вдалося підключитися до «{host}».", last);
    }

    private static Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        => Dns.GetHostAddressesAsync(host, ct);

    private static async Task<Stream> OpenAsync(IPAddress ip, int port, CancellationToken ct)
    {
        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(new IPEndPoint(ip, port), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
