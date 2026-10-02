// src/Ecr.Infrastructure/Integration/SmtpNotificationSender.cs
using System.Globalization;
using System.Net;
using System.Net.Mail;
using Ecr.Application.Notifications;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Notifications;
using Ecr.Infrastructure.Notifications;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Infrastructure.Integration;

/// <summary>
/// Доставка сповіщень поштою (<c>D-124</c>, <c>D-263</c>).
/// </summary>
/// <remarks>
/// ⛔ Пароль із конфігурації процесу береться **лише за іменем секрету** через
/// <see cref="ISecretProvider"/> (`ФВ-6.11`); пароль з адмін-налаштувань лежить у базі ЗАШИФРОВАНИМ
/// (DataProtection) і розшифровується тут, у момент відправки, ніколи не повертаючись в API.
///
/// ⚠ Ефективні налаштування: УВІМКНЕНІ й повні налаштування з БД (<c>sys_ecr.SmtpSettings</c>)
/// мають пріоритет над конфігурацією процесу (<c>Smtp:*</c>, залишається запасним шляхом).
/// Результат кешується на <see cref="CacheTtl"/>; <see cref="Invalidate"/> (після PUT) скидає кеш.
/// Недоступна БД — не привід мовчки слати «з нічого»: падаємо на конфігурацію, а кеш на цей збій
/// не ставимо.
///
/// ⚠ Без заданих `Host` і `From` відправник **не вважається налаштованим** і черга
/// накопичує далі. Це не помилка конфігурації, а нормальний стан контуру, де
/// пошту ще не підключили; задача каже про це вголос у зведенні, і події не
/// позначаються невдалими.
///
/// ⚠ Збій відправки **не втрачає** повідомлення: виняток іде нагору, задача
/// рахує спробу і лишає запис у черзі (`ФВ-12.4a` — три спроби, далі `Failed`).
/// Проковтнути його тут означало б «надіслано» для листа, якого немає.
/// </remarks>
public sealed class SmtpNotificationSender(
    IConfiguration configuration,
    ISecretProvider secrets,
    IServiceScopeFactory? scopes = null,
    SmtpPasswordProtector? protector = null,
    TimeProvider? time = null,
    ISmtpEndpointPolicy? endpointPolicy = null) : INotificationSender, ISmtpSettingsCache
{
    /// <summary>Скільки діє знімок ефективних налаштувань.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>Порт за замовчуванням: submission із STARTTLS.</summary>
    private const int DefaultPort = 587;

    /// <summary>Скільки чекати на сервер, перш ніж вважати спробу невдалою.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly ISecretProvider _secrets = secrets;
    private readonly SmtpPasswordProtector? _protector = protector;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ISmtpEndpointPolicy _endpointPolicy =
        endpointPolicy ?? new SmtpEndpointPolicy(new EndpointNetwork(configuration));
    private readonly object _gate = new();
    private Effective? _cached;
    private DateTimeOffset _expires;

    /// <inheritdoc />
    public bool IsConfigured => Current().IsConfigured;

    /// <summary>Скільки разів налаштування читалися з БД — для тестів кешу.</summary>
    public int DatabaseReads { get; private set; }

    /// <inheritdoc />
    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    /// <inheritdoc />
    public async Task SendAsync(
        IReadOnlyList<string> recipients, string subject, string body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recipients);

        var settings = Current();

        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException(
                "SMTP не налаштовано: задайте його в адмін-налаштуваннях або ECR_Smtp__Host і ECR_Smtp__From. Події лишаються в черзі.");
        }

        if (recipients.Count == 0)
        {
            throw new InvalidOperationException("Адресатів не визначено.");
        }

        // ⛔ S4 (ент6): політика напрямку діє на КОЖНЕ відправлення (і пробу, і чергу; БД і запасний Smtp:*):
        // старі збережені значення з нестандартним портом чи loopback-хостом тут не проходять.
        if (!_endpointPolicy.IsPortAllowed(settings.Port)
            || !await _endpointPolicy.IsHostAllowedAsync(settings.Host!, SmtpEndpointStrictness.IsStrict, ct).ConfigureAwait(false))
        {
            throw new SmtpEndpointForbiddenException();
        }

        using var message = new MailMessage
        {
            From = string.IsNullOrWhiteSpace(settings.FromName)
                ? new MailAddress(settings.From!)
                : new MailAddress(settings.From!, settings.FromName),
            Subject = subject,
            Body = body,
        };

        foreach (var recipient in recipients)
        {
            message.To.Add(recipient);
        }

        using var client = CreateClient(settings);

        // ⚠ `SendMailAsync` із токеном: без нього зупинка застосунку чекала б
        // на таймаут SMTP.
        await client.SendMailAsync(message, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Клієнт під ефективні налаштування. ⛔ S2 (ent6): без логіна — лише анонімна відправка. Інтегрована
    /// автентифікація (<c>UseDefaultCredentials</c>, NTLM-дані облікового запису служби) НЕ підтримується:
    /// з адмін-налаштувань вона відправила б дані служби на довільний хост. Те саме для запасного <c>Smtp:*</c>.
    /// </summary>
    private SmtpClient CreateClient(Effective settings)
        => BuildClient(
            settings.Host, settings.Port, settings.StartTls, settings.User, () => settings.PasswordOf(this));

    /// <summary>Збирає клієнта; відкрито для тесту стану облікових даних (без мережі).</summary>
    /// <param name="host">Хост.</param>
    /// <param name="port">Порт.</param>
    /// <param name="startTls">Чи вмикати STARTTLS.</param>
    /// <param name="user">Логін; порожній — анонімно.</param>
    /// <param name="password">Пароль (читається лише за наявності логіна).</param>
    public static SmtpClient BuildClient(string? host, int port, bool startTls, string? user, Func<string> password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var client = new SmtpClient(host, port)
        {
            EnableSsl = startTls,
            Timeout = (int)Timeout.TotalMilliseconds,
            UseDefaultCredentials = false,
        };

        if (!string.IsNullOrWhiteSpace(user))
        {
            client.Credentials = new NetworkCredential(user, password());
        }

        return client;
    }

    private Effective Current()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();

            if (_cached is not null && now < _expires)
            {
                return _cached;
            }

            var fromDb = TryReadDatabase(out var failed);
            var value = fromDb ?? FromConfiguration();

            if (!failed)
            {
                _cached = value;
                _expires = now + CacheTtl;
            }

            return value;
        }
    }

    /// <summary>Налаштування з БД; <c>null</c> — немає, вимкнені, неповні або БД недоступна.</summary>
    private Effective? TryReadDatabase(out bool failed)
    {
        failed = false;

        if (scopes is null)
        {
            return null;
        }

        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<EcrDbContext>();
            DatabaseReads++;

            var row = db.SmtpSettings.AsNoTracking().FirstOrDefault(s => s.Id == SmtpSettings.SingletonId);

            return row is { IsComplete: true }
                ? new Effective(
                    row.Host, row.Port, row.EncryptionMode == SmtpEncryptionMode.StartTls, row.FromAddress,
                    row.FromName, row.AuthMode == SmtpAuthMode.Password ? row.UserName : null, null,
                    row.PasswordProtected, "database")
                : null;
        }
