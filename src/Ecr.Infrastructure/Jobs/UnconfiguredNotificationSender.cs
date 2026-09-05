using Ecr.Application.Ports;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Відправник, який чесно повідомляє, що транспорту немає.
/// </summary>
/// <remarks>
/// ⛔ Це <b>не</b> заглушка, що мовчить. Вона не «доставляє» нічого і не
/// позначає події надісланими: <see cref="IsConfigured"/> дорівнює
/// <c>false</c>, задача бачить це і лишає події в черзі зі станом
/// <c>Pending</c>, а в журнал обслуговування пише, скільки їх накопичилося.
/// <para>
/// Мовчазна «успішна» доставка була б гіршою за відсутність відправки:
/// події зникали б, а система рапортувала б про надіслані листи, яких ніхто
/// не отримував (`P-13`).
/// </para>
/// </remarks>
public sealed class UnconfiguredNotificationSender : INotificationSender
{
    /// <inheritdoc />
    public bool IsConfigured => false;

    /// <inheritdoc />
    public Task SendAsync(
        IReadOnlyList<string> recipients, string subject, string body, CancellationToken ct)
        => throw new InvalidOperationException(
            "Транспорт сповіщень не налаштований: ні SMTP, ні іншого відправника не зареєстровано. " +
            "Події лишаються в черзі itg.NotificationOutbox.");
}
