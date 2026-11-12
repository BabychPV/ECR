using Ecr.Application.Common;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Обов'язковий вхід методології, який сам є КЛЮЧЕМ правил (<c>MatchJson</c>), — варіант B (рішення людини через
/// координатора, 11.11): порожній ключ означає, що жодне правило не збігається, і до фіксу gate мовчки пропускав рядок
/// (<c>Land_TypeEquipment</c>/<c>Land_TypeFuel</c> на ECW_C05_01..05 — реєстрація вимоги нічого не давала).
/// </summary>
/// <remarks>
/// Правило тут — <c>{"14":"77","12":"Engine"}</c>: колонка 14 — статус (77 = Running, у знімку метаданих її немає,
/// значення лише з бази), колонка 12 — <c>Category</c>, ключ правила й обов'язковий вхід водночас.
/// Рядок «у межах методології без збігу правила» — правило, всі ЗАПОВНЕНІ ключі якого збігаються, а порожні є;
/// і рядок «розпочато»: заповнений бодай один ключ, що сам не є вимогою (тут статус), або рядок пройшов фільтр
/// активності D-425 методології.
/// </remarks>
public sealed partial class PatchCellsTests
{
    private const string StatusAndCategoryRule = """{"14":"77","12":"Engine"}""";

    /// <summary>Збережений стан рядка 1001: статус (колонка 14) і, за потреби, <c>Category</c> (12).</summary>
    private void StoredRow(long? statusEntryId, string? category = null)
    {
        var stored = new Dictionary<CellAddress, CellValueData>();
        if (statusEntryId is { } status)
        {
            stored[new CellAddress(PeriodKey.Parse(Period), 1001L, 14)] = new() { ValueRegistryEntryId = status };
        }

        if (category is not null)
        {
            stored[new CellAddress(PeriodKey.Parse(Period), 1001L, 12)] = new() { ValueString = category };
        }

        _cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
              .Returns(stored);
    }