#pragma warning disable CA1031 // Недоступна БД не має валити «чи налаштовано»: падаємо на конфігурацію процесу.
        catch (Exception)
#pragma warning restore CA1031
        {
            failed = true;

            return null;
        }
    }

    private Effective FromConfiguration()
    {
        var port = int.TryParse(Value("Port"), CultureInfo.InvariantCulture, out var p) && p > 0 ? p : DefaultPort;

        return new Effective(
            Value("Host"), port, bool.TryParse(Value("UseStartTls"), out var tls) ? tls : true, Value("From"), null,
            Value("User"), Value("SecretName"), null, "configuration");
    }

    private string? Value(string key)
    {
        var raw = configuration[$"Smtp:{key}"];

        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    /// <summary>Знімок налаштувань, за якими йде відправка.</summary>
    private sealed record Effective(
        string? Host, int Port, bool StartTls, string? From, string? FromName, string? User, string? SecretName,
        byte[]? PasswordProtected, string Source)
    {
        public bool IsConfigured => Host is not null && From is not null;

        public string PasswordOf(SmtpNotificationSender owner)
        {
            if (PasswordProtected is { Length: > 0 } blob)
            {
                // ⚠ Без розшифровувача блоб прочитати нічим: це помилка збірки, а не «вхід без пароля».
                return owner._protector?.Unprotect(blob)
                       ?? throw new InvalidOperationException("Пароль SMTP збережено, а розшифровувача немає.");
            }

            // ⚠ Ім'я секрету, а не пароль. Порожній секрет — це помилка
            // конфігурації, а не «вхід без пароля»: другий варіант мовчки
            // перетворив би автентифіковану відправку на анонімну.
            var secretName = SecretName
                             ?? throw new InvalidOperationException(
                                 "ECR_Smtp__User задано, а ECR_Smtp__SecretName — ні.");

            return owner._secrets.Find(secretName)
                   ?? throw new InvalidOperationException($"Секрет '{secretName}' не знайдено.");
        }
    }
}
