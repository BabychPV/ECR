// tests/Ecr.Infrastructure.Tests/Notifications/SmtpAdminSettingsTests.cs
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ecr.Application.Notifications;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Notifications;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Notifications;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Notifications;

/// <summary>
/// Адмін-налаштування SMTP (<c>D-263</c>) на справжній базі: ефективні налаштування БД &gt; конфігурація,
/// кеш і його скидання, пароль зашифрованим блобом, адресати за ролями (активні, з поштою, своєю мовою).
/// </summary>
/// <remarks>
/// ⚠ Транспорт перевіряється проти справжнього локального SMTP-приймача (TcpListener), а не мока:
/// лист мусить дійти саме туди, куди вказали НАЛАШТУВАННЯ З БАЗИ, а не конфігурація.
/// </remarks>
[Collection("SqlServer")]
public sealed class SmtpAdminSettingsTests(SqlServerFixture sql) : IAsyncLifetime
{
    /// <summary>⚠ Єдиний рядок спільної бази не лишається після тесту: інакше він «налаштував би» пошту чужим тестам.</summary>
    public Task InitializeAsync() => ClearAsync();

    public Task DisposeAsync() => ClearAsync();

    private async Task ClearAsync()
    {
        await using var db = Db();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM sys_ecr.SmtpSettings");
    }

    private const string Password = "Db-Stored-Pw-1";

