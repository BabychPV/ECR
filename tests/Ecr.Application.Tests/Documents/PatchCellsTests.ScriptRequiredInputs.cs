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
/// ✎ P-1124-1 (UI-прохід редактора скрипта, 24.11): обов'язковий вхід, заведений у вкладці «Required columns»
/// ВЕРСІЇ-СКРИПТА, справді блокує (Block) чи попереджає (Warn) запис рядка в документі.
/// </summary>
/// <remarks>
/// Дефект до фіксу: gate (<c>PatchCellsHandler.ResolveApplicableAsync</c>) брав лише версії з правилами відбору, а
/// версія-скрипт їх не має (рядки вибирає сам скрипт) — вимога нової версії-скрипта не спрацьовувала ніколи.
/// Мутація: прибрати виняток <c>scriptWithoutRules</c> — обидва перші тести червоні (запис проходить без 0437).
/// Контроль: формульна версія без правил і далі не перевіряє нічого (поведінка без змін).
/// </remarks>
public sealed partial class PatchCellsTests
{
    private static PatchCellsRequest EmptyCategoryEdit()
        => Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)]));

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    public async Task Скрипт_без_правил_Block_вимога_блокує_запис_рядка_ECR_CALC_0437()
    {
        WithMethodology(RequiredInputSeverity.Block, engine: MethodologyEngine.Script, withRule: false);
        _cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<CellAddress, CellValueData>());

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(EmptyCategoryEdit(), CancellationToken.None));

        Assert.Equal("ECR-CALC-0437", error.ErrorCode);
        await _cells.DidNotReceive().ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    public async Task Скрипт_без_правил_Warn_вимога_попереджає_і_пише()
    {
        WithMethodology(RequiredInputSeverity.Warn, engine: MethodologyEngine.Script, withRule: false);
        _cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<CellAddress, CellValueData>());

        var response = await Handler().HandleAsync(EmptyCategoryEdit(), CancellationToken.None);

        await _cells.Received(1).ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
        var warning = Assert.Single(response.Validation, m => m.RuleCode == "ECR-CALC-0437");
        Assert.Equal("Warning", warning.Severity);
        Assert.Equal("Category", warning.ColumnCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    public async Task Скрипт_без_правил_заповнений_вхід_не_блокує()
    {
        WithMethodology(RequiredInputSeverity.Block, engine: MethodologyEngine.Script, withRule: false);
        _cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<CellAddress, CellValueData>
              {
                  [new CellAddress(PeriodKey.Parse(Period), 1001L, 12)] = new() { ValueString = "Engine" },
              });

        var response = await Handler().HandleAsync(EmptyCategoryEdit(), CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);
        Assert.DoesNotContain(response.Validation, m => m.RuleCode == "ECR-CALC-0437");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    public async Task Формульна_версія_без_правил_і_далі_нічого_не_перевіряє()
    {
        WithMethodology(RequiredInputSeverity.Block, engine: MethodologyEngine.Formulas, withRule: false);
        _cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<CellAddress, CellValueData>());

        var response = await Handler().HandleAsync(EmptyCategoryEdit(), CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);
        Assert.DoesNotContain(response.Validation, m => m.RuleCode == "ECR-CALC-0437");
    }
}
