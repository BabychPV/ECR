// tests/Ecr.Api.Tests/Startup/CalculationLimitsBindingTests.cs

using Ecr.Calculations;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests.Startup;

/// <summary>
/// ФВ-9.8 (<c>D-205</c>): ліміти перерахунку із секції <c>Calculations</c> доходять
/// до оркестратора через справжній <c>Program.cs</c>, а не лише перевіряються на старті.
/// </summary>
/// <remarks>
/// ⛔ <c>Ecr.Calculations</c> не знає <c>IConfiguration</c>: зв'язує ліміти
/// <c>Program.cs</c>. Без цього рядка ключі проходили б
/// <c>EcrConfigurationValidation</c> і не робили б нічого. Мутація «повернути
/// <c>AddEcrCalculations()</c> без аргументу» робить перший тест червоним.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationLimitsBindingTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.8")]
    public void Налаштування_Calculations_доходять_до_оркестратора()
    {
        using var app = new EcrApiFactory(sql);
        using var configured = app.WithWebHostBuilder(b => b
            .UseSetting("Calculations:MaxParallelism", "2")
            .UseSetting("Calculations:MaxInputCellsPerBinding", "12345"));

        var limits = configured.Services.GetRequiredService<CalculationLimits>();

        Assert.Equal(2, limits.MaxParallelism);
        Assert.Equal(12345, limits.MaxInputCellsPerBinding);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.8")]
    public void Без_налаштувань_ліміти_типові()
    {
        // ⚠ Літералами: appsettings.json несе ті самі 4 і 300 000, що й дефолти коду.
        using var app = new EcrApiFactory(sql);

        var limits = app.Services.GetRequiredService<CalculationLimits>();

        Assert.Equal(4, limits.MaxParallelism);
        Assert.Equal(300_000, limits.MaxInputCellsPerBinding);
    }
}
