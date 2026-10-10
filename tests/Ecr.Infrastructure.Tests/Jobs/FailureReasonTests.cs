// tests/Ecr.Infrastructure.Tests/Jobs/FailureReasonTests.cs
using Ecr.Application.Errors;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>Y4-05: причина відмови для параметра локалізованого тексту - ключ чи код, а не текст винятку.</summary>
public sealed class FailureReasonTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "Y4-05")]
    public void Y4_05_ключ_каталогу_якщо_є_інакше_код_а_текст_і_чужі_виняток_не_витікають()
    {
        var withKey = new BusinessRuleException(
            "ECR-UOM-0422", "Конверсія неможлива.", new Dictionary<string, object?> { ["messageKey"] = "err.ECR-UOM-0422.zeroFactor" });
        var domainWithKey = new DomainException(
            "ECR-UOM-0422", "Різні розмірності.", new Dictionary<string, object?> { ["messageKey"] = "err.ECR-UOM-0422.incompatibleDimensions" });
        var withoutKey = new DomainException("ECR-CELL-0422", "Значення відхилено.");
        var blankKey = new BusinessRuleException(
            "ECR-X-0001", "Текст.", new Dictionary<string, object?> { ["messageKey"] = " " });
        var foreign = new InvalidOperationException("Cannot insert duplicate key in object 'dbo.t' (Password=secret)");

        Assert.Equal("err.ECR-UOM-0422.zeroFactor", FailureReason.Of(withKey));
        Assert.Equal("err.ECR-UOM-0422.incompatibleDimensions", FailureReason.Of(domainWithKey));
        Assert.Equal("ECR-CELL-0422", FailureReason.Of(withoutKey));
        Assert.Equal("ECR-X-0001", FailureReason.Of(blankKey));
        Assert.Equal(nameof(InvalidOperationException), FailureReason.Of(foreign));
    }
}
