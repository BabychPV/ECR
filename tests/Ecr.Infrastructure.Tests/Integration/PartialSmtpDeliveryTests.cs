// tests/Ecr.Infrastructure.Tests/Integration/PartialSmtpDeliveryTests.cs
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ecr.Application.Notifications;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Ecr.Infrastructure.Tests.Integration;

/// <summary>
/// J1-03: один відхилений адресат (550 на <c>RCPT</c>) не робить подію невдалою — лист уже
/// пішов решті, і повтор розіслав би його їм удруге (до <c>MaxAttempts</c> копій).
/// </summary>
/// <remarks>
/// ⚠ Транспорт — проти справжнього локального SMTP-приймача (<see cref="TcpListener"/>): саме
/// <c>SmtpClient</c> вирішує, кидати ДО чи ПІСЛЯ <c>DATA</c>, і мок цього не довів би.
/// </remarks>
[Collection("SqlServer")]
public sealed class PartialSmtpDeliveryTests(SqlServerFixture sql)
{
    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "J1-03")]
    public async Task Відхилений_адресат_дає_часткову_доставку_а_лист_іде_решті()
    {
        await using var server = new RejectingSmtpServer("gone@corp.example");
        var sender = Sender(server.Port);

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати перетворення в `SmtpNotificationSender.SendAsync` — летить
        // `SmtpFailedRecipientException`, а не часткова доставка.
        var partial = await Assert.ThrowsAsync<NotificationPartiallyDeliveredException>(() => sender.SendAsync(
            ["a@corp.example", "gone@corp.example", "b@corp.example"], "Тема", "Текст", CancellationToken.None));

        Assert.Single(partial.Rejected);
        Assert.Contains("gone@corp.example", partial.Rejected[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, server.DeliveredCount);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "J1-03")]
    public async Task Відхилено_всіх_це_збій_а_не_часткова_доставка()
    {
        await using var server = new RejectingSmtpServer("gone@corp.example");
        var sender = Sender(server.Port);

        var failure = await Record.ExceptionAsync(() => sender.SendAsync(
            ["gone@corp.example"], "Тема", "Текст", CancellationToken.None));

        Assert.NotNull(failure);
        Assert.IsNotType<NotificationPartiallyDeliveredException>(failure);
        Assert.Equal(0, server.DeliveredCount);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "J1-03")]
    public async Task Частково_доставлена_подія_Sent_і_не_повторюється()
    {
        var eventCode = $"j1.partial.{Guid.NewGuid():N}"[..40];
        var now = new DateTime(2037, 5, 1, 1, 0, 0, DateTimeKind.Utc);

        await using (var setup = CreateContext())
        {
            setup.NotificationOutbox.Add(new NotificationOutboxItem(
                eventCode, "Тема", "Текст", "a@corp.example,gone@corp.example", now));
            await setup.SaveChangesAsync();
        }

        try
        {
            var sender = Substitute.For<INotificationSender>();
            sender.IsConfigured.Returns(true);
            sender
                .SendAsync(
                    Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new NotificationPartiallyDeliveredException(["gone@corp.example"]));

            await using (var db = CreateContext())
            {
                var dispatcher = new OutboxDispatcher(db, new TestClock(now), sender);
                await dispatcher.FlushAsync(eventCode, CancellationToken.None);
                await dispatcher.FlushAsync(eventCode, CancellationToken.None);
            }

            // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати гілку `NotificationPartiallyDeliveredException` в
            // `OutboxDispatcher` — подія повертається в `Pending`, другий прогін шле вдруге.
            await sender.Received(1).SendAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

            await using var verify = CreateContext();
            var row = await verify.NotificationOutbox.AsNoTracking().SingleAsync(n => n.EventCode == eventCode);
            Assert.Equal("Sent", row.State);
            Assert.Contains("gone@corp.example", row.Error, StringComparison.Ordinal);
        }
        finally
        {
            await using var cleanup = CreateContext();
            await cleanup.NotificationOutbox.Where(n => n.EventCode == eventCode).ExecuteDeleteAsync();
        }
    }

    private static SmtpNotificationSender Sender(int port)
        => new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Smtp:Host"] = "127.0.0.1",
                    ["Smtp:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["Smtp:From"] = "ecr@corp.example",
                    ["Smtp:UseStartTls"] = "false",
                })
                .Build(),
            Substitute.For<ISecretProvider>(),
            endpointPolicy: new AllowAllSmtpEndpointPolicy());

    /// <summary>Локальний SMTP-приймач, що відповідає 550 на <c>RCPT</c> для однієї адреси.</summary>
    private sealed class RejectingSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly string _rejected;
        private readonly Task _loop;
        private int _delivered;

        public RejectingSmtpServer(string rejected)
        {
            _rejected = rejected;
            _listener.Start();
            _loop = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int DeliveredCount => Volatile.Read(ref _delivered);

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
            var inData = false;

            while (await reader.ReadLineAsync() is { } line)
            {
                if (inData)
                {
                    if (line == ".")
                    {
                        inData = false;
                        Interlocked.Increment(ref _delivered);
                        await writer.WriteLineAsync("250 queued");
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
                    case "RCPT":
                        await writer.WriteLineAsync(
                            line.Contains(_rejected, StringComparison.OrdinalIgnoreCase)
                                ? "550 5.1.10 recipient rejected"
                                : "250 ok");
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
            _stop.Dispose();
        }
    }
}
