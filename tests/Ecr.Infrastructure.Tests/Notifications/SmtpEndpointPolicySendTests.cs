// tests/Ecr.Infrastructure.Tests/Notifications/SmtpEndpointPolicySendTests.cs
using Ecr.Application.Notifications;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Integration;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Notifications;

/// <summary>
/// ent6 S4: політика напрямку діє на ВІДПРАВЛЕННЯ (а не лише на збереження): порт і хост із запасного
/// <c>Smtp:*</c> чи старого рядка БД перевіряються перед з'єднанням. Усі випадки відмовляють ДО мережі.
/// </summary>
public sealed class SmtpEndpointPolicySendTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    private static SmtpNotificationSender Sender(params (string Key, string Value)[] values)
        => new(Config(values), Substitute.For<ISecretProvider>());

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ent6-S4")]
    [InlineData("10.0.0.5", "22")]          // приватний relay, але порт не поштовий
    [InlineData("10.0.0.5", "3306")]
    [InlineData("127.0.0.1", "587")]        // стандартний порт, але loopback
    [InlineData("localhost", "25")]
    [InlineData("[::1]:25", "587")]                // P1: справжній EndpointNetwork і справжній SocketException
    [InlineData("[::ffff:127.0.0.1]:25", "587")]
    [InlineData("169.254.169.254", "587")]  // metadata
    public async Task Відправлення_відхиляє_нестандартний_порт_і_loopback_link_local_до_будь_якого_з_єднання(string host, string port)
    {
        var sender = Sender(("Smtp:Host", host), ("Smtp:Port", port), ("Smtp:From", "ecr@corp.example"), ("Smtp:UseStartTls", "false"));

        // ⛔ Мутація: прибрати перевірку політики в SmtpNotificationSender.SendAsync → виняток стане мережевим (або лист піде).
        await Assert.ThrowsAsync<SmtpEndpointForbiddenException>(
            () => sender.SendAsync(["a@corp.example"], "s", "b", CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ent6-S4")]
    public async Task Нерозв_язане_ім_я_відправлення_в_черзі_йде_далі_а_в_пробі_закрите()
    {
        var net = Substitute.For<IEndpointNetwork>();
        net.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<System.Net.IPAddress>>([]));
        var sender = new SmtpNotificationSender(
            Config(("Smtp:Host", "no-such-relay.invalid"), ("Smtp:Port", "587"), ("Smtp:From", "ecr@corp.example")),
            Substitute.For<ISecretProvider>(), endpointPolicy: new SmtpEndpointPolicy(net));

        // Черга: політика пропускає (fail-open), далі — справжня мережева відмова DNS, а НЕ відмова політики.
        var queued = await Assert.ThrowsAnyAsync<Exception>(
            () => sender.SendAsync(["a@corp.example"], "s", "b", CancellationToken.None));
        Assert.IsNotType<SmtpEndpointForbiddenException>(queued);

        // Проба: fail-closed ДО мережі.
        using (SmtpEndpointStrictness.Begin())
        {
            await Assert.ThrowsAsync<SmtpEndpointForbiddenException>(
                () => sender.SendAsync(["a@corp.example"], "s", "b", CancellationToken.None));
        }
    }
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ent6-S4")]
    public async Task Smtp_AllowedPorts_відкриває_порт_але_не_loopback()
    {
        var config = Config(("Smtp:AllowedPorts:0", "8025"), ("Smtp:AllowedPorts:1", "not-a-port"), ("Smtp:AllowedPorts:2", "70000"));
        var policy = new SmtpEndpointPolicy(new EndpointNetwork(config));

        // ⛔ Мутація: ігнорувати Smtp:AllowedPorts у EndpointNetwork → 8025 закритий; не фільтрувати межі → 70000 відкритий.
        Assert.True(policy.IsPortAllowed(8025));
        Assert.True(policy.IsPortAllowed(587));
        Assert.False(policy.IsPortAllowed(22));
        Assert.False(policy.IsPortAllowed(70000));
        Assert.False(await policy.IsHostAllowedAsync("127.0.0.1", failClosed: false, CancellationToken.None));
        Assert.True(await policy.IsHostAllowedAsync("10.1.2.3", failClosed: false, CancellationToken.None));
    }
}