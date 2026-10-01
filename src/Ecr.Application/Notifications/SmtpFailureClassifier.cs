// src/Ecr.Application/Notifications/SmtpFailureClassifier.cs
using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Ecr.Application.Notifications;

/// <summary>
/// Відносить відмову поштового транспорту до категорії, яку адміністратор може
/// виправити (<c>DNS</c> / з'єднання / <c>TLS</c> / автентифікація / relay / тайм-аут).
/// </summary>
/// <remarks>
/// ⛔ Повертає лише КЛЮЧ каталогу, а не текст винятку: пробу показує адміністратору
/// екран каналів, і «сирий» текст SMTP-відповіді там — це або шум, або (за
/// неакуратного сервера) шматок облікових даних. Ключ без категорії
/// (<see cref="Unknown"/>) — чесне «не знаю», а не вигадана причина.
/// <para>
/// ⚠ Порядок перевірок важливий: <see cref="SmtpException"/> обгортає майже все
/// («Failure sending mail»), тож спершу йде обхід внутрішніх винятків (там сокет
/// і TLS), і лише потім — код відповіді сервера.
/// </para>
/// </remarks>
public static class SmtpFailureClassifier
{
    /// <summary>Імена ключів каталогу (<c>notifications.test.smtp.*</c>).</summary>
    public const string Dns = "notifications.test.smtp.dns";

    /// <summary>Сервер не приймає з'єднання (порт закрито, мережа недоступна).</summary>
    public const string Connect = "notifications.test.smtp.connect";

    /// <summary>Сертифікат сервера, версія <c>TLS</c> або обов'язковий <c>STARTTLS</c>.</summary>
    public const string Tls = "notifications.test.smtp.tls";

    /// <summary>Сервер відхилив логін/пароль.</summary>
    public const string Auth = "notifications.test.smtp.auth";

    /// <summary>Сервер не пересилає пошту цьому відправнику або адресату (relay).</summary>
    public const string Relay = "notifications.test.smtp.relay";

    /// <summary>Сервер не відповів за відведений час.</summary>
    public const string Timeout = "notifications.test.smtp.timeout";

    /// <summary>Причину не розпізнано.</summary>
    public const string Unknown = "notifications.test.smtp.unknown";

    /// <summary>Ключ категорії відмови.</summary>
    /// <param name="error">Виняток відправки.</param>
    public static string MessageKeyOf(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case AuthenticationException:
                    return Tls;

                case SocketException socket:
                    return socket.SocketErrorCode switch
                    {
                        SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => Dns,
                        SocketError.TimedOut => Timeout,
                        _ => Connect,
                    };

                case TimeoutException or OperationCanceledException:
                    return Timeout;
            }
        }

        return error switch
        {
            SmtpFailedRecipientException => Relay,
            SmtpException { StatusCode: SmtpStatusCode.MustIssueStartTlsFirst } => Tls,
            SmtpException { StatusCode: SmtpStatusCode.ClientNotPermitted } => Auth,
            SmtpException { StatusCode: SmtpStatusCode.MailboxUnavailable
                or SmtpStatusCode.MailboxNameNotAllowed } => Relay,
            SmtpException ex when ex.Message.Contains("authenticat", StringComparison.OrdinalIgnoreCase) => Auth,
            _ => Unknown,
        };
    }
}
