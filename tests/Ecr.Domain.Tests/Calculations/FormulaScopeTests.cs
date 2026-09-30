// tests/Ecr.Domain.Tests/Calculations/FormulaScopeTests.cs
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Calculations;

/// <summary>
/// Область формули, проміжні результати й адреса кроку трейсу
/// (FEATURE-HSE301-VIEW §7.1, крок F6, <c>D-175</c>, <c>D-176</c>).
/// </summary>
/// <remarks>
/// ⛔ Головне тут — ТИПОВІ значення: сутність, створена чинним кодом без нових
/// параметрів, мусить поводитися рівно так, як до кроку F6 (<c>Substance</c>,
/// <c>IsPerSubstance = true</c>, <c>Output</c>, крок без адреси). Інше типове
/// значення тихо змінило б кожну наявну методологію.
///
/// Мутаційний доказ (F6): типове <c>Scope = Row</c> у
/// <see cref="MethodologyFormula"/> — червоніє
/// <see cref="Нова_формула_рахується_на_кожну_речовину_і_не_видима"/>.
/// Те саме на рівні бази (DEFAULT схеми) стереже <c>CalculationTraceSchemaTests</c>.
/// </remarks>
public sealed class FormulaScopeTests
{
    private const int VersionId = 51;
    private const int TonneUnit = 8;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Directive", "HSE301-F6")]
    public void Нова_формула_рахується_на_кожну_речовину_і_не_видима()
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create("tons"), "!MassKg / 1000");

        Assert.Equal(MethodologyFormulaScope.Substance, formula.Scope);
        Assert.False(formula.IsVisible);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Directive", "HSE301-F6")]
    public void Область_і_видимість_формули_задаються_явно()
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create("M_t"), "!V_Sm3 * @Rho20");

        formula.SetScope(MethodologyFormulaScope.Row);
        formula.SetVisible(true);

        Assert.Equal(MethodologyFormulaScope.Row, formula.Scope);
        Assert.True(formula.IsVisible);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Directive", "HSE301-F6")]
    public void Область_поза_переліком_відхиляється()
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create("tons"), "1");

        // Колонка tinyint прийняла б будь-який байт; рушій, що розрізняє лише
        // Row і Substance, мовчки трактував би сміття як одне з двох.
        Assert.Throws<ArgumentOutOfRangeException>(() => formula.SetScope((MethodologyFormulaScope)7));
        Assert.Equal(MethodologyFormulaScope.Substance, formula.Scope);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Directive", "HSE301-F6")]
    public void Новий_вихід_пишеться_на_кожну_речовину_доки_не_сказано_інакше()
    {
        var output = new MethodologyOutput(VersionId, EcrCode.Create("tons"), TonneUnit);
        Assert.True(output.IsPerSubstance);

        output.SetPerSubstance(false);
        Assert.False(output.IsPerSubstance);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Directive", "HSE301-F6")]
    public void Результат_без_виду_є_виходом_а_проміжний_задається_явно()
    {
        var output = new CalculationResult(1, VersionId, 202601, 700, "7001001", "tons", 0.912688m, TonneUnit);
        var intermediate = new CalculationResult(
            1, VersionId, 202601, 700, "7001001", "M_t", 0.2581914962m, TonneUnit,
            CalculationResultKind.Intermediate);

        Assert.Equal(CalculationResultKind.Output, output.Kind);
        Assert.Equal(CalculationResultKind.Intermediate, intermediate.Kind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Directive", "HSE301-F6")]
    public void Вид_результату_поза_переліком_відхиляється()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CalculationResult(
            1, VersionId, 202601, 700, null, "tons", 1m, TonneUnit, (CalculationResultKind)9));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Directive", "HSE301-F6")]
    public void Крок_трейсу_без_адреси_доки_її_не_задано()
    {
        var step = new CalculationStep(1, 202601, 3, "M_t");

        Assert.Null(step.DocumentId);
        Assert.Null(step.SourceRowKey);
        Assert.Null(step.SubstanceEntryId);

        step.SetAddress(700, "E-2026-01-001", 901);

        Assert.Equal((700L, "E-2026-01-001", 901L), (step.DocumentId!.Value, step.SourceRowKey!, step.SubstanceEntryId!.Value));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Directive", "HSE301-F6")]
    [InlineData("E-2026-01-001", null)]
    [InlineData(null, 901L)]
    public void Рядок_чи_речовина_без_документа_не_є_адресою(string? rowKey, long? substance)
    {
        var step = new CalculationStep(1, 202601, 3, "M_t");

        // Ключ рядка унікальний лише в межах документа: без документа такий
        // крок показався б у чужій комірці.
        Assert.Throws<ArgumentException>(() => step.SetAddress(null, rowKey, substance));
        Assert.Null(step.SourceRowKey);
        Assert.Null(step.SubstanceEntryId);
    }
}