    private static PatchCellsRequest VolumeEdit()
        => Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)]));

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    public async Task Порожній_Block_ключ_правила_блокує_запис_розпочатого_рядка_ECR_CALC_0437()
    {
        WithMethodology(RequiredInputSeverity.Block, matchJson: StatusAndCategoryRule);
        StoredRow(statusEntryId: 77);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(VolumeEdit(), CancellationToken.None));

        Assert.Equal("ECR-CALC-0437", error.ErrorCode);
        var details = System.Text.Json.JsonSerializer.Serialize(error.Details);
        Assert.Contains("Category", details, StringComparison.Ordinal);
        Assert.Contains("ECW_TEST", details, StringComparison.Ordinal);
        await _cells.DidNotReceive().ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    public async Task Порожній_Warn_ключ_правила_попереджає_і_не_блокує()
    {
        WithMethodology(RequiredInputSeverity.Warn, matchJson: StatusAndCategoryRule);
        StoredRow(statusEntryId: 77);

        var response = await Handler().HandleAsync(VolumeEdit(), CancellationToken.None);

        await _cells.Received(1).ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
        var warning = Assert.Single(response.Validation, m => m.RuleCode == "ECR-CALC-0437");
        Assert.Equal("Warning", warning.Severity);
        Assert.Equal("7001001", warning.RowKey);
        Assert.Equal("Category", warning.ColumnCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    public async Task Інтеграція_з_порожнім_Block_ключем_правила_отримує_попередження_а_не_блок()
    {
        WithMethodology(RequiredInputSeverity.Block, matchJson: StatusAndCategoryRule);
        StoredRow(statusEntryId: 77);

        var response = await Handler().HandleAsync(
            new PatchCellsRequest(
                TableInstance, Period, "Integration", [new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)])]),
            CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);
        var warning = Assert.Single(response.Validation, m => m.RuleCode == "ECR-CALC-0437");
        Assert.Equal("Warning", warning.Severity);
    }

    [Theory]
    [InlineData("Engine")]
    [InlineData("Boiler")]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    public async Task Заповнений_ключ_правила_не_блокує_навіть_якщо_правило_не_збіглося(string category)
    {
        // «Engine» — правило збігається, вимогу виконано. «Boiler» — рядок належить іншій методології таблиці:
        // заповнений ключ розходиться з правилом, тож рядок поза межами ЦІЄЇ методології — тиша, як і до фіксу.
        WithMethodology(RequiredInputSeverity.Block, matchJson: StatusAndCategoryRule);
        StoredRow(statusEntryId: 77, category);

        var response = await Handler().HandleAsync(VolumeEdit(), CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);
        Assert.DoesNotContain(response.Validation, m => m.RuleCode == "ECR-CALC-0437");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    public async Task Порожній_ключ_правила_без_вимоги_і_вимога_поза_ключами_мовчать_без_збігу()
    {
        // Вимога — колонка 15 (не ключ правила, порожня), ключ 12 не зареєстровано: правило не збіглося,
        // а звичайні вимоги без збігу не перевіряються — поведінка до фіксу.
        WithMethodology(RequiredInputSeverity.Block, matchJson: StatusAndCategoryRule, requiredColumnId: 15);
        StoredRow(statusEntryId: 77);

        var response = await Handler().HandleAsync(VolumeEdit(), CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);
        Assert.DoesNotContain(response.Validation, m => m.RuleCode == "ECR-CALC-0437");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    public async Task Нерозпочатий_рядок_без_статусу_не_блокується_порожнім_ключем()
    {
        // Жоден ключ, що не є вимогою, не заповнений, фільтра активності немає — рядок ще не віднесено до методології.
        WithMethodology(RequiredInputSeverity.Block, matchJson: StatusAndCategoryRule);
        StoredRow(statusEntryId: null);

        var response = await Handler().HandleAsync(VolumeEdit(), CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);
        Assert.DoesNotContain(response.Validation, m => m.RuleCode == "ECR-CALC-0437");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    public async Task Новий_рядок_лише_з_обовязковим_ключем_не_блокується()
    {
        // Порядок заповнення: ключ-вимогу можна ввести першим — без статусу рядок не «розпочато», дедлоку немає.
        WithMethodology(RequiredInputSeverity.Block, matchJson: """{"14":"77","12":"Engine","15":"Diesel"}""");
        StoredRow(statusEntryId: null);

        var response = await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Category", "Engine")])),
            CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);
        Assert.DoesNotContain(response.Validation, m => m.RuleCode == "ECR-CALC-0437");
    }

    /// <summary>D-425: рядок, що пройшов фільтр активності методології, «розпочато», навіть якщо статус не ключ правила.</summary>
    private void WithStatusFilterOnly(string matchJson)
    {
        WithMethodology(RequiredInputSeverity.Block, matchJson: matchJson);

        var source = Substitute.For<IRowActivityFilterSource>();
        var resolution = new RowActivityFilterResolution(
            "77", new Dictionary<int, IReadOnlyList<int>> { [3] = [14] });
        source.ResolveForMethodologiesAsync(
                  Arg.Is<IReadOnlyCollection<int>>(ids => ids.Contains(100)), Arg.Any<IReadOnlyCollection<int>>(),
                  Arg.Any<CancellationToken>())
              .Returns(new Dictionary<int, RowActivityFilterResolution?> { [100] = resolution });
        _rowActivity = source;
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    [Trait("Requirement", "D-425")]
    public async Task Активний_рядок_за_фільтром_D425_блокується_порожнім_ключем_правила()
    {
        WithStatusFilterOnly("""{"12":"Engine"}""");
        StoredRow(statusEntryId: 77);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(VolumeEdit(), CancellationToken.None));

        Assert.Equal("ECR-CALC-0437", error.ErrorCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    public async Task Правило_лише_з_ключів_вимог_без_фільтра_активності_не_блокує()
    {
        // Межа обраного правила: без фільтра D-425 і без жодного ключа, що не є вимогою, рядок не «розпочато».
        WithMethodology(RequiredInputSeverity.Block, matchJson: """{"12":"Engine"}""");
        StoredRow(statusEntryId: 77);

        var response = await Handler().HandleAsync(VolumeEdit(), CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);
        Assert.DoesNotContain(response.Validation, m => m.RuleCode == "ECR-CALC-0437");
    }
}
