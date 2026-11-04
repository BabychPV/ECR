// tests/Ecr.Application.Tests/Calculations/MethodologyCategoryTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// План CONST-CATEGORY: перелік категорій констант версії — ключ зіставлення, вибір категорії константою зі
/// списку, фільтр констант, попередження публікації про літерали правила без категорії.
/// </summary>
/// <remarks>
/// ⛔ Мутаційний доказ (лише цей клас): у <c>DeleteMethodologyCategoryHandler</c> прибрати
/// <c>MethodologyCategoryChecks.RequireUnused</c> — червоніє <see cref="Зайняту_категорію_не_видаляють_409_з_кількістю"/>;
/// у <c>SaveMethodologyCategoryHandler</c> прибрати ту саму перевірку перед виведенням з ужитку — червоніє
/// <see cref="Зайняту_категорію_не_виводять_з_ужитку"/>.
/// </remarks>
public sealed class MethodologyCategoryTests
{
    private const int VersionId = 7;
    private static readonly DateTime Now = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);

    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly List<MethodologyCategory> _categories = [];
    private readonly Dictionary<string, int> _usage = new(StringComparer.OrdinalIgnoreCase);
    private readonly MethodologyVersion _version = new(1, "1.0", CalculationLevel.Configuration, 9, Now);

    public MethodologyCategoryTests()
    {
        // Версія «з бази»: категорії належать їй за ідентифікатором (RequireOwn у домені).
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(_version, VersionId);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission(SaveMethodologyCategoryHandler.Permission)
                .Permission(ListMethodologyCategoriesHandler.Permission)
                .Build());

        _drafts.FindVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(_version);
        _drafts.GetCategoriesTrackedAsync(VersionId, Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<MethodologyCategory>)[.. _categories]);
        _drafts.CountConstantsByCategoryAsync(VersionId, Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyDictionary<string, int>)new Dictionary<string, int>(_usage, StringComparer.OrdinalIgnoreCase));
        _drafts.When(d => d.Add(Arg.Any<MethodologyCategory>())).Do(c => _categories.Add(c.Arg<MethodologyCategory>()));
    }

    private SaveMethodologyCategoryHandler Save() => new(_drafts, _uow, _access, _user);

    private DeleteMethodologyCategoryHandler Delete() => new(_drafts, _uow, _access, _user);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Нова_категорія_стає_в_кінець_переліку_з_описом()
    {
        _categories.Add(new MethodologyCategory(VersionId, "Diesel", 4));

        var dto = await Save().HandleAsync(VersionId, "Loc_BeforeMR_A", "  Before MR, class A ", null, null, CancellationToken.None);

        Assert.Equal("Loc_BeforeMR_A", dto.Code);
        Assert.Equal(5, dto.Ordinal);
        Assert.Equal("Before MR, class A", dto.Description);
        Assert.False(dto.IsRetired);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Той_самий_код_в_іншому_регістрі_це_перейменування_409()
    {
        _categories.Add(new MethodologyCategory(VersionId, "Diesel_2754", 1));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(VersionId, "diesel_2754", null, null, null, CancellationToken.None));

        Assert.Equal("ECR-CALC-0409", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0409.categoryRename", error.Details!["messageKey"]);
        Assert.Equal("Diesel_2754", error.Details!["existing"]);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Diesel")]
    [InlineData("Diesel ")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Недопустимий_код_422(string code)
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(VersionId, code, null, null, null, CancellationToken.None));

        Assert.Equal("err.ECR-CALC-0422.categoryCodeInvalid", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Зайняту_категорію_не_виводять_з_ужитку()
    {
        _categories.Add(new MethodologyCategory(VersionId, "Diesel", 1));
        _usage["Diesel"] = 3;

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(VersionId, "Diesel", null, null, isRetired: true, CancellationToken.None));

        Assert.Equal("ECR-CALC-0409", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0409.categoryInUse", error.Details!["messageKey"]);
        Assert.Equal("3", error.Details!["count"]);
        Assert.False(_categories[0].IsRetired);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Вільну_категорію_виводять_з_ужитку_і_повертають()
    {
        _categories.Add(new MethodologyCategory(VersionId, "Gasoline", 2));

        var retired = await Save().HandleAsync(VersionId, "Gasoline", null, null, isRetired: true, CancellationToken.None);
        Assert.True(retired.IsRetired);

        var back = await Save().HandleAsync(VersionId, "Gasoline", null, null, isRetired: false, CancellationToken.None);
        Assert.False(back.IsRetired);
        Assert.Equal(2, back.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Зайняту_категорію_не_видаляють_409_з_кількістю()
    {
        _categories.Add(new MethodologyCategory(VersionId, "6585 (001)", 1));
        _usage["6585 (001)"] = 12;

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Delete().HandleAsync(VersionId, "6585 (001)", CancellationToken.None));

        Assert.Equal("err.ECR-CALC-0409.categoryInUse", error.Details!["messageKey"]);
        Assert.Equal("12", error.Details!["count"]);
        _drafts.DidNotReceiveWithAnyArgs().Remove(default(MethodologyCategory)!);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Вільну_категорію_видаляють_а_відсутню_404()
    {
        var free = new MethodologyCategory(VersionId, "LPG", 1);
        _categories.Add(free);

        await Delete().HandleAsync(VersionId, "LPG", CancellationToken.None);
        _drafts.Received(1).Remove(Arg.Is<MethodologyCategory>(c => ReferenceEquals(c, free)));

        // Регістр значущий: «lpg» — не та категорія.
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Delete().HandleAsync(VersionId, "lpg", CancellationToken.None));
        Assert.Equal("err.ECR-CALC-0404.category", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Перелік_несе_кількість_констант_під_категорією()
    {
        _categories.Add(new MethodologyCategory(VersionId, "Diesel", 1));
        _categories.Add(new MethodologyCategory(VersionId, "LPG", 2));
        _usage["Diesel"] = 73;

        var list = await new ListMethodologyCategoriesHandler(_drafts, _access, _user)
            .HandleAsync(VersionId, CancellationToken.None);

        Assert.Equal([("Diesel", 73), ("LPG", 0)], list.Select(c => (c.Code, c.ConstantCount)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Категорія_константи_з_переліку_точно_з_регістром()
    {
        IReadOnlyList<MethodologyCategory> list =
        [
            new MethodologyCategory(VersionId, "Diesel", 1),
            Retired("Old"),
        ];

        // Спільна (порожня) і точний код — проходять.
        MethodologyCategoryChecks.RequireListed("EF_1", null, null, list);
        MethodologyCategoryChecks.RequireListed("EF_1", "Diesel", null, list);

        var unknown = Assert.Throws<BusinessRuleException>(
            () => MethodologyCategoryChecks.RequireListed("EF_1", "diesel", null, list));
        Assert.Equal("err.ECR-CALC-0422.constantCategoryUnknown", unknown.Details!["messageKey"]);

        var retired = Assert.Throws<BusinessRuleException>(
            () => MethodologyCategoryChecks.RequireListed("EF_1", "Old", null, list));
        Assert.Equal("err.ECR-CALC-0422.constantCategoryRetired", retired.Details!["messageKey"]);

        // Категорія, що вже стоїть на константі, не перевіряється вдруге (повторний PUT того самого рядка).
        MethodologyCategoryChecks.RequireListed("EF_1", "Old", "Old", list);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Константа_з_категорією_поза_переліком_не_записується()
    {
        var handler = new SaveMethodologyConstantHandler(
            _drafts, Substitute.For<IRegistryStore>(), Units(), _uow, Access(SaveMethodologyConstantHandler.Permission), _user);
        _drafts.GetConstantsByCodeAsync(VersionId, "EF_1", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MethodologyConstant>)[]);
        _categories.Add(new MethodologyCategory(VersionId, "Diesel", 1));

        var request = new SaveMethodologyConstant(
            ConstantKind.Numeric, 1.5m, 5, null, null, null, "Desiel", null, null);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(VersionId, "EF_1", request, CancellationToken.None));

        Assert.Equal("err.ECR-CALC-0422.constantCategoryUnknown", error.Details!["messageKey"]);
        _drafts.DidNotReceiveWithAnyArgs().Add(default(MethodologyConstant)!);

        // Та сама константа з категорією зі списку — записується; порожня категорія = спільна.
        var saved = await handler.HandleAsync(VersionId, "EF_1", request with { Category = "Diesel" }, CancellationToken.None);
        Assert.Equal("Diesel", saved.Category);

        var shared = await handler.HandleAsync(VersionId, "EF_1", request with { Category = " " }, CancellationToken.None);
        Assert.Null(shared.Category);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Фільтр_констант_за_категорією_спільними_кодом_і_сортування()
    {
        var constants = new[]
        {
            Constant("EF_gsec_1325_", "Loc_BeforeMR_B", 1),
            Constant("EF_gsec_1325_", "Loc_BeforeMR_A", 2),
            Constant("EF_gsec_1325_", null, 3),
            Constant("k1_", "Common", 4),
            Constant("RHO", null, 5),
        };

        Assert.Equal(
            ["EF_gsec_1325_/", "EF_gsec_1325_/Loc_BeforeMR_A", "EF_gsec_1325_/Loc_BeforeMR_B", "k1_/Common", "RHO/"],
            Keys(MethodologyConstantsQuery.All.Apply(constants)));

        Assert.Equal(
            ["EF_gsec_1325_/Loc_BeforeMR_A"],
            Keys(new MethodologyConstantsQuery(Categories: ["Loc_BeforeMR_A"]).Apply(constants)));

        Assert.Equal(
            ["EF_gsec_1325_/", "EF_gsec_1325_/Loc_BeforeMR_A", "RHO/"],
            Keys(new MethodologyConstantsQuery(Categories: ["Loc_BeforeMR_A"], Shared: true).Apply(constants)));

        Assert.Equal(
            ["EF_gsec_1325_/", "RHO/"],
            Keys(new MethodologyConstantsQuery(Shared: true).Apply(constants)));

        Assert.Equal(
            ["EF_gsec_1325_/Loc_BeforeMR_A", "EF_gsec_1325_/Loc_BeforeMR_B"],
            Keys(new MethodologyConstantsQuery(Shared: false, Code: "gsec").Apply(constants)));

        Assert.Equal(
            ["RHO/", "k1_/Common", "EF_gsec_1325_/", "EF_gsec_1325_/Loc_BeforeMR_A", "EF_gsec_1325_/Loc_BeforeMR_B"],
            Keys(new MethodologyConstantsQuery(Descending: true).Apply(constants)));

        Assert.Equal(
            ["EF_gsec_1325_/", "RHO/", "k1_/Common", "EF_gsec_1325_/Loc_BeforeMR_A", "EF_gsec_1325_/Loc_BeforeMR_B"],
            Keys(new MethodologyConstantsQuery(Sort: MethodologyConstantSort.Category).Apply(constants)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Публікація_попереджає_про_літерал_правила_без_категорії_а_умови_й_динаміку_не_чіпає()
    {
        const string rule = "if(in(@Land_NAE, '6640', '6641'), 'Diesel', if(@Land_Power > 5, 'Gasoline', "
                            + "if(@Land_Kind = 'X', @Land_NAE + '_LPG', '')))";
        var warnings = new List<string>();

        var outcome = MethodologyCategoryRuleChecks.Check(
            rule, new RealFormulaEngine(), [], [], [], warnings, null, ["Diesel"]);

        Assert.Empty(outcome.Problems);
        var warning = Assert.Single(warnings);
        Assert.Contains("\"Gasoline\"", warning, StringComparison.Ordinal);

        // Без переліку (збереження правила) — звірки немає.
        var silent = new List<string>();
        MethodologyCategoryRuleChecks.Check(rule, new RealFormulaEngine(), [], [], [], silent, null);
        Assert.Empty(silent);
    }

    private static string[] Keys(IEnumerable<MethodologyConstant> constants)
        => [.. constants.Select(c => $"{c.Code}/{c.Category}")];

    private static MethodologyConstant Constant(string code, string? category, int day)
    {
        var constant = new MethodologyConstant(VersionId, EcrCode.Create(code), 1m, 5);
        constant.SetScope(category, null);
        constant.SetValidity(new DateOnly(2026, 1, day), null);
        return constant;
    }

    private static MethodologyCategory Retired(string code)
    {
        var category = new MethodologyCategory(VersionId, code, 9);
        category.Describe(null, 9, isRetired: true);
        return category;
    }

    private IAccessDecisionService Access(string permission)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission(permission).Build());
        return access;
    }

    private static IUnitCatalog Units()
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitCatalogSnapshot(
                new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["t"] = new(5, "t", 1) },
                new Dictionary<string, int>(StringComparer.Ordinal)));
        return units;
    }
}
