using Ecr.Api.Auth;
using Ecr.Api.Health;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Чим захищені ключі кільця — і що застосунок робить, коли захисту немає
/// (`MI-01`, `D14-08`).
/// </summary>
/// <remarks>
/// ⚠ Кроку «Транспорт» у майстрі ще немає (`W0.1`), тож режим визначає один
/// ключ конфігурації. Перевіряються обидві гілки, і особливо та, де відбиток
/// заданий помилково: мовчазний відкат до незахищеного режиму дав би систему,
/// яка вважає себе захищеною.
/// </remarks>
public sealed class DataProtectionKeyProtectionModeTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Без_відбитка_режим_позначається_незахищеним()
    {
        var services = Configure(thumbprint: null);

        var protection = services
            .Single(d => d.ServiceType == typeof(DataProtectionKeyProtection))
            .ImplementationInstance as DataProtectionKeyProtection;

        Assert.NotNull(protection);
        Assert.False(protection.IsProtected);
        Assert.Null(protection.CertificateThumbprint);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Заданий_але_відсутній_відбиток_валить_старт_а_не_відкочується_тихо()
    {
        // ⛔ Саме відмова. Тихий відкат означав би, що адміністратор налаштував
        // захист, застосунок його не застосував, і дізнатися про це можна лише
        // з таблиці ключів — тобто вже після витоку.
        var missing = new string('A', 40);

        var error = Assert.Throws<InvalidOperationException>(() => Configure(missing));

        // Повідомлення називає і ключ конфігурації, і що робити далі: відмова
        // старту без цього перетворюється на «служба не піднімається».
        Assert.Contains(AuthenticationSetup.CertificateThumbprintKey, error.Message, StringComparison.Ordinal);
        Assert.Contains("LocalMachine", error.Message, StringComparison.Ordinal);
    }

    private static ServiceCollection Configure(string? thumbprint)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Negotiate вимкнений із тієї ж причини, що і в `EcrApiFactory`:
            // його обробник вимагає можливостей, яких поза Kestrel немає.
            ["Auth:EnableNegotiate"] = "false",
        };

        if (thumbprint is not null)
        {
            settings[AuthenticationSetup.CertificateThumbprintKey] = thumbprint;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddEcrAuthentication(configuration);
        return services;
    }
}
