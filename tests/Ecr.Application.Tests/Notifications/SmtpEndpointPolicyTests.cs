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
    [InlineData("[fe80::1]:25")]
    [InlineData("127.0.0.1:25")]
    [InlineData("localhost:25")]
    [InlineData("LOCALHOST:25")]
    [InlineData("0.0.0.0:25")]
    [InlineData("169.254.169.254:25")]
    [InlineData("metadata.google.internal:25")]
    [InlineData("fd00:ec2::254")]
    [InlineData("[fd00:ec2::254]")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("127.0.0.2")]
    [InlineData("0.0.0.0")]
    [InlineData("localhost.")]
    [InlineData("LOCALHOST")]
    [InlineData("a.b.localhost")]
    [InlineData("Metadata.Google.Internal.")]
    [InlineData("169.254.170.2")]
    public async Task Loopback_у_будь_якому_записі_і_хибна_форма_хоста_відхиляються_на_сирому_рядку(string host)
    {
        // ⛔ Мутація: повернути `host.Trim().Trim('[', ']')` до розбору літерала → `[::1]:25` знову проходить.
        Assert.False(await Policy().IsHostAllowedAsync(host, failClosed: false, CancellationToken.None));
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
        => Assert.True(await Policy().IsHostAllowedAsync(host, failClosed: false, CancellationToken.None));

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ent6-S4")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.1.1")]
    [InlineData("fe80::1")]
    [InlineData("[fe80::1]")]
    [InlineData("metadata.google.internal")]
    [InlineData("metadata.goog.")]
    [InlineData("metadata.azure.internal")]
    [InlineData("localhost")]
    [InlineData("smtp.localhost")]
    public async Task Link_local_і_metadata_відхиляються_самою_політикою_без_Validate(string host)
    {
        // ⛔ Мутації (кожна окремо): прибрати IsCloudMetadataName; прибрати IsBlocked/IsBlockedAddress для літералів;
        // прибрати перевірку localhost. Викликаємо політику НАПРЯМУ — Validate цих випадків тут не відсікає.
        Assert.False(await Policy().IsHostAllowedAsync(host, failClosed: false, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ent6-S4")]
    public async Task Збій_тайм_аут_і_порожня_відповідь_DNS_закривають_пробу_і_не_ламають_чергу()
    {
        // ⛔ Мутація: `Unresolved` → завжди true (fail-open скрізь) або завжди false (fail-closed скрізь).
        var cases = new (string Name, Func<Task<IReadOnlyList<IPAddress>>> Resolve)[]
        {
            ("never", () => new TaskCompletionSource<IReadOnlyList<IPAddress>>().Task),
            ("socket", () => Task.FromException<IReadOnlyList<IPAddress>>(new System.Net.Sockets.SocketException(11001))),
            ("empty", () => Task.FromResult<IReadOnlyList<IPAddress>>([])),
        };

        foreach (var (name, resolve) in cases)
        {
            _net.Resolve = resolve;
            var policy = new SmtpEndpointPolicy(_net) { ResolveTimeout = TimeSpan.FromMilliseconds(50) };

            Assert.False(await policy.IsHostAllowedAsync("relay.corp.example", failClosed: true, CancellationToken.None), name);
            Assert.True(await policy.IsHostAllowedAsync("relay.corp.example", failClosed: false, CancellationToken.None), name);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ent6-S4")]
    public async Task Скасування_пробрасується_а_не_читається_як_тайм_аут()
    {
        _net.Resolve = () => new TaskCompletionSource<IReadOnlyList<IPAddress>>().Task;
        using var cts = new CancellationTokenSource();
        var policy = new SmtpEndpointPolicy(_net) { ResolveTimeout = TimeSpan.FromMinutes(5) };

        var pending = policy.IsHostAllowedAsync("relay.corp.example", failClosed: false, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ent6-S4")]
    public async Task Розв_язаний_у_loopback_відхиляється_і_в_режимі_черги()
    {
        _net.Resolve = () => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("127.0.0.1")]);

        Assert.False(await Policy().IsHostAllowedAsync("rebind.example", failClosed: false, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ent6-S4")]
    public async Task Суворий_режим_діє_лише_всередині_Begin_і_знімається_після()
    {
        Assert.False(SmtpEndpointStrictness.IsStrict);

        using (SmtpEndpointStrictness.Begin())
        {
            Assert.True(SmtpEndpointStrictness.IsStrict);
            await Task.Yield();
            Assert.True(SmtpEndpointStrictness.IsStrict);
        }

        Assert.False(SmtpEndpointStrictness.IsStrict);
    }

    private sealed class StubNetwork : IEndpointNetwork
    {
        public Func<Task<IReadOnlyList<IPAddress>>> Resolve { get; set; } =
            () => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("10.0.0.5")]);

        public IReadOnlyList<string> AllowedHosts => [];

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct) => Resolve();
    }
}