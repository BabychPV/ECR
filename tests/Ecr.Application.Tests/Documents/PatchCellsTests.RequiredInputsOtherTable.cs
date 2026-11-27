// tests/Ecr.Application.Tests/Documents/PatchCellsTests.RequiredInputsOtherTable.cs
using Ecr.Application.Documents.Dto;
using Ecr.Application.Documents.RequiredInputs;
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
/// ✎ Рішення координатора 27.11 (D-577 L6 / D-578): методологія прив'язана до ДВОХ таблиць одного шаблону, у кожної — своя
/// обов'язкова колонка. Вимога, чия колонка не в перевірюваній таблиці, для неї пропускається: кожна таблиця вимагає лише
/// свою колонку — у воротах PATCH, у поданні, «Перевірити» й переліку при відкритті (спільний <c>MethodologyRequiredInputs</c>).
/// </summary>
/// <remarks>
/// Мутація (локально): у <c>MethodologyRequiredInputs.ResolveAsync</c> фільтр <c>ownColumns</c> → завжди істина — обидва тести
/// червоні (чужа колонка «порожня» в кожному рядку).
/// </remarks>
public sealed partial class PatchCellsTests
{
    private const int SecondTableId = 4;
    private const int SecondRequiredColumnId = 21;
    private const int SecondInputColumnId = 22;

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-577")]
    public async Task Кожна_таблиця_вимагає_лише_свою_колонку_при_відкритті_й_поданні()
    {
        WithMethodology(RequiredInputSeverity.Block);
        var (version, _, firstRequired, _) = _lastMethodology;
        var secondRequired = new MethodologyRequiredInput(version.Id, SecondRequiredColumnId, RequiredInputSeverity.Block, hint: null);
        _methodologies.GetRequiredInputsOfVersionsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<MethodologyRequiredInput>>([firstRequired, secondRequired]));
        _methodologies.GetMethodologyIdsBoundToTableAsync(SecondTableId, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<int>>([100]));

        var snapshot = await _metadata.GetAsync(2, CancellationToken.None);
        var first = snapshot.Sheets[0].Tables[0];
        var second = new TableDef(
            sheetDefId: 1, EcrCode.Create("Second"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Second" }), 2,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(second, SecondTableId);
        var input = new ColumnDef(SecondTableId, EcrCode.Create("Rate"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Rate" }), 1, CellDataType.Decimal);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(input, SecondInputColumnId);
        var required = new ColumnDef(SecondTableId, EcrCode.Create("Factor"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Factor" }), 2, CellDataType.Decimal);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(required, SecondRequiredColumnId);
        second.AddColumn(input);
        second.AddColumn(required);
        var columns = new Dictionary<int, ColumnDef>(snapshot.ColumnsById) { [SecondInputColumnId] = input, [SecondRequiredColumnId] = required };

        var scanner = new RequiredInputsScanner(_methodologies, _periods);
        var firstPlan = await scanner.PlanAsync(700, Period, 2, first, CancellationToken.None);
        var secondPlan = await scanner.PlanAsync(700, Period, 2, second, CancellationToken.None);

        // Перша таблиця: свою колонку заповнено — чужа (Factor другої таблиці) не «порожня».
        var rows = new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 1 };
        CellRecord[] firstCells =
        [
            Cell(1, VolumeColumnId, new CellValueData { ValueNumeric = 5m }),
            Cell(1, _lastMethodology.Category.Id, new CellValueData { ValueString = "Engine" }),
        ];
        Assert.Empty(RequiredInputsScanner.Scan(firstPlan!, first, columns, firstCells, rows, "en"));

        // Друга таблиця: вимагається лише її Factor, а не Category першої.
        CellRecord[] secondCells =
        [
            new(new CellAddress(PeriodKey.Parse(Period), 1, SecondInputColumnId), SecondTableId, new CellValueData { ValueNumeric = 3m }),
        ];
        var only = Assert.Single(RequiredInputsScanner.Scan(secondPlan!, second, columns, secondCells, rows, "en"));
        Assert.Equal("Factor", only.ColumnCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "D-577")]
    public async Task Ворота_PATCH_не_вимагають_колонки_іншої_таблиці()
    {
        // Вимога методології — на колонку іншої таблиці шаблону (99 — не в таблиці 3): правка Volume не блокується.
        WithMethodology(RequiredInputSeverity.Block, requiredColumnId: 99);

        var response = await Handler().HandleAsync(
            Request(new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)])), CancellationToken.None);

        Assert.DoesNotContain(response.Validation, m => m.RuleCode == "ECR-CALC-0437");
        await _cells.Received(1).ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
    }
}