    private EcrDbContext Db()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private IServiceScopeFactory Scopes()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Db());

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    private async Task SaveRowAsync(
        string host, int port, bool enabled, string from = "ecr@corp.example", string? password = null)
    {
        await using var db = Db();
        var row = await db.SmtpSettings.SingleOrDefaultAsync(s => s.Id == SmtpSettings.SingletonId);

        if (row is null)
        {
            row = new SmtpSettings(DateTime.UtcNow, null);
            db.SmtpSettings.Add(row);
        }

        row.Update(host, port, SmtpEncryptionMode.None, from, "ECR", SmtpAuthMode.None, null, enabled, DateTime.UtcNow, null);

        if (password is not null)
        {
            row.ReplacePassword(new SmtpPasswordProtector(new EphemeralDataProtectionProvider()).Protect(password), DateTime.UtcNow, null);
        }

        await db.SaveChangesAsync();
    }

    private static SmtpNotificationSender Sender(
        IServiceScopeFactory scopes, TimeProvider time, params (string, string)[] config)
        => new(Config(config), Substitute.For<ISecretProvider>(), scopes, new SmtpPasswordProtector(new EphemeralDataProtectionProvider()), time);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-263")]
    public async Task Лист_іде_на_сервер_з_налаштувань_БД_а_не_з_конфігурації_і_кеш_скидається_після_збереження()
    {
        await using var server = new FakeSmtpServer();
        await SaveRowAsync("127.0.0.1", server.Port, enabled: true);
        var time = new ManualTime();
        var sender = Sender(Scopes(), time, ("Smtp:Host", "config.invalid"), ("Smtp:From", "cfg@corp.example"));

        Assert.True(sender.IsConfigured);
        await sender.SendAsync(["to@corp.example"], "Subject DB", "Body DB", CancellationToken.None);

        var mail = Assert.Single(server.Messages);
        Assert.Contains("To: to@corp.example", mail, StringComparison.Ordinal);
        Assert.Contains("ecr@corp.example", mail, StringComparison.Ordinal);
        Assert.DoesNotContain("cfg@corp.example", mail, StringComparison.Ordinal);

        // Кеш: друге звернення в межах 30 с БД не читає (мутація: прибрати кеш → лічильник 2 і тест червоний).
        var reads = sender.DatabaseReads;
        _ = sender.IsConfigured;
        Assert.Equal(reads, sender.DatabaseReads);

        // Інвалідація після PUT: нові налаштування діють негайно, а не через 30 с.
        await SaveRowAsync("127.0.0.1", server.Port, enabled: true, from: "ecr2@corp.example");
        sender.Invalidate();
        await sender.SendAsync(["to@corp.example"], "S2", "B2", CancellationToken.None);
        Assert.Contains("ecr2@corp.example", server.Messages[^1], StringComparison.Ordinal);
        Assert.True(sender.DatabaseReads > reads);

        // Без інвалідації кеш доживає до TTL, і не довше.
        await SaveRowAsync("127.0.0.1", server.Port, enabled: true, from: "ecr3@corp.example");
        await sender.SendAsync(["to@corp.example"], "S3", "B3", CancellationToken.None);
        Assert.Contains("ecr2@corp.example", server.Messages[^1], StringComparison.Ordinal);
        time.Advance(SmtpNotificationSender.CacheTtl + TimeSpan.FromSeconds(1));
        await sender.SendAsync(["to@corp.example"], "S4", "B4", CancellationToken.None);
        Assert.Contains("ecr3@corp.example", server.Messages[^1], StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-263")]
    public async Task Вимкнені_або_неповні_налаштування_БД_не_діють_і_транспорт_береться_з_конфігурації()
    {
        var time = new ManualTime();
        var withConfig = Sender(Scopes(), time, ("Smtp:Host", "config.example"), ("Smtp:From", "cfg@corp.example"));
        var withoutConfig = Sender(Scopes(), time);

        await SaveRowAsync("db.example", 25, enabled: false);
        Assert.True(withConfig.IsConfigured);
        Assert.False(withoutConfig.IsConfigured);

        await SaveRowAsync("db.example", 25, enabled: true, from: "ecr@corp.example");
        withConfig.Invalidate();
        withoutConfig.Invalidate();
        Assert.True(withoutConfig.IsConfigured);

        // Недоступна БД: падаємо на конфігурацію, а не на «не налаштовано»; і кеш на збій не ставимо.
        var broken = new ServiceCollection()
            .AddScoped<EcrDbContext>(_ => new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
                .UseSqlServer("Server=127.0.0.1,1;Database=x;Connect Timeout=1;TrustServerCertificate=True").Options))
            .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var offline = Sender(broken, time, ("Smtp:Host", "config.example"), ("Smtp:From", "cfg@corp.example"));
        Assert.True(offline.IsConfigured);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-263")]
    public async Task Пароль_лежить_у_базі_зашифрованим_блобом_а_не_відкритим_текстом()
    {
        await SaveRowAsync("db.example", 25, enabled: true, password: Password);

        await using var db = Db();
        var blob = (await db.SmtpSettings.AsNoTracking().SingleAsync(s => s.Id == SmtpSettings.SingletonId)).PasswordProtected!;

        Assert.NotEmpty(blob);
        Assert.DoesNotContain(Password, Encoding.UTF8.GetString(blob), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, Encoding.Unicode.GetString(blob), StringComparison.Ordinal);

        // Singleton: другого рядка база не прийме.
        db.SmtpSettings.Add(new SmtpSettings(DateTime.UtcNow, null));
        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-263")]
    [Trait("Requirement", "ФВ-12.5")]
    public async Task Адресати_за_ролями_активні_з_поштою_кожен_своєю_мовою_без_дублів_з_явними_адресами()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        int roleId;
        await using (var db = Db())
        {
            var role = new Role(EcrCode.Create($"SM{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "smtp test" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            roleId = role.Id;

            var en = await AddUserAsync(db, roleId, $"en_{tag}@corp.example", "en");
            await AddUserAsync(db, roleId, $"ru_{tag}@corp.example", "ru");
            await AddUserAsync(db, roleId, $"nolang_{tag}@corp.example", null);
            await AddUserAsync(db, roleId, null, "ru"); // без пошти — пропускається
            var inactive = await AddUserAsync(db, roleId, $"off_{tag}@corp.example", "ru");
            await db.Database.ExecuteSqlRawAsync("UPDATE sec.[User] SET IsActive = 0 WHERE Id = {0}", inactive);
            await AddUserAsync(db, roleId, $"dup_{tag}@corp.example", "ru");
            _ = en;
        }

        await using var server = new FakeSmtpServer();
        await SaveRowAsync("127.0.0.1", server.Port, enabled: true);
        var transport = Sender(Scopes(), new ManualTime());

        await using var ctx = Db();
        var channel = new NotificationChannel(
            NotificationChannelKind.Smtp, $"roles-{tag}", """{"recipients":["DUP_%TAG%@corp.example"]}""".Replace("%TAG%", tag, StringComparison.Ordinal),
            DateTime.UtcNow, null);
        ctx.NotificationChannels.Add(channel);
        await ctx.SaveChangesAsync();
        ctx.NotificationChannelRoles.Add(new NotificationChannelRole(channel.Id, roleId));
        await ctx.SaveChangesAsync();

        var catalog = Substitute.For<IUiStringCatalog>();
        catalog.GetAsync("ru", Arg.Any<CancellationToken>()).Returns(new UiStringCatalog("ru", 1, new Dictionary<string, string>
        {
            ["notifications.periodOpened.subject"] = "RU: период {period}, проект {project}",
            ["notifications.periodOpened.body"] = "RU-тело {project}",
        }));
        var sender = new SmtpChannelSender(transport, ctx, catalog);

        await sender.SendAsync(
            channel,
            new NotificationMessage(
                "EN subject P1", "EN body",
                new NotificationText(
                    "notifications.periodOpened.subject", "notifications.periodOpened.body",
                    new Dictionary<string, string> { ["project"] = "PRJ", ["period"] = "P1" })),
            CancellationToken.None);

        var all = string.Join('\n', server.Messages);
        Assert.Contains($"en_{tag}@corp.example", all, StringComparison.Ordinal);
        Assert.Contains($"nolang_{tag}@corp.example", all, StringComparison.Ordinal);
        Assert.Contains($"ru_{tag}@corp.example", all, StringComparison.Ordinal);
        Assert.DoesNotContain($"off_{tag}", all, StringComparison.Ordinal);

        // Мовні групи: явний адресат (дублікат ролі) отримує ОДИН лист англійською, не два.
        Assert.Equal(1, server.Messages.Count(m => m.Contains($"dup_{tag}@corp.example", StringComparison.OrdinalIgnoreCase)));
        var ruMail = Assert.Single(server.Messages, m => m.Contains("RU: период P1, проект PRJ", StringComparison.Ordinal) || m.Contains("=?utf-8?", StringComparison.OrdinalIgnoreCase));
        Assert.Contains($"ru_{tag}@corp.example", ruMail, StringComparison.Ordinal);
        Assert.DoesNotContain($"en_{tag}@corp.example", ruMail, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-263")]
    public async Task Роль_без_жодного_активного_адресата_і_без_явних_адрес_дає_названу_відмову_а_не_тишу()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Db();
        var role = new Role(EcrCode.Create($"SN{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "empty role" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        await AddUserAsync(db, role.Id, null, "en");

        var channel = new NotificationChannel(NotificationChannelKind.Smtp, $"empty-{tag}", "{}", DateTime.UtcNow, null);
        db.NotificationChannels.Add(channel);
        await db.SaveChangesAsync();
        db.NotificationChannelRoles.Add(new NotificationChannelRole(channel.Id, role.Id));
        await db.SaveChangesAsync();

        var transport = Substitute.For<INotificationSender>();
        transport.IsConfigured.Returns(true);
        var sender = new SmtpChannelSender(transport, db);

        await Assert.ThrowsAsync<NotificationNoRecipientsException>(
            () => sender.SendAsync(channel, new NotificationMessage("S", "B"), CancellationToken.None));
        await transport.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-263")]
    public async Task Проба_розгортає_ролі_каналу_в_адреси_але_не_більше_межі_а_розсилка_без_межі()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Db();
        var role = new Role(EcrCode.Create($"SP{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "probe role" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        for (var i = 0; i < 3; i++)
        {
            await AddUserAsync(db, role.Id, $"p{i}_{tag}@corp.example", "en");
        }

        var channel = new NotificationChannel(NotificationChannelKind.Smtp, $"probe-{tag}", "{}", DateTime.UtcNow, null);
        db.NotificationChannels.Add(channel);
        await db.SaveChangesAsync();
        db.NotificationChannelRoles.Add(new NotificationChannelRole(channel.Id, role.Id));
        await db.SaveChangesAsync();

        var sent = new List<string>();
        var transport = Substitute.For<INotificationSender>();
        transport.IsConfigured.Returns(true);
        transport.SendAsync(Arg.Do<IReadOnlyList<string>>(sent.AddRange), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var sender = new SmtpChannelSender(transport, db);

        // ⛔ Мутація: прибрати Take(RecipientLimit) → лишиться 3, рядок стане червоним.
        await sender.SendAsync(channel, new NotificationMessage("S", "B", RecipientLimit: 2), CancellationToken.None);
        Assert.Equal(2, sent.Count);

        sent.Clear();
        await sender.SendAsync(channel, new NotificationMessage("S", "B"), CancellationToken.None);
        Assert.Equal(3, sent.Count);
    }

    /// <summary>Рев'ю ent6 S3: ліміт проби — на ВСІХ адресатів разом (спершу явні, решту — ролям).</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ent6-S3")]
    public async Task Ліміт_проби_діє_на_явні_адреси_і_ролі_разом()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Db();
        var role = new Role(EcrCode.Create($"SQ{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "probe role" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        for (var i = 0; i < 5; i++)
        {
            await AddUserAsync(db, role.Id, $"r{i}_{tag}@corp.example", "en");
        }

        var explicitJson = "{\"recipients\":[\"e0@corp.example\",\"e1@corp.example\",\"e2@corp.example\"]}";
        var channel = new NotificationChannel(NotificationChannelKind.Smtp, $"mix-{tag}", explicitJson, DateTime.UtcNow, null);
        db.NotificationChannels.Add(channel);
        await db.SaveChangesAsync();
        db.NotificationChannelRoles.Add(new NotificationChannelRole(channel.Id, role.Id));
        await db.SaveChangesAsync();

        var sent = new List<string>();
        var transport = Substitute.For<INotificationSender>();
        transport.IsConfigured.Returns(true);
        transport.SendAsync(Arg.Do<IReadOnlyList<string>>(sent.AddRange), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var sender = new SmtpChannelSender(transport, db);

        // Мутація: повернути `.Take(message.RecipientLimit ?? int.MaxValue)` лише на ролях → піде 3 + 4 = 7.
        await sender.SendAsync(channel, new NotificationMessage("S", "B", RecipientLimit: 4), CancellationToken.None);
        Assert.Equal(4, sent.Count);
        Assert.Equal(3, sent.Count(a => a.StartsWith('e')));

        // Явних більше за ліміт — обрізаються, ролі не йдуть узагалі.
        sent.Clear();
        await sender.SendAsync(channel, new NotificationMessage("S", "B", RecipientLimit: 2), CancellationToken.None);
        Assert.Equal(2, sent.Count);
        Assert.All(sent, a => Assert.StartsWith("e", a, StringComparison.Ordinal));
    }

    private static async Task<int> AddUserAsync(EcrDbContext db, int roleId, string? email, string? language)
    {
        var user = new User($"sm_{Guid.NewGuid():N}"[..20], "Smtp test user", AuthProvider.Local);
        user.SetPassword("not-a-real-hash");
        user.SetEmail(email);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        db.RoleAssignments.Add(new RoleAssignment(roleId, user.Id, principalSid: null));

        if (language is not null)
        {
            db.UserPreferences.Add(new UserPreference(user.Id, "language", $"\"{language}\"", DateTime.UtcNow));
        }

        await db.SaveChangesAsync();

        return user.Id;
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Мінімальний SMTP-приймач: EHLO/MAIL/RCPT/DATA/QUIT, без шифрування й автентифікації.</summary>
    private sealed class FakeSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public FakeSmtpServer()
        {
            _listener.Start();
            _loop = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<string> Messages { get; } = [];

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 fake ESMTP");
            var data = new StringBuilder();
            var inData = false;

            while (await reader.ReadLineAsync() is { } line)
            {
                if (inData)
                {
                    if (line == ".")
                    {
                        inData = false;
                        lock (Messages)
                        {
                            Messages.Add(data.ToString());
                        }

                        data.Clear();
                        await writer.WriteLineAsync("250 queued");
                    }
                    else
                    {
                        data.AppendLine(line);
                    }

                    continue;
                }

                var verb = line.Length >= 4 ? line[..4].ToUpperInvariant() : line.ToUpperInvariant();

                switch (verb)
                {
                    case "EHLO":
                    case "HELO":
                        await writer.WriteLineAsync("250 fake");
                        break;
                    case "MAIL":
                    case "RCPT":
                        data.AppendLine(line);
                        await writer.WriteLineAsync("250 ok");
                        break;
                    case "DATA":
                        inData = true;
                        await writer.WriteLineAsync("354 go");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 bye");
                        return;
                    default:
                        await writer.WriteLineAsync("250 ok");
                        break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop;
        }
    }
}
