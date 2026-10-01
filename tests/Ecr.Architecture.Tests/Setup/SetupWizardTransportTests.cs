using Ecr.Setup;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests.Setup;

/// <summary>
/// D14-08, майстер встановлення: крок «Транспорт» — явний вибір HTTPS / проксі / HTTP-стенд, який
/// доїжджає до <c>deploy-ecr.ps1</c> єдиним із трьох параметрів.
/// </summary>
/// <remarks>
/// ⚠ Код майстра підключено посиланням на файли; сховище сертифікатів — шов
/// <see cref="ICertificateSource"/>. Самі WinForms-екрани (<c>TransportStep</c>, рядок «Огляду»)
/// тут не перевіряються — лише стан і параметри; вигляд екрана візуально не перевірено.
/// </remarks>
public sealed class SetupWizardTransportTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Local);

    private static readonly CertificateInfo Valid = new(
        "0123456789ABCDEF0123456789ABCDEF01234567", "CN=ecr.customer.example", Now.AddYears(-1), Now.AddYears(1), HasPrivateKey: true);

    private static readonly CertificateInfo Expired = new(
        "1123456789ABCDEF0123456789ABCDEF01234567", "CN=old", Now.AddYears(-3), Now.AddDays(-1), HasPrivateKey: true);

    /// <remarks>
    /// ⛔ Головне: майстер завжди передає РІВНО ОДИН транспорт, і жоден режим не підміняє інший.
    /// Мутація (прогнано): у <c>DeployArguments</c> для Proxy додати ще й <c>AllowHttp</c> → червоний.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("Https", "HttpsThumbprint")]
    [InlineData("Proxy", "BehindHttpsProxy")]
    [InlineData("Http", "AllowHttp")]
    public void Майстер_передає_рівно_один_параметр_транспорту(string transportName, string expected)
    {
        var state = new WizardState { Transport = Enum.Parse<WizardTransport>(transportName), HttpsThumbprint = Valid.Thumbprint };

        var names = DeployArguments.Build(state).Select(a => a.Key)
            .Where(n => n is "HttpsThumbprint" or "BehindHttpsProxy" or "AllowHttp")
            .ToList();

        Assert.Equal([expected], names);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Типовий_вибір_HTTPS_а_HTTP_лише_явний_вибір()
    {
        var state = new WizardState();

        Assert.Equal(WizardTransport.Https, state.Transport);
        Assert.Null(state.HttpsThumbprint);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Значення_параметра_HTTPS_це_нормалізований_відбиток_а_для_проксі_й_HTTP_прапорець()
    {
        var https = DeployArguments.Build(new WizardState { HttpsThumbprint = "01 23 45 67 89 ab cd ef 01 23 45 67 89 ab cd ef 01 23 45 67" });
        Assert.Equal(Valid.Thumbprint, Assert.Single(https, a => a.Key == "HttpsThumbprint").Value);

        var proxy = DeployArguments.Build(new WizardState { Transport = WizardTransport.Proxy });
        Assert.Null(Assert.Single(proxy, a => a.Key == "BehindHttpsProxy").Value);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("порожньо", "Select the HTTPS certificate")]
    [InlineData("зник", "no longer in Cert:\\LocalMachine\\My")]
    [InlineData("прострочений", "expired")]
    public void HTTPS_без_придатного_сертифіката_не_проходить_перевірку(string @case, string expected)
    {
        var (thumbprint, store) = @case switch
        {
            "порожньо" => ((string?)null, new[] { Valid }),
            "зник" => (Valid.Thumbprint, Array.Empty<CertificateInfo>()),
            "прострочений" => (Expired.Thumbprint, new[] { Expired }),
            _ => throw new ArgumentOutOfRangeException(nameof(@case), @case, null),
        };
        var state = new WizardState { Transport = WizardTransport.Https, HttpsThumbprint = thumbprint };

        Assert.False(state.TryValidateTransport(new FakeSource(store), Now, out var error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Проксі й HTTP-стенд сертифіката не потребують: перевірка їх не блокує, навіть коли сховище порожнє.
    /// Мутація (прогнано): у <c>TryValidateTransport</c> прибрати ранній вихід для не-HTTPS → червоний.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("Proxy")]
    [InlineData("Http")]
    public void Проксі_й_HTTP_не_вимагають_сертифіката(string transportName)
    {
        var state = new WizardState { Transport = Enum.Parse<WizardTransport>(transportName) };

        Assert.True(state.TryValidateTransport(new FakeSource(), Now, out var error), error);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Чинний_сертифікат_HTTPS_проходить_перевірку()
    {
        var state = new WizardState { Transport = WizardTransport.Https, HttpsThumbprint = Valid.Thumbprint.ToLowerInvariant() };

        Assert.True(state.TryValidateTransport(new FakeSource(Valid), Now, out var error), error);
    }

    private sealed class FakeSource(params CertificateInfo[] items) : ICertificateSource
    {
        public IReadOnlyList<CertificateInfo> List() => items;
    }
}
