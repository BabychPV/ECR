// tests/Ecr.Application.Tests/Notifications/SmtpEndpointPolicyTests.cs
using System.Net;
using Ecr.Application.Notifications;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Notifications;

/// <summary>
/// Політика напрямку пошти (ent6 S4) НАПРЯМУ, без <c>SaveSmtpSettingsHandler.Validate</c>: той раніше відсікає частину
/// випадків і маскував би видалення гілок політики (рев'ю sec-s4-2, P1/P2-2).
/// </summary>
public sealed class SmtpEndpointPolicyTests
{
    private readonly StubNetwork _net = new();

    private SmtpEndpointPolicy Policy() => new(_net);

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ent6-S4")]
    [InlineData("[::1]:25")]
    [InlineData("[::ffff:127.0.0.1]:25")]
    [InlineData("[0:0:0:0:0:0:0:1]:1")]
    [InlineData("[::1]")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:7f00:1")]
    [InlineData("127.0.0.1")]
    [InlineData("127.1")]
    [InlineData("0x7f.1")]
    [InlineData("2130706433")]
    [InlineData("::1%1")]
    [InlineData("::127.0.0.1")]
    [InlineData("::")]
    [InlineData("[2001:db8::1]:25")]       // порт у полі Server навіть при дозволеній адресі
    [InlineData("relay.corp.example:25")]
    [InlineData("[relay.corp.example]")]
    [InlineData("a/b")]
    public async Task Loopback_у_будь_якому_записі_і_хибна_форма_хоста_відхиляються_на_сирому_рядку(string host)
    {
        // ⛔ Мутація: повернути `host.Trim().Trim('[', ']')` до розбору літерала → `[::1]:25` знову проходить.
        Assert.False(await Policy().IsHostAllowedAsync(host, CancellationToken.None));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ent6-S4")]
    [InlineData("10.1.2.3")]
    [InlineData("[2001:db8::1]")]
    [InlineData("fd00::5")]
    [InlineData("relay.corp.example")]
    [InlineData("mail_relay-1.corp.example.")]
    public async Task Приватні_й_звичайні_адреси_та_імена_проходять(string host)
        => Assert.True(await Policy().IsHostAllowedAsync(host, CancellationToken.None));

    private sealed class StubNetwork : IEndpointNetwork
    {
        public IReadOnlyList<string> AllowedHosts => [];

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("10.0.0.5")]);
    }
}