// tests/Ecr.Domain.Tests/Calculations/MethodologyVersionDeletionTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Calculations;

/// <summary>Видалити можна лише чернетку, якою ще не рахували (<c>BE-25</c>).</summary>
public sealed class MethodologyVersionDeletionTests
{
    private static readonly DateTime Now = new(2026, 2, 10, 12, 0, 0, DateTimeKind.Utc);

    private static MethodologyVersion Draft() => new(1, "2.0", CalculationLevel.Configuration, createdByUserId: 7, Now);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Чернетка_без_розрахунків_видаляється()
        => Draft().EnsureDeletable(usedInCalculations: false);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Опублікована_версія_дає_0409_з_причиною_стану()
    {
        var version = Draft();
        version.Publish(publishedByUserId: 9, "first", new DateOnly(2026, 1, 1), testsPassed: true, Now);

        var error = Assert.Throws<DomainException>(() => version.EnsureDeletable(usedInCalculations: false));

        Assert.Equal("ECR-CALC-0409", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0409.versionNotDraft", error.Details!["messageKey"]);
        Assert.Equal("Published", error.Details["reason"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Чернетка_якою_вже_рахували_не_видаляється()
    {
        var error = Assert.Throws<DomainException>(() => Draft().EnsureDeletable(usedInCalculations: true));

        Assert.Equal("ECR-CALC-0409", error.ErrorCode);
        Assert.Equal("UsedInCalculations", error.Details!["reason"]);
    }

    /// <summary>
    /// Заголовок <c>ECR-CALC-0409</c> нейтральний, тож відмову чотирьох очей від
    /// відмови видалення відрізняє лише <c>messageKey</c>.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Чотири_очі_і_видалення_мають_той_самий_код_але_різні_messageKey()
    {
        var fourEyes = Assert.Throws<DomainException>(
            () => Draft().Publish(publishedByUserId: 7, "first", new DateOnly(2026, 1, 1), testsPassed: true, Now));
        var deletion = Assert.Throws<DomainException>(() => Draft().EnsureDeletable(usedInCalculations: true));

        Assert.Equal(deletion.ErrorCode, fourEyes.ErrorCode);
        Assert.NotNull(fourEyes.Details);
        Assert.Equal("err.ECR-CALC-0409.authorCannotPublish", fourEyes.Details["messageKey"]);
        Assert.Equal("2.0", fourEyes.Details["version"]);
        Assert.Equal("err.ECR-CALC-0409.versionUsedInCalculations", deletion.Details!["messageKey"]);
    }
}
