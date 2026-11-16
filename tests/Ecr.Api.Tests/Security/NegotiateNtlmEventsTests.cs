// tests/Ecr.Api.Tests/Security/NegotiateNtlmEventsTests.cs
using System.Security.Claims;
using Ecr.Api.Auth;
using Ecr.TestKit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// PT-4 (F13 AU, 16.11): відмову NTLM перевірено на справжніх опціях Negotiate, які будує
/// <see cref="AuthenticationSetup.AddEcrAuthentication"/>, а не лише на <see cref="AuthenticationSetup.IsKerberos"/>.
/// </summary>
/// <remarks>
/// Мутації (для AU): умова <c>!allowNtlm &amp;&amp; !IsKerberos(...)</c> у <c>OnAuthenticated</c> → <c>&amp;&amp; false</c>
/// червонить <see cref="За_замовчуванням_NTLM_відхилено_а_Kerberos_прийнято"/>; дефолт <c>AllowNtlm</c> → <c>true</c>
/// червонить її ж; ключ ігнорується (завжди відмова) — червонить <see cref="З_AllowNtlm_true_приймаються_обидва"/>.
/// </remarks>
public sealed class NegotiateNtlmEventsTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "PT-4")]
    public async Task За_замовчуванням_NTLM_відхилено_а_Kerberos_прийнято()
    {
        using var provider = Build(new Dictionary<string, string?>());

        Assert.True(await FailsAsync(provider, "NTLM"), "Без ключа NTLM має відхилятись.");
        Assert.False(await FailsAsync(provider, "Kerberos"), "Kerberos має прийматись.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "PT-4")]
    public async Task З_AllowNtlm_true_приймаються_обидва()
    {
        using var provider = Build(new Dictionary<string, string?> { [AuthenticationSetup.AllowNtlmKey] = "true" });

        Assert.False(await FailsAsync(provider, "NTLM"), "З AllowNtlm=true NTLM має прийматись.");
        Assert.False(await FailsAsync(provider, "Kerberos"), "Kerberos має прийматись.");
    }

    /// <summary>Викликає <c>OnAuthenticated</c> опцій Negotiate з суб'єктом заданого пакета.</summary>
    /// <returns><c>true</c> — подія відхилила вхід.</returns>
    private static async Task<bool> FailsAsync(ServiceProvider provider, string package)
    {
        var options = provider.GetRequiredService<IOptionsMonitor<NegotiateOptions>>()
            .Get(NegotiateDefaults.AuthenticationScheme);
        var scheme = new AuthenticationScheme(
            NegotiateDefaults.AuthenticationScheme, NegotiateDefaults.AuthenticationScheme, typeof(NegotiateHandler));
        var context = new AuthenticatedContext(new DefaultHttpContext(), scheme, options)
        {
            Principal = new ClaimsPrincipal(new ClaimsIdentity(package)),
        };

        Assert.NotNull(options.Events);
        await options.Events.OnAuthenticated(context);

        return context.Result?.Failure is not null;
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        settings["Auth:EnableNegotiate"] = "true";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new StubEnvironment());
        services.AddEcrAuthentication(configuration);
        return services.BuildServiceProvider();
    }

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Ecr";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
