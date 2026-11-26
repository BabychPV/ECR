using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// ✎ 25.11 (живий прохід, таблиця 3.00 LAND_UI): повідомлення <c>ECR-CALC-0437</c> — одне на (рядок, колонку) з
/// переліком методологій, мовою читача, з назвою колонки; колонка з дефолтом заповнена.
/// </summary>
/// <remarks>
/// До фіксу: 5 методологій × 2 колонки = 10 повідомлень (і повтори тієї самої методології), текст — український
/// літерал «Колонка «Land_TypeFuel» обов'язкова для методології «…»» за будь-якої мови, власна підказка — лише
/// англійською; «Information on overhauled engines» з дефолтом <c>NOTPASSEDOVERHAUL</c> (сітка й формули бачать
/// значення) вважалась незаповненою — попередження, яке людина не могла зняти.
/// Мутації: (1) повернути <c>messages.Add</c> на кожну методологію — тест «дві методології» червоний (2 повідомлення);
/// (2) повернути літерал — тест «ru» червоний; (3) <c>IsFilled</c> → <c>ValueOf(...) is not null</c> — тест «дефолт»
/// червоний.
/// </remarks>
public sealed partial class PatchCellsTests
{
    private (MethodologyVersion Version, IReadOnlyList<MethodologyRule> Rules, MethodologyRequiredInput Required, ColumnDef Category)
        _lastMethodology;

    /// <summary>Друга опублікована методологія з тією самою вимогою (колонка <c>Category</c>), плюс повтор першої.</summary>
    private void WithSecondMethodologyAndRepeat(RequiredInputSeverity severity)
    {
        var (first, firstRules, firstRequired, _) = _lastMethodology;
        var version = new MethodologyVersion(101, "1.0", CalculationLevel.Configuration, 1, Now, MethodologyEngine.Formulas);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(version, 9101);
        IReadOnlyList<MethodologyRule> rules = [version.AddRule(EcrCode.Create("all"), "{}", 100)];
        var required = version.AddRequiredInput(12, severity, hint: null);
        version.Publish(publishedByUserId: 2, "тестова публікація", new DateOnly(2026, 1, 1), testsPassed: true, Now);

        _methodologies.GetMethodologyIdsBoundToTableAsync(3, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<int>>([100, 101]));
        _methodologies.GetPublishedVersionsOfAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<MethodologyVersion>>([first, version, first]));
        _methodologies.GetRulesOfVersionsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<MethodologyRule>>([.. firstRules, .. rules]));
        _methodologies.GetRequiredInputsOfVersionsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<MethodologyRequiredInput>>([firstRequired, required]));
        _methodologies.GetVersionLabelsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyDictionary<int, MethodologyVersionLabel>>(
                          new Dictionary<int, MethodologyVersionLabel>
                          {
                              [first.Id] = new(first.Id, "ECW_TEST", first.Version),
                              [version.Id] = new(version.Id, "ECW_OTHER", version.Version),
                          }));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    public async Task Дві_методології_й_повтор_однієї_дають_ОДНЕ_повідомлення_з_переліком_і_назвою_колонки()
    {
        WithMethodology(RequiredInputSeverity.Warn);
        WithSecondMethodologyAndRepeat(RequiredInputSeverity.Warn);

        var response = await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)])), CancellationToken.None);

        var warning = Assert.Single(response.Validation, m => m.RuleCode == "ECR-CALC-0437");
        Assert.Equal("Category", warning.ColumnCode);
        Assert.Equal(["ECW_TEST", "ECW_OTHER"], warning.Methodologies);
        Assert.Equal("Column \"Category\" is required for methodologies ECW_TEST, ECW_OTHER.", warning.Message);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    public async Task Відмова_Block_несе_методології_в_cells_без_повторів()
    {
        WithMethodology(RequiredInputSeverity.Block);
        WithSecondMethodologyAndRepeat(RequiredInputSeverity.Warn);

        var error = await Assert.ThrowsAsync<Ecr.Application.Errors.BusinessRuleException>(
            () => Handler().HandleAsync(
                Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)])), CancellationToken.None));

        var cells = System.Text.Json.JsonSerializer.Serialize(error.Details!["cells"]);
        Assert.Equal(1, cells.Split("\"ColumnCode\"").Length - 1);
        Assert.Contains("\"Methodologies\":[\"ECW_TEST\",\"ECW_OTHER\"]", cells, StringComparison.Ordinal);
        Assert.DoesNotMatch("[Ѐ-ӿ]", cells);
    }

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    [InlineData("ru", "Колонка «Category» обязательна для методологии ECW_TEST.")]
    [InlineData("kz", "«Category» бағаны ECW_TEST әдістемесі үшін міндетті.")]
    [InlineData("en", "Column \"Category\" is required for methodology ECW_TEST.")]
    public async Task Текст_мовою_читача_а_не_українським_літералом(string language, string expected)
    {
        _user.Language.Returns(language);
        WithMethodology(RequiredInputSeverity.Warn);

        var response = await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)])), CancellationToken.None);

        var warning = Assert.Single(response.Validation, m => m.RuleCode == "ECR-CALC-0437");
        Assert.Equal(expected, warning.Message);
        Assert.DoesNotContain("обов'язкова", warning.Message, StringComparison.Ordinal);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    public async Task Колонка_з_дефолтом_без_запису_вважається_заповненою()
    {
        WithMethodology(RequiredInputSeverity.Block);
        _lastMethodology.Category.SetPresentation("NOTPASSED", displayFormat: null, styleId: null);
        _cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<CellAddress, CellValueData>());

        var response = await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)])), CancellationToken.None);

        Assert.DoesNotContain(response.Validation, m => m.RuleCode == "ECR-CALC-0437");
        await _cells.Received(1).ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    public async Task Явне_стирання_колонки_з_дефолтом_і_далі_блокує()
    {
        WithMethodology(RequiredInputSeverity.Block);
        _lastMethodology.Category.SetPresentation("NOTPASSED", displayFormat: null, styleId: null);

        var error = await Assert.ThrowsAsync<Ecr.Application.Errors.BusinessRuleException>(
            () => Handler().HandleAsync(
                Request(new PatchRow("7001001", "0x0A", [new PatchCell("Category", null)])), CancellationToken.None));

        Assert.Equal("ECR-CALC-0437", error.ErrorCode);
    }
}
