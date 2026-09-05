// src/Ecr.Infrastructure/Integration/SmtpNotificationSender.cs
using System.Globalization;
using System.Net;
using System.Net.Mail;
using Ecr.Application.Ports;
using Microsoft.Extensions.Configuration;

namespace Ecr.Infrastructure.Integration;

/// <summary>
/// Доставка сповіщень поштою (<c>D-124</c>).
/// </summary>
/// <remarks>
/// ⛔ Пароль береться **лише за іменем секрету** через <see cref="ISecretProvider"/>
/// (`ФВ-6.11`): у конфігурації лежить `ECR_Smtp__SecretName`, а не значення.
/// Пароль у `appsettings.json` — це пароль у системі контролю версій.
///
/// ⚠ Без заданого `Host` відправник **не вважається налаштованим** і черга
/// накопичує далі. Це не помилка конфігурації, а нормальний стан контуру, де
/// пошту ще не підключили; задача каже про це вголос у зведенні, і події не
/// позначаються невдалими.
///
/// ⚠ Збій відправки **не втрачає** повідомлення: виняток іде нагору, задача
/// рахує спробу і лишає запис у черзі (`ФВ-12.4a` — три спроби, далі `Failed`).
/// Проковтнути його тут означало б «надіслано» для листа, якого немає.
/// </remarks>
public sealed class SmtpNotificationSender(IConfiguration configuration, ISecretProvider secrets)
    : INotificationSender
{
    /// <summary>Порт за замовчуванням: submission із STARTTLS.</summary>
    private const int DefaultPort = 587;

    /// <summary>Скільки чекати на сервер, перш ніж вважати спробу невдалою.</summary>
    /// <remarks>
    /// Тридцять секунд — межа, за якою затримка перестає бути «повільно» і
    /// стає «недоступно». Задача погодинна; чекати довше немає сенсу, а
    /// коротше — ловити хибні збої на завантаженому сервері.
    /// </remarks>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private string? Host => Value("Host");

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

    /// <inheritdoc />
    public async Task SendAsync(
        IReadOnlyList<string> recipients, string subject, string body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recipients);

        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "SMTP не налаштовано: задайте ECR_Smtp__Host. Події лишаються в черзі.");
        }

        if (recipients.Count == 0)
        {
            throw new InvalidOperationException("Адресатів не визначено.");
        }

        var from = Value("From")
                   ?? throw new InvalidOperationException("ECR_Smtp__From не задано.");

        using var message = new MailMessage { From = new MailAddress(from), Subject = subject, Body = body };

        foreach (var recipient in recipients)
        {
            message.To.Add(recipient);
        }

        using var client = new SmtpClient(Host, Port())
        {
            EnableSsl = Flag("UseStartTls", @default: true),
            Timeout = (int)Timeout.TotalMilliseconds,
        };

        var user = Value("User");
        if (!string.IsNullOrWhiteSpace(user))
        {
            // ⚠ Ім'я секрету, а не пароль. Порожній секрет — це помилка
            // конфігурації, а не «вхід без пароля»: другий варіант мовчки
            // перетворив би автентифіковану відправку на анонімну.
            var secretName = Value("SecretName")
                             ?? throw new InvalidOperationException(
                                 "ECR_Smtp__User задано, а ECR_Smtp__SecretName — ні.");

            var password = secrets.Find(secretName)
                           ?? throw new InvalidOperationException(
                               $"Секрет '{secretName}' не знайдено.");

            client.Credentials = new NetworkCredential(user, password);
        }
        else
        {
            // Інтегрована або анонімна відправка — рішення контуру, не наше.
            client.UseDefaultCredentials = true;
        }

        // ⚠ `SendMailAsync` із токеном: без нього зупинка застосунку чекала б
        // на таймаут SMTP.
        await client.SendMailAsync(message, ct).ConfigureAwait(false);
    }

    private string? Value(string key)
    {
        var raw = configuration[$"Smtp:{key}"];

        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private int Port()
        => int.TryParse(Value("Port"), CultureInfo.InvariantCulture, out var port) && port > 0
            ? port
            : DefaultPort;

    private bool Flag(string key, bool @default)
        => bool.TryParse(Value(key), out var value) ? value : @default;
}
