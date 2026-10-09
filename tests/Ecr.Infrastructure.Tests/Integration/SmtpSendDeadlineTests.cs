// tests/Ecr.Infrastructure.Tests/Integration/SmtpSendDeadlineTests.cs
using System.Net;
using System.Net.Sockets;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Integration;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Integration;

/// <summary>
/// R5-E1/E1-01: <c>SmtpClient.Timeout</c> не діє на <c>SendMailAsync</c>. Мовчазний сервер (неявний TLS на 465,
/// напіввідкрите з'єднання) без власного дедлайну підвішував відправку — а з нею <c>PeriodStateJob</c>.
/// </summary>
public sealed class SmtpSendDeadlineTests
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Requirement", "D-263")]
    public async Task Мовчазний_SMTP_сервер_дає_TimeoutException_у_межах_дедлайну_а_не_зависання()
    {
        // Сервер приймає з'єднання і мовчить: клієнт чекає на привітання 220 вічно.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var accepted = new CancellationTokenSource();
        var acceptTask = listener.AcceptTcpClientAsync(accepted.Token).AsTask();

        var policy = Substitute.For<ISmtpEndpointPolicy>();
        policy.IsPortAllowed(Arg.Any<int>()).Returns(true);
        policy.IsHostAllowedAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(true);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smtp:Host"] = "127.0.0.1",
                ["Smtp:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Smtp:From"] = "ecr@corp.example",
                ["Smtp:UseStartTls"] = "false",
            })
            .Build();

        var sender = new SmtpNotificationSender(config, Substitute.For<ISecretProvider>(), endpointPolicy: policy)
        {
            SendDeadline = TimeSpan.FromMilliseconds(500),
        };

        // ⛔ Мутація: повернути `client.SendMailAsync(message, ct)` без дедлайну → WaitAsync(Ceiling) кидає
        // TimeoutException ЗОВНІШНЬОЇ межі тесту, а не відправника (InnerException перевіряється нижче).
        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => sender.SendAsync(["a@corp.example"], "s", "b", CancellationToken.None).WaitAsync(Ceiling));

        Assert.StartsWith("SMTP:", error.Message, StringComparison.Ordinal);

        await accepted.CancelAsync();
        try
        {
            (await acceptTask).Dispose();
        }
        catch (OperationCanceledException)
        {
            // Клієнт так і не під'єднався — не предмет цього тесту.
        }
    }

    [Fact]
    [Trait("Requirement", "D-263")]
    public async Task Транспорт_що_не_чує_скасування_зупиняється_жорсткою_межею()
    {
        var never = new TaskCompletionSource();

        // ⛔ Мутація: прибрати `WaitAsync` (лише зведений токен) → задача, що ігнорує токен, висить; тест падає по Ceiling.
        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => SmtpNotificationSender
                .SendWithDeadlineAsync(_ => never.Task, TimeSpan.FromMilliseconds(100), CancellationToken.None)
                .WaitAsync(Ceiling));

        Assert.StartsWith("SMTP:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "D-263")]
    public async Task Зупинка_застосунку_лишається_OperationCanceled_а_не_таймаутом()
    {
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        // Зупинка не є відмовою каналу: диспетчер пропускає OCE зі зведеним токеном нагору.
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SmtpNotificationSender.SendWithDeadlineAsync(
                token => Task.Delay(Timeout.Infinite, token), TimeSpan.FromSeconds(10), stop.Token));

        Assert.IsNotType<TimeoutException>(error);
    }
}
