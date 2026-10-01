using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Ecr.Api.Auth;
using Ecr.Api.Health;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-267: заміна сертифіката Data Protection не має робити старі ключі кільця
/// нечитабельними — <c>Auth:DataProtection:PreviousCertificateThumbprints</c>.
/// </summary>
/// <remarks>
/// ⚠ Шов: пошук сертифіката — делегат (сертифікати в пам'яті), сховище ключів —
/// у пам'яті. Системні сховища сертифікатів не чіпаються.
/// </remarks>
public sealed class DataProtectionPreviousCertificateTests
{
    private const string Purpose = "d267-purpose";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Без_PreviousCertificateThumbprints_поведінка_та_сама_ключ_попереднього_сертифіката_нечитабельний()
    {
        // (а)+(в): контрольний випадок — B без Previous не читає дані, захищені A.
        using var a = SelfSigned();
        using var b = SelfSigned();
        var repository = new MemoryXmlRepository();
        var payload = Protect(a, repository);

        using var provider = Build(b, repository, previous: null, [a, b]);
        provider.GetRequiredService<IStartupValidator>().Validate();

        Assert.Empty(AuthenticationSetup.ParsePreviousThumbprints(Configuration(b, null)));
        var protector = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
        Assert.Throws<CryptographicException>(() => protector.Unprotect(payload));
        Assert.Equal([b.Thumbprint], provider.GetRequiredService<DataProtectionKeyProtection>().ReadableThumbprints);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Previous_містить_A_старі_дані_читаються_після_переходу_на_B()
    {
        // (б)
        using var a = SelfSigned();
        using var b = SelfSigned();
        var repository = new MemoryXmlRepository();
        var payload = Protect(a, repository);

        using var provider = Build(b, repository, previous: a.Thumbprint, [a, b]);
        provider.GetRequiredService<IStartupValidator>().Validate();

        var protector = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
        Assert.Equal("secret-value", protector.Unprotect(payload));
        Assert.Contains(a.Thumbprint, provider.GetRequiredService<DataProtectionKeyProtection>().ReadableThumbprints!);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Previous_приймає_список_через_крапку_з_комою_і_індексовану_форму_з_пробілами()
    {
        var spaced = string.Join(' ', "AB12CD34".Chunk(2).Select(c => new string(c)));
        var fromList = AuthenticationSetup.ParsePreviousThumbprints(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AuthenticationSetup.PreviousCertificateThumbprintsKey] = $"{spaced}; ef56 ,AB12CD34",
            }).Build());
        Assert.Equal(["AB12CD34", "EF56"], fromList);

        var fromArray = AuthenticationSetup.ParsePreviousThumbprints(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AuthenticationSetup.PreviousCertificateThumbprintsKey + ":0"] = "aa11",
                [AuthenticationSetup.PreviousCertificateThumbprintsKey + ":1"] = "bb22",
            }).Build());
        Assert.Equal(["AA11", "BB22"], fromArray);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Ненайдений_попередній_відбиток_не_валить_старт()
    {
        using var b = SelfSigned();
        using var provider = Build(b, new MemoryXmlRepository(), previous: new string('A', 40), [b]);

        provider.GetRequiredService<IStartupValidator>().Validate();

        Assert.Equal([b.Thumbprint], provider.GetRequiredService<DataProtectionKeyProtection>().ReadableThumbprints);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Відбитки_у_XML_кільця_видобуваються_з_реального_ключа()
    {
        using var a = SelfSigned();
        var repository = new MemoryXmlRepository();
        Protect(a, repository);

        var found = DataProtectionKeyProtection.ThumbprintsInKeyRing(
            repository.GetAllElements().Select(e => e.ToString(SaveOptions.DisableFormatting)));

        Assert.Equal([a.Thumbprint], found);
    }

    private static string Protect(X509Certificate2 certificate, MemoryXmlRepository repository)
    {
        using var provider = Build(certificate, repository, previous: null, [certificate]);
        return provider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(Purpose).Protect("secret-value");
    }

    private static IConfiguration Configuration(X509Certificate2 current, string? previous)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Auth:EnableNegotiate"] = "false",
            [AuthenticationSetup.CertificateThumbprintKey] = current.Thumbprint,
        };
        if (previous is not null)
        {
            settings[AuthenticationSetup.PreviousCertificateThumbprintsKey] = previous;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static ServiceProvider Build(
        X509Certificate2 current, MemoryXmlRepository repository, string? previous, X509Certificate2[] store)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new StubEnvironment(Environments.Production));
        services.AddEcrAuthentication(
            Configuration(current, previous),
            thumbprint => store.FirstOrDefault(c => string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase)));
        services.Configure<KeyManagementOptions>(o => o.XmlRepository = repository);
        return services.BuildServiceProvider();
    }

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=ecr-d267-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    internal sealed class MemoryXmlRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = [];

        public IReadOnlyCollection<XElement> GetAllElements() => _elements.ToList();

        public void StoreElement(XElement element, string friendlyName) => _elements.Add(new XElement(element));
    }

    private sealed class StubEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Ecr";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>D-267: <c>/health/db</c> не каже «ключі захищені», коли кільце зашифроване недоступним сертифікатом.</summary>
