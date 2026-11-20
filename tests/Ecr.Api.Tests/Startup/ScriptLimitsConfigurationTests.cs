// tests/Ecr.Api.Tests/Startup/ScriptLimitsConfigurationTests.cs

using Ecr.Api.Options;
using Ecr.Api.Security;
using Ecr.Calculations;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ecr.Api.Tests.Startup;

/// <summary>
/// PSS-21: ключі меж скрипта — бюджет рядка (<c>Calculations:MaxRowEvaluationSteps</c>), частота й одночасність прогонів і
/// перевірок, межа очікування в черзі — перевіряються на старті (<c>U19</c>): недійсне значення зупиняє службу з назвою
/// ключа, а не мовчки стає дефолтом (досі ворота затискали його <c>Math.Max</c>).
/// </summary>
/// <remarks>
/// Окремий файл від <c>EcrConfigurationValidationTests</c>: ключі лейну L7 — окремим блоком у
/// <c>EcrConfigurationValidation.Integers</c>. Мутація: прибрати рядок ключа з переліку — його рядок тут червоніє.
/// </remarks>
public sealed class ScriptLimitsConfigurationTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "PSS-21")]
    [InlineData("Calculations:MaxRowEvaluationSteps", "19999", "20000")]
    [InlineData("Calculations:MaxRowEvaluationSteps", "20 млн", "20000000")]
    [InlineData(ScriptAuthoringRateLimitPolicy.PermitKey, "0", "1")]
    [InlineData(ScriptCheckRateLimitPolicy.PermitKey, "-1", "1")]
    [InlineData(ScriptRunGate.ConcurrencyKey, "0", "1")]
    [InlineData(ScriptRunGate.QueueLimitKey, "-1", "0")]
    [InlineData(ScriptRunGate.QueueWaitSecondsKey, "0", "1")]
    [InlineData(ScriptRunGate.QueueWaitSecondsKey, "10s", "10")]
    public void Недійсна_межа_скрипта_зупиняє_старт_а_найменша_допустима_проходить(string key, string invalid, string smallest)
    {
        var problem = Assert.Single(EcrConfigurationValidation.Validate(Config(key, invalid)));
        Assert.StartsWith(key + " = «" + invalid + "»", problem, StringComparison.Ordinal);

        Assert.Empty(EcrConfigurationValidation.Validate(Config(key, smallest)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "PSS-21")]
    public void Найменший_бюджет_рядка_на_старті_той_самий_що_в_CalculationLimits()
    {
        // Перелік старту тримає літерал (його звіряє сторож воркера по тексту); розійтися з кодом він не має права.
        Assert.Empty(EcrConfigurationValidation.Validate(
            Config("Calculations:MaxRowEvaluationSteps", CalculationLimits.MinRowEvaluationSteps.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        Assert.Single(EcrConfigurationValidation.Validate(
            Config("Calculations:MaxRowEvaluationSteps", (CalculationLimits.MinRowEvaluationSteps - 1).ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    private static IConfiguration Config(string key, string value)
        => new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>(key, value)])
            .Build();
}
