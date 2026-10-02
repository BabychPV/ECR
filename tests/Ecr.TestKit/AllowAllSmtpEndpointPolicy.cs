// tests/Ecr.TestKit/AllowAllSmtpEndpointPolicy.cs
using Ecr.Application.Ports;

namespace Ecr.TestKit;

/// <summary>
/// Політика напрямку пошти без обмежень — ЛИШЕ для тестів, що ганяють справжній SMTP-транспорт проти локального
/// приймача на loopback (продукт loopback забороняє, ent6 S4). Умикається явною підстановкою в конструктор відправника.
/// </summary>
public sealed class AllowAllSmtpEndpointPolicy : ISmtpEndpointPolicy
{
    public bool IsPortAllowed(int port) => true;

    public Task<bool> IsHostAllowedAsync(string host, CancellationToken ct) => Task.FromResult(true);
}