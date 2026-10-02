// src/Ecr.Application/Ports/ISmtpEndpointPolicy.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Політика напрямку пошти (ent6 S4): куди SMTP-транспорт має право з'єднуватися. Проба й відправлення
/// без неї були б сканером внутрішніх портів і хостів з боку адміністратора налаштувань.
/// </summary>
public interface ISmtpEndpointPolicy
{
    /// <summary>Чи дозволено порт: 25/465/587/2525 і додаткові з <c>Smtp:AllowedPorts</c>.</summary>
    /// <param name="port">Порт.</param>
    public bool IsPortAllowed(int port);

    /// <summary>
    /// Чи дозволено хост: loopback, link-local, хмарний metadata (за літералом, за іменем і по КОЖНІЙ
    /// розв'язаній адресі) заборонені завжди; приватні діапазони дозволені (типовий корпоративний relay).
    /// </summary>
    /// <param name="host">Ім'я чи IP-літерал.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<bool> IsHostAllowedAsync(string host, CancellationToken ct);
}