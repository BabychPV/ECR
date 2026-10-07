// tests/Ecr.Application.Tests/Calculations/MethodologyPackageCategoryRuleTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Вузол <c>categoryRule</c> версії пакета імпорту (L-2): план, блокери, звіт, зворотна сумісність.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні докази: планувальник не переносить вузол у <c>Content.CategoryRule</c> —
/// <see cref="Вузол_categoryRule_потрапляє_у_план_і_звіт_як_added"/> червоний; прибрати перевірку
/// розбору — червоні <see cref="Нерозібраний_або_числовий_вираз_блокує_імпорт"/>; прибрати
/// <c>R\u001f</c> з <c>ImportVersionContent.Keys</c> — червоний
/// <see cref="Версія_що_вже_є_з_іншим_правилом_дає_конфлікт_а_з_тим_самим_unchanged"/>.
/// </remarks>
public sealed class MethodologyPackageCategoryRuleTests
{
    private static readonly TimeZoneInfo Atyrau = TimeZoneInfo.FindSystemTimeZoneById("Asia/Atyrau");

    private static readonly UnitCatalogSnapshot Units = new(
        new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["one"] = new UnitRef(7, "one", 7) },
        new Dictionary<string, int>(StringComparer.Ordinal));

    private static readonly IReadOnlyDictionary<string, ExistingMethodology> Nothing =
        new Dictionary<string, ExistingMethodology>(StringComparer.OrdinalIgnoreCase);

    private static readonly IFormulaEngine Engine = new RealFormulaEngine();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Вузол_categoryRule_потрапляє_у_план_і_звіт_як_added()
    {
        var package = Package(Version(new MethodologyPackageCategoryRuleDto("  !ECW_Category  ")));

        var plan = MethodologyPackagePlanner.Plan(package, Nothing, Units, Atyrau, Engine);

        Assert.Empty(plan.Blockers);
        var planned = Assert.Single(Assert.Single(plan.Methodologies).Versions);
        Assert.Equal("!ECW_Category", planned.Content.CategoryRule);

        var report = plan.ToReport(dryRun: true, applied: false);
        Assert.Equal("added", Assert.Single(Assert.Single(report.Methodologies).Versions).CategoryRule);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Пакет_без_вузла_читається_як_і_раніше_і_звіт_не_згадує_правила()
    {
        // v1 зворотно сумісний: відсутній вузол = як до L-2.
        var plan = MethodologyPackagePlanner.Plan(Package(Version(null)), Nothing, Units, Atyrau, Engine);

        Assert.Empty(plan.Blockers);
        Assert.Null(Assert.Single(Assert.Single(plan.Methodologies).Versions).Content.CategoryRule);
        Assert.Null(Assert.Single(Assert.Single(plan.ToReport(dryRun: true, applied: false).Methodologies).Versions).CategoryRule);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData(null, "categoryRuleEmpty")]
    [InlineData("   ", "categoryRuleEmpty")]
    [InlineData("@Fuel +", "categoryRuleInvalid")]
    [InlineData("1 + 1", "categoryRuleNotText")]
    public void Нерозібраний_або_числовий_вираз_блокує_імпорт(string? expression, string kind)
    {
        var plan = MethodologyPackagePlanner.Plan(
            Package(Version(new MethodologyPackageCategoryRuleDto(expression))), Nothing, Units, Atyrau, Engine);

        var blocker = Assert.Single(plan.Blockers);
        Assert.Equal(kind, blocker.Kind);
        Assert.Equal("M1", blocker.Methodology);
        Assert.Equal("categoryRule", blocker.Subject);
        Assert.Equal("blocked", plan.ToReport(dryRun: true, applied: false).Outcome);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Задовгий_вираз_блокує_імпорт_і_без_рушія()
    {
        var plan = MethodologyPackagePlanner.Plan(
            Package(Version(new MethodologyPackageCategoryRuleDto(new string('x', 4001)))), Nothing, Units, Atyrau);

        Assert.Equal("categoryRuleTooLong", Assert.Single(plan.Blockers).Kind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Версія_що_вже_є_з_іншим_правилом_дає_конфлікт_а_з_тим_самим_unchanged()
    {
        var package = Package(Version(new MethodologyPackageCategoryRuleDto("!ECW_Category")));

        var same = MethodologyPackagePlanner.Plan(package, Existing("!ECW_Category"), Units, Atyrau, Engine);
        Assert.Empty(same.Conflicts);
        var unchanged = Assert.Single(Assert.Single(same.Methodologies).Versions);
        Assert.Equal("unchanged", unchanged.Action);
        Assert.Equal("unchanged", Assert.Single(Assert.Single(same.ToReport(dryRun: true, applied: false).Methodologies).Versions).CategoryRule);

        var different = MethodologyPackagePlanner.Plan(package, Existing("!Other"), Units, Atyrau, Engine);
        var conflict = Assert.Single(different.Conflicts);
        Assert.Equal("draftDiffers", conflict.Kind);

        var missing = MethodologyPackagePlanner.Plan(package, Existing(null), Units, Atyrau, Engine);
        Assert.Single(missing.Conflicts);
    }

    private static Dictionary<string, ExistingMethodology> Existing(string? rule)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["M1"] = new ExistingMethodology(
                1, "M1", Ecr.Domain.Enums.MethodologyKind.DataDriven,
                [new ExistingMethodologyVersion(10, "V1", IsDraft: true, new ImportVersionContent([], [], [], rule))]),
        };

    private static MethodologyPackageDto Package(MethodologyPackageVersionDto version)
        => new(MethodologyPackagePlanner.FormatName, 1, "Common", [new MethodologyPackageMethodologyDto("M1", [version])], []);

    private static MethodologyPackageVersionDto Version(MethodologyPackageCategoryRuleDto? rule)
        => new("V1", [], [], rule);
}
