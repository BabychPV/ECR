// tests/Ecr.Domain.Tests/Calculations/MethodologyCategoryRuleTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Calculations;

/// <summary>
/// Правило категорії константи (L-2, <c>calc.CategoryRule</c>) править лише чернетка, а порожній
/// вираз не приймається.
/// </summary>
/// <remarks>
/// ⛔ Зміна ключа категорії в опублікованій версії тихо міняє, яку константу бере кожен рядок
/// (Diesel замість Gas), без diff-у й сліду публікації. Тому перевіряється сутність.
///
/// Мутаційний доказ: прибрати <c>RequireDraft</c> у <c>SetCategoryRule</c> — червоні
/// <see cref="Опублікована_версія_не_приймає_ні_постановки_ні_видалення_правила_категорії"/>.
/// </remarks>
public sealed class MethodologyCategoryRuleTests
{
    private const int Author = 7;
    private const int Reviewer = 9;

    private static readonly DateTime Now = new(2026, 2, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = Now.AddHours(1);
    private static readonly DateOnly From = new(2026, 1, 1);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Чернетка_створює_правило_і_переписує_його_з_новим_часом()
    {
        var version = Draft();

        var created = version.SetCategoryRule(existing: null, "  !ECW_Location  ", Now);

        Assert.Equal("!ECW_Location", created.Expression);
        Assert.Equal(Now, created.CreatedAt);
        Assert.Equal(Now, created.UpdatedAt);

        var rewritten = version.SetCategoryRule(created, "!ECW_Category", Later);

        Assert.Same(created, rewritten);
        Assert.Equal("!ECW_Category", rewritten.Expression);
        Assert.Equal(Now, rewritten.CreatedAt);
        Assert.Equal(Later, rewritten.UpdatedAt);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [InlineData("")]
    [InlineData("   ")]
    public void Порожній_вираз_не_приймається_ні_при_створенні_ні_при_правці(string expression)
    {
        var version = Draft();

        var creating = Assert.Throws<DomainException>(() => version.SetCategoryRule(null, expression, Now));
        Assert.Equal("ECR-CALC-0422", creating.ErrorCode);
        Assert.Equal("err.ECR-CALC-0422.categoryRuleEmpty", creating.Details?["messageKey"]);

        var existing = version.SetCategoryRule(null, "!K", Now);
        var editing = Assert.Throws<DomainException>(() => version.SetCategoryRule(existing, expression, Later));
        Assert.Equal("ECR-CALC-0422", editing.ErrorCode);

        // Вираз лишився попереднім: відмова не псує наявне правило.
        Assert.Equal("!K", existing.Expression);
        Assert.Equal(Now, existing.UpdatedAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Опублікована_версія_не_приймає_ні_постановки_ні_видалення_правила_категорії()
    {
        var version = Draft();
        var rule = version.SetCategoryRule(null, "!K", Now);

        version.Publish(Reviewer, "Причина", From, testsPassed: true, Now);

        var set = Assert.Throws<DomainException>(() => version.SetCategoryRule(rule, "!Other", Later));
        Assert.Equal("ECR-CALC-0409", set.ErrorCode);

        var created = Assert.Throws<DomainException>(() => version.SetCategoryRule(null, "!Other", Later));
        Assert.Equal("ECR-CALC-0409", created.ErrorCode);

        var removed = Assert.Throws<DomainException>(() => version.RemoveCategoryRule(rule));
        Assert.Equal("ECR-CALC-0409", removed.ErrorCode);

        Assert.Equal("!K", rule.Expression);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Правило_чужої_версії_не_правиться_через_цю_версію()
    {
        var version = Draft();
        var foreign = new MethodologyCategoryRule(methodologyVersionId: 999, "!K", Now);

        var error = Assert.Throws<DomainException>(() => version.SetCategoryRule(foreign, "!Other", Later));

        Assert.Equal("ECR-CALC-0409", error.ErrorCode);
        Assert.Equal("!K", foreign.Expression);
    }

    private static MethodologyVersion Draft()
        => new(1, "1.0", CalculationLevel.Configuration, Author, Now);
}
