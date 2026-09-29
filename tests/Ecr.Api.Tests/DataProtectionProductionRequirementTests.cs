using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Ecr.Api.Auth;
using Ecr.TestKit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// S11 (аудит безпеки): у Production ключі кільця DataProtection — лише під
/// захистом сертифіката; без нього застосунок не стартує.
/// </summary>
/// <remarks>
/// ⛔ Експлойт, який закривається: без відбитка ключі лежать у
/// <c>sec.DataProtectionKey</c> відкрито, і хто читає базу чи її бекап,
/// підробляє cookie <c>ecr.auth</c> з <c>ecr:uid</c> будь-якого користувача.
///
/// ⚠ Перевірка через <see cref="IStartupValidator"/> — той самий механізм,
/// яким хост валить старт (<c>ValidateOnStart</c>), без підняття бази. Живий
/// старт — окремим тестом нижче (<see cref="DataProtectionProductionStartupTests"/>).
/// </remarks>
public sealed class DataProtectionProductionRequirementTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Production_без_сертифіката_валить_старт_з_названим_ключем()
    {
        using var provider = Build(Environments.Production, allowUnprotected: null);

        var error = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        // Повідомлення називає і ключ, і змінну служби: відмова без цього —
        // це «служба не піднімається» без жодної підказки.
        Assert.Contains(AuthenticationSetup.CertificateThumbprintKey, error.Message, StringComparison.Ordinal);
        Assert.Contains("ECR_Auth__DataProtection__CertificateThumbprint", error.Message, StringComparison.Ordinal);
        Assert.Contains("LocalMachine", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void Поза_Production_без_сертифіката_стартує_як_було(string environment)
    {
        using var provider = Build(environment, allowUnprotected: null);

        provider.GetRequiredService<IStartupValidator>().Validate();

        Assert.Null(provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Production_з_явною_згодою_стенда_стартує_але_пише_Critical_з_причиною()
    {
        var log = new RecordingLoggerProvider();
        using var provider = Build(
            Environments.Production,
            allowUnprotected: "true",
            extra: services => services.AddLogging(b => b.AddProvider(log)));

        provider.GetRequiredService<IStartupValidator>().Validate();

        // ⛔ Згода — не тихий режим: адміністратор бачить причину в журналі
        // старту (EventLog служби), а не лише в /health/db.
        var critical = Assert.Single(log.Entries, e => e.Level == LogLevel.Critical);
        Assert.Contains(AuthenticationSetup.AllowUnprotectedKeysKey, critical.Message, StringComparison.Ordinal);
        Assert.Contains(AuthenticationSetup.CertificateThumbprintKey, critical.Message, StringComparison.Ordinal);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData("Development", "true")]
    [InlineData("Development", null)]
    [InlineData("Production", "false")]
    public void Critical_про_згоду_лише_коли_згода_справді_відкрила_старт(string environment, string? allow)
    {
        var log = new RecordingLoggerProvider();
        using var provider = Build(
            environment, allowUnprotected: allow, extra: services => services.AddLogging(b => b.AddProvider(log)));

        try
        {
            provider.GetRequiredService<IStartupValidator>().Validate();
        }
        catch (OptionsValidationException)
        {
            // Production без згоди — відмова старту; предмет тут — журнал.
        }

        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Critical);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Production_зі_згодою_false_так_само_валить_старт()
    {
        using var provider = Build(Environments.Production, allowUnprotected: "false");

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Production_із_сертифікатом_стартує_і_ключ_у_сховищі_зашифрований()
    {
        // ⚠ Шов: сертифікат у пам'яті замість `LocalMachine\My`, сховище
        // ключів у пам'яті замість таблиці. Перевіряється саме те, що
        // вирішує S11: вимога задоволена ФАКТОМ шифратора, і XML ключа, який
        // потрапляє в сховище, не містить майстер-ключа відкрито.
        using var certificate = SelfSigned();
        var repository = new MemoryXmlRepository();

        using var provider = Build(
            Environments.Production,
            allowUnprotected: null,
            extra: services =>
            {
                services.AddDataProtection().ProtectKeysWithCertificate(certificate);
                services.Configure<KeyManagementOptions>(o => o.XmlRepository = repository);
            });

        provider.GetRequiredService<IStartupValidator>().Validate();
        Assert.NotNull(provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor);

        provider.GetRequiredService<IKeyManager>()
            .CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));

        var stored = Assert.Single(repository.GetAllElements()).ToString(SaveOptions.DisableFormatting);
        Assert.Contains("EncryptedData", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("<masterKey", stored, StringComparison.Ordinal);
    }

    private static ServiceProvider Build(
        string environment, string? allowUnprotected, Action<IServiceCollection>? extra = null)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Negotiate вимкнений із тієї ж причини, що і в `EcrApiFactory`.
            ["Auth:EnableNegotiate"] = "false",
        };

        if (allowUnprotected is not null)
        {
            settings[AuthenticationSetup.AllowUnprotectedKeysKey] = allowUnprotected;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new StubEnvironment(environment));
        services.AddEcrAuthentication(configuration);
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=ecr-s11-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class MemoryXmlRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = [];

        public IReadOnlyCollection<XElement> GetAllElements() => _elements.ToList();

        public void StoreElement(XElement element, string friendlyName) => _elements.Add(new XElement(element));
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Recorder(Entries);

        public void Dispose()
        {
        }

        private sealed class Recorder(List<(LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (entries)
                {
                    entries.Add((logLevel, formatter(state, exception)));
                }
            }
        }
    }

    private sealed class StubEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Ecr";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>
/// S11 живим стартом: застосунок у Production без сертифіката не піднімається.
/// </summary>
[Collection("SqlServer")]
public sealed class DataProtectionProductionStartupTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public void Production_без_сертифіката_застосунок_не_стартує()
    {
        // ⚠ Змінні оточення процесні: `OpenApiScalarAccessTests` ставить згоду
        // стенда в true — тут вона явно false, і після тесту знімається.
        Environment.SetEnvironmentVariable("ECR_Auth__DataProtection__AllowUnprotectedKeys", "false");
        Environment.SetEnvironmentVariable("ECR_Auth__DataProtection__CertificateThumbprint", null);
        try
        {
            using var app = new ProductionFactory(sql);

            var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());

            Assert.Contains(
                AuthenticationSetup.CertificateThumbprintKey,
                Flatten(error),
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ECR_Auth__DataProtection__AllowUnprotectedKeys", null);
        }
    }

    private static string Flatten(Exception error)
    {
        var parts = new List<string>();
        for (var e = error; e is not null; e = e.InnerException)
        {
            parts.Add(e.Message);
            if (e is AggregateException aggregate)
            {
                parts.AddRange(aggregate.InnerExceptions.Select(i => i.Message));
            }
        }

        return string.Join(Environment.NewLine, parts);
    }

    private sealed class ProductionFactory(SqlServerFixture sql) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            Environment.SetEnvironmentVariable("ECR_ConnectionStrings__Ecr", sql.ConnectionString);
            Environment.SetEnvironmentVariable("ECR_Schema__StartupMode", "Validate");
            Environment.SetEnvironmentVariable("ECR_Auth__RequireHttps", "false");
            Environment.SetEnvironmentVariable("ECR_Auth__EnableNegotiate", "false");
            Environment.SetEnvironmentVariable("ECR_Auth__StampCacheSeconds", "0");

            builder.UseEnvironment("Production");
            builder.ConfigureLogging(logging => logging.ClearProviders());
        }
    }
}