[Collection("SqlServer")]
public sealed class DatabaseHealthUnreadableKeyRingTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кільце_зашифроване_невідомим_сертифікатом_Degraded_з_відбитком()
    {
        using var unknown = Cert();
        using var current = Cert();
        var xml = KeyXml(unknown);

        await using var db = sql.CreateContext();
        var row = new DataProtectionKey { FriendlyName = "d267-test", Xml = xml };
        db.DataProtectionKeys.Add(row);
        await db.SaveChangesAsync();
        try
        {
            var result = await CheckAsync(db, DataProtectionKeyProtection.ProtectedBy(current.Thumbprint, [current.Thumbprint]));

            Assert.True(
                result.Status == HealthStatus.Degraded,
                $"{result.Status}: {result.Description}; rows={await db.DataProtectionKeys.CountAsync()}; "
                + $"unreadable={(result.Data.TryGetValue("unreadableKeyCertificates", out var u) ? string.Join(',', (IEnumerable<string>)u) : "-")}");
            Assert.Contains(unknown.Thumbprint, result.Description, StringComparison.Ordinal);
            Assert.Contains("PreviousCertificateThumbprints", result.Description, StringComparison.Ordinal);
            Assert.DoesNotContain("<", result.Description, StringComparison.Ordinal);

            // Той самий сертифікат у списку читабельних (Previous) — стан знімається.
            var ok = await CheckAsync(db, DataProtectionKeyProtection.ProtectedBy(current.Thumbprint, [current.Thumbprint, unknown.Thumbprint]));
            Assert.NotEqual(HealthStatus.Degraded, ok.Status);
            Assert.DoesNotContain(unknown.Thumbprint, ok.Description ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            db.DataProtectionKeys.Remove(row);
            await db.SaveChangesAsync();
        }
    }

    private static X509Certificate2 Cert()
    {
        using var rsa = RSA.Create(2048);
        return new CertificateRequest("CN=ecr-d267-health", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static string KeyXml(X509Certificate2 certificate)
    {
        var repository = new DataProtectionPreviousCertificateTests.MemoryXmlRepository();
        var services = new ServiceCollection();
        services.AddDataProtection().ProtectKeysWithCertificate(certificate);
        services.Configure<KeyManagementOptions>(o => o.XmlRepository = repository);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
        return repository.GetAllElements().Single().ToString(SaveOptions.DisableFormatting);
    }

    private static async Task<HealthCheckResult> CheckAsync(
        Ecr.Infrastructure.Persistence.EcrDbContext db, DataProtectionKeyProtection protection)
    {
        var capabilities = Substitute.For<ISqlCapabilities>();
        capabilities.SupportsOnlineIndexRebuild.Returns(true);
        capabilities.SupportsResourceGovernor.Returns(true);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc));
        var host = Substitute.For<IHostEnvironment>();
        host.EnvironmentName.Returns(Environments.Production);
        host.ContentRootFileProvider.Returns(new NullFileProvider());

        var check = new DatabaseHealthCheck(
            capabilities, db, clock, Substitute.For<IUiStringCatalog>(), Substitute.For<ICurrentUser>(), protection, host);
        return await check.CheckHealthAsync(
            new HealthCheckContext
            {
                Registration = new HealthCheckRegistration("db", Substitute.For<IHealthCheck>(), null, null),
            },
            CancellationToken.None);
    }
}
