using Ecr.Setup;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests.Setup;

/// <summary>
/// PSS-28, майстер встановлення: імена хоста (<c>AllowedHosts</c>) пропонуються з DNS-імен сертифіката HTTPS,
/// редагуються, <c>*</c> лишається явним вибором, а значення доїжджає до <c>deploy-ecr.ps1 -AllowedHosts</c>.
/// </summary>
/// <remarks>
/// ⚠ WinForms-екран (<c>TransportStep</c>) тут не перевіряється — лише правила й параметри.
/// Мутації (прогнано): <c>FromCertificate</c> без <c>localhost</c> → червоний
/// <c>Пропозиція_з_сертифіката_містить_імена_і_localhost</c>; <c>TryNormalize</c> приймає порожній рядок →
/// червоний <c>Порожнє_значення_відхиляється</c>; <c>DeployArguments</c> не додає <c>AllowedHosts</c> →
/// червоний <c>Імена_хоста_передаються_скрипту_явно</c>; порт знову дозволено в шаблоні й без <c>HasPort</c> →
/// червоний <c>Ім_я_з_портом_відхиляється_з_поясненням</c>.
/// </remarks>
public sealed class SetupWizardAllowedHostsTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Пропозиція_з_сертифіката_містить_імена_і_localhost()
    {
        var value = AllowedHostsRules.FromCertificate(["ECR.Customer.Example", "ecr", "ecr.customer.example"]);

        Assert.Equal("ecr.customer.example;ecr;localhost", value);
        Assert.True(AllowedHostsRules.TryNormalize(value, out _, out _));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData(null)]
    [InlineData("")]
    public void Сертифікат_без_імен_дає_явну_зірочку(string? names)
    {
        Assert.Equal("*", AllowedHostsRules.FromCertificate(names is null ? null : []));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("*", "*")]
    [InlineData(" ecr.example.com ; *.example.com,localhost ", "ecr.example.com;*.example.com;localhost")]
    [InlineData("[::1];localhost", "[::1];localhost")]
    [InlineData("ECR.example.com ecr.example.com", "ECR.example.com")]
    public void Коректне_значення_нормалізується(string input, string expected)
    {
        Assert.True(AllowedHostsRules.TryNormalize(input, out var normalized, out var error), error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ;, ")]
    [InlineData("*;ecr.example.com")]
    [InlineData("https://ecr.example.com")]
    [InlineData("ecr.example.com/path")]
    [InlineData("-bad.example.com")]
    [InlineData("ecr.example.com:99999")]
    [InlineData("*.")]
    public void Порожнє_або_хибне_значення_відхиляється(string? input)
    {
        Assert.False(AllowedHostsRules.TryNormalize(input, out var normalized, out var error));
        Assert.Empty(normalized);
        Assert.NotEmpty(error);
    }

    /// <summary>
    /// ⛔ Рецензія L9 H1: фільтр хостів ASP.NET Core порівнює записи AllowedHosts БЕЗ порту запиту, тож запис із портом
    /// не збігається ніколи — усі користувачі отримали б 400, а перевірка розгортання на <c>localhost</c> лишилась би
    /// зеленою. Порт відхиляється з поясненням, а не мовчки.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("ecr.example.com:8443")]
    [InlineData("localhost:5000;ecr")]
    [InlineData("[::1]:8443")]
    public void Ім_я_з_портом_відхиляється_з_поясненням(string input)
    {
        Assert.False(AllowedHostsRules.TryNormalize(input, out var normalized, out var error));
        Assert.Empty(normalized);
        Assert.Contains("port", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Імена_хоста_передаються_скрипту_явно()
    {
        var state = new WizardState { AllowedHosts = "ecr.example.com;localhost" };

        var arguments = DeployArguments.Build(state);

        Assert.Equal("ecr.example.com;localhost", Assert.Single(arguments, a => a.Key == "AllowedHosts").Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Явна_зірочка_теж_передається_а_без_кроку_параметра_немає()
    {
        Assert.Equal("*", Assert.Single(DeployArguments.Build(new WizardState { AllowedHosts = "*" }), a => a.Key == "AllowedHosts").Value);
        Assert.DoesNotContain(DeployArguments.Build(new WizardState()), a => a.Key == "AllowedHosts");
    }
}
