// tests/Ecr.Application.Tests/Calculations/MethodologyPackagePlannerTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// План імпорту пакета методологій (крок V, FEATURE-HSE301-VIEW §11.6): резолвінг посилань,
/// блокери, конфлікти, ідемпотентність, дати.
/// </summary>
/// <remarks>
/// Мутаційні докази (кожну зміну внесено окремо → названий тест червоний, після відкату — зелений):
/// прибрати гілку <c>unresolvedFormula</c> → <see cref="Нерезолвне_посилання_на_формулу_блокує"/>;
/// прибрати <c>unresolvedConstant</c> → <see cref="CST_без_цілі_блокує"/>;
/// прибрати <c>imports.Add(libraryCode)</c> → <see cref="Посилання_в_бібліотеку_стає_імпортом_а_CST_копією"/>;
/// порівнювати число без <c>G29</c> → <see cref="Той_самий_вміст_у_базі_дає_unchanged_навіть_із_хвостовими_нулями"/>;
/// прибрати перевід у пояс → <see cref="Дати_AF_переводяться_в_день_майданчика_і_півінтервал"/>.
/// </remarks>
public sealed class MethodologyPackagePlannerTests
{
    private static readonly TimeZoneInfo Atyrau = TimeZoneInfo.FindSystemTimeZoneById("Asia/Atyrau");

    private static readonly UnitCatalogSnapshot Units = new(
        new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
        {
            ["one"] = new UnitRef(7, "one", 7),
            ["t"] = new UnitRef(8, "t", 1),
            ["kg_per_t"] = new UnitRef(21, "kg_per_t", 9),
        },
        new Dictionary<string, int>(StringComparer.Ordinal));

    private static readonly IReadOnlyDictionary<string, ExistingMethodology> Nothing =
        new Dictionary<string, ExistingMethodology>(StringComparer.OrdinalIgnoreCase);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Нерезолвне_посилання_на_формулу_блокує()
    {
        var package = Package(Methodology("M1", Formula("F1", "!Missing * 2", "!Missing")));

        var plan = MethodologyPackagePlanner.Plan(package, Nothing, Units, Atyrau);

        var blocker = Assert.Single(plan.Blockers);
        Assert.Equal("unresolvedFormula", blocker.Kind);
        Assert.Equal("M1", blocker.Methodology);
        Assert.Equal("F1", blocker.Subject);
        Assert.Equal("!Missing", blocker.Detail);
        Assert.Equal("blocked", plan.ToReport(dryRun: true, applied: false).Outcome);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void CST_без_цілі_блокує()
    {
        var package = Package(Methodology("M1", Formula("F1", "CST.K * 2", "CST.K")));

        var plan = MethodologyPackagePlanner.Plan(package, Nothing, Units, Atyrau);

        var blocker = Assert.Single(plan.Blockers);
        Assert.Equal("unresolvedConstant", blocker.Kind);
        Assert.Equal("CST.K", blocker.Detail);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Блокер_експортера_переходить_у_звіт()
    {
        var package = Package(Methodology("M1", Formula("F1", "@A", "@A"))) with { Blockers = ["цикл F1 → F1"] };

        var plan = MethodologyPackagePlanner.Plan(package, Nothing, Units, Atyrau);

        Assert.Equal("packageBlocker", Assert.Single(plan.Blockers).Kind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Чужий_формат_блокує_без_плану()
    {
        var package = Package(Methodology("M1", Formula("F1", "@A", "@A"))) with { Version = 2 };

        var plan = MethodologyPackagePlanner.Plan(package, Nothing, Units, Atyrau);

        Assert.Equal("format", Assert.Single(plan.Blockers).Kind);
        Assert.Empty(plan.Methodologies);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Недопустимий_код_блокує()
    {
        var package = Package(Methodology("M-1", Formula("F1", "@A", "@A")));

        var plan = MethodologyPackagePlanner.Plan(package, Nothing, Units, Atyrau);

        Assert.Equal("invalidCode", Assert.Single(plan.Blockers).Kind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Посилання_в_бібліотеку_стає_імпортом_а_CST_копією()
    {
        var common = Methodology(
            "Common",
            [Formula("Shared", "@X * CST.EF", "@X;CST.EF")],
            [Constant("EF", "t", Value("2.5"))]);
        var owner = Methodology("M1", Formula("F1", "!Shared + CST.EF", "!Shared;CST.EF"));

        var plan = MethodologyPackagePlanner.Plan(Package(common, owner), Nothing, Units, Atyrau);

        Assert.Empty(plan.Blockers);
        Assert.Equal(MethodologyKind.Library, plan.Methodologies.Single(m => m.Code == "Common").Kind);

        var version = plan.Methodologies.Single(m => m.Code == "M1").Versions.Single();
        Assert.Equal(["Common"], version.Content.Imports);
        Assert.Equal(1, version.ConstantsFromLibrary);

        var copied = Assert.Single(version.Content.Constants);
        Assert.Equal("EF", copied.Code);
        Assert.Equal(2.5m, copied.Value);
        Assert.Equal(8, copied.UnitId);
        Assert.Equal("AF Common/V1", copied.Source);
        Assert.Contains(plan.Warnings, w => w.Kind == "constantsFromLibrary");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Бібліотека_в_базі_резолвить_посилання_якщо_в_пакеті_її_немає()
    {
        var library = new ExistingMethodology(
            90, "Common", MethodologyKind.Library,
            [new ExistingMethodologyVersion(
                91, "V1", IsDraft: false,
                new ImportVersionContent([new ImportFormulaContent("Shared", "@X", "@X")], [], []))]);
        var existing = new Dictionary<string, ExistingMethodology>(StringComparer.OrdinalIgnoreCase) { ["Common"] = library };

        var plan = MethodologyPackagePlanner.Plan(
            Package(Methodology("M1", Formula("F1", "!Shared", "!Shared"))), existing, Units, Atyrau);

        Assert.Empty(plan.Blockers);
        Assert.Equal(["Common"], plan.Methodologies.Single().Versions.Single().Content.Imports);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Той_самий_вміст_у_базі_дає_unchanged_навіть_із_хвостовими_нулями()
    {
        var package = Package(Methodology(
            "M1",
            [Formula("F1", "@A * CST.K", "@A;CST.K")],
            [Constant("K", "kg_per_t", Value("1.5", "2023-12-31T19:00:00Z", "9999-02-19T19:00:00Z"))]));

        var first = MethodologyPackagePlanner.Plan(package, Nothing, Units, Atyrau);
        var content = first.Methodologies.Single().Versions.Single().Content;

        // Так те саме повертає база: decimal(38,18) і рядки в іншому порядку.
        var stored = content with
        {
            Constants = [.. content.Constants.Select(c => c with { Value = 1.500000000000000000m })],
        };
        var existing = Existing("M1", "V1", isDraft: true, stored);

        var second = MethodologyPackagePlanner.Plan(package, existing, Units, Atyrau);

        Assert.Equal("unchanged", second.Methodologies.Single().Versions.Single().Action);
        Assert.False(second.HasChanges);
        Assert.Equal("unchanged", second.ToReport(dryRun: false, applied: false).Outcome);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Інший_вміст_тієї_самої_версії_конфлікт()
    {
        var package = Package(Methodology("M1", Formula("F1", "@A * 2", "@A")));
        var existing = Existing(
            "M1", "V1", isDraft: false,
            new ImportVersionContent([new ImportFormulaContent("F1", "@A * 3", "@A")], [], []));

        var plan = MethodologyPackagePlanner.Plan(package, existing, Units, Atyrau);

        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal("publishedDiffers", conflict.Kind);
        Assert.Equal("conflict", plan.ToReport(dryRun: true, applied: false).Outcome);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Дати_AF_переводяться_в_день_майданчика_і_півінтервал()
    {
        var package = Package(Methodology(
            "M1",
            [Formula("F1", "CST.K", "CST.K")],
            [Constant(
                "K", "t",
                Value("1", "2023-12-31T19:00:00Z", "2024-12-31T18:59:59Z"),
                Value("2", "2024-12-31T19:00:00Z", "9999-02-19T19:00:00Z"))]));

        var plan = MethodologyPackagePlanner.Plan(package, Nothing, Units, Atyrau);

        var rows = plan.Methodologies.Single().Versions.Single().Content.Constants.OrderBy(c => c.Value).ToList();
        Assert.Equal(new DateOnly(2024, 1, 1), rows[0].ValidFrom);
        Assert.Equal(new DateOnly(2025, 1, 1), rows[0].ValidTo);
        Assert.Equal(new DateOnly(2025, 1, 1), rows[1].ValidFrom);
        Assert.Null(rows[1].ValidTo);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Нечислова_константа_за_вживанням_текст_або_мітка_невідома_одиниця_попередження()
    {
        var package = Package(Methodology(
            "M1",
            [Formula("F1", "CST.Season", "CST.Season")],
            [
                Constant("Season", null, Value("Summer")),
                Constant("Label", null, Value("LPG")),
                Constant("Odd", "furlong", Value("3")),
            ]));

        var plan = MethodologyPackagePlanner.Plan(package, Nothing, Units, Atyrau);
        var constants = plan.Methodologies.Single().Versions.Single().Content.Constants;

        Assert.Equal(ConstantKind.Text, constants.Single(c => c.Code == "Season").Kind);
        Assert.Equal(ConstantKind.CategoryLabel, constants.Single(c => c.Code == "Label").Kind);
        Assert.Equal(7, constants.Single(c => c.Code == "Odd").UnitId);
        Assert.Contains(plan.Warnings, w => w.Kind == "unitUnknown" && w.Subject == "Odd");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Кілька_версій_формули_дають_одну_з_найпізнішим_початком_і_попередження()
    {
        var package = Package(Methodology(
            "M1",
            Formula("F1", "@A * 1", "@A", version: "1", start: "2020-01-01T00:00:00Z"),
            Formula("F1", "@A * 2", "@A", version: "2", start: "2024-01-01T00:00:00Z")));

        var plan = MethodologyPackagePlanner.Plan(package, Nothing, Units, Atyrau);

        var formula = Assert.Single(plan.Methodologies.Single().Versions.Single().Content.Formulas);
        Assert.Equal("@A * 2", formula.Expression);
        Assert.Contains(plan.Warnings, w => w.Kind == "formulaVersionsCollapsed");
    }

    // ── Опора ─────────────────────────────────────────────────────────────

    private static MethodologyPackageDto Package(params MethodologyPackageMethodologyDto[] methodologies)
        => new(MethodologyPackagePlanner.FormatName, 1, "Common", methodologies, []);

    private static MethodologyPackageMethodologyDto Methodology(string name, params MethodologyPackageFormulaDto[] formulas)
        => Methodology(name, formulas, []);

    private static MethodologyPackageMethodologyDto Methodology(
        string name, MethodologyPackageFormulaDto[] formulas, MethodologyPackageConstantDto[] constants)
        => new(name, [new MethodologyPackageVersionDto("V1", formulas, constants)]);

    private static MethodologyPackageFormulaDto Formula(
        string name, string text, string arguments, string version = "1", string start = "2023-12-31T19:00:00Z")
        => new(name, version, arguments, text, start, "9999-02-19T19:00:00Z", IsAvailable: true, Report: string.Empty);

    private static MethodologyPackageConstantDto Constant(
        string name, string? unit, params MethodologyPackageConstantValueDto[] values)
        => new(name, string.Empty, unit, values);

    private static MethodologyPackageConstantValueDto Value(
        string value, string? start = null, string? end = null)
        => new(string.Empty, "1", value, start, end);

    private static Dictionary<string, ExistingMethodology> Existing(
        string code, string version, bool isDraft, ImportVersionContent content)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [code] = new ExistingMethodology(
                10, code, MethodologyKind.DataDriven, [new ExistingMethodologyVersion(11, version, isDraft, content)]),
        };
}
