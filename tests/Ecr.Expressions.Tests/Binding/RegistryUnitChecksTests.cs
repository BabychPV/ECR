// tests/Ecr.Expressions.Tests/Binding/RegistryUnitChecksTests.cs
using Ecr.Expressions.Binding;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// Перевірка 20 (`02b` §12, FEATURE-REGISTRY-TABLES §5.5): одиниці полів
/// довідника беруть участь у перевірках 9–10 — неявних конверсій немає і тут.
/// </summary>
public sealed class RegistryUnitChecksTests
{
    private const int GramPerMole = 10;
    private const int Tonne = 1;
    private const int Celsius = 20;
    private const int Kelvin = 21;

    private static readonly UnitChecker Checker = new();

    /// <remarks>
    /// Мутація: у <c>UnitChecker.CheckRegistryScan</c> повертати <c>null</c> для
    /// <c>REGSUM</c> — тест червоний (агрегат став безрозмірним і додавання
    /// г/моль до тонн пройшло).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Агрегат_має_одиницю_виразу_і_не_складається_з_іншою_розмірністю()
    {
        var context = Context();

        var (unit, diagnostics) = Check("REGSUM('GAS_COMPOSITION', TRUE, ROW.COMPONENT.MW)", context);
        Assert.Equal(GramPerMole, unit);
        Assert.Empty(diagnostics);

        var (_, mixed) = Check("REGSUM('GAS_COMPOSITION', TRUE, ROW.COMPONENT.MW) + [Mass]", context);
        var diagnostic = Assert.Single(mixed);
        Assert.Equal("ECR-TMPL-4223", diagnostic.Code);
        Assert.Equal("expr.unit.dimensionMismatch", diagnostic.MessageKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGFIELD_має_одиницю_поля_і_вимагає_CONVERT()
    {
        var context = Context();

        var (unit, _) = Check("REGFIELD(REGFIND('STREAM_CASE', [Stream], [CaseName]), 'T_C')", context);
        Assert.Equal(Celsius, unit);

        // °C + K — та сама розмірність, інша одиниця: без CONVERT відмова (D-74).
        var (_, diagnostics) = Check("REGFIELD(REGFIND('STREAM_CASE', [Stream], [CaseName]), 'T_C') + [Kelvin]", context);
        Assert.Equal("expr.unit.addNeedsConvert", Assert.Single(diagnostics).MessageKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Кількість_і_пошук_запису_безрозмірні_а_однакові_одиниці_складаються()
    {
        var context = Context();

        Assert.Null(Check("REGCOUNT('GAS_COMPOSITION', TRUE)", context).Unit);
        Assert.Null(Check("REGFIND('COMPONENT', 'H2S')", context).Unit);

        var (unit, diagnostics) = Check(
            "REGMAX('GAS_COMPOSITION', TRUE, ROW.COMPONENT.MW) - REGMIN('GAS_COMPOSITION', TRUE, ROW.COMPONENT.MW)", context);
        Assert.Equal(GramPerMole, unit);
        Assert.Empty(diagnostics);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Порівняння_в_фільтрі_звіряє_одиниці_поля_рядка()
    {
        // Фільтр рахується в області рядка: ROW.COMPONENT.MW у г/моль проти
        // колонки в тоннах — та сама відмова, що й поза агрегатом.
        var (_, diagnostics) = Check("REGCOUNT('GAS_COMPOSITION', ROW.COMPONENT.MW > [Mass])", Context());

        Assert.Equal("expr.unit.dimensionMismatch", Assert.Single(diagnostics).MessageKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Без_форм_довідників_поля_безрозмірні()
    {
        var context = Context();
        context.Registries = null;

        var (unit, diagnostics) = Check("REGSUM('GAS_COMPOSITION', TRUE, ROW.COMPONENT.MW) + [Mass]", context);

        Assert.Equal(Tonne, unit);
        Assert.Empty(diagnostics);
    }

    private static TestBindingContext Context()
    {
        var context = new TestBindingContext
        {
            Registries = InMemoryRegistryShapes.Hse301(mwUnit: GramPerMole, temperatureUnit: Celsius),
        };

        context.ColumnRegistries["Stream"] = "STREAM";
        context.ColumnUnits["Mass"] = Tonne;
        context.ColumnUnits["Kelvin"] = Kelvin;
        context.Dimensions[GramPerMole] = 7;
        context.Dimensions[Tonne] = 1;
        context.Dimensions[Celsius] = 4;
        context.Dimensions[Kelvin] = 4;
        return context;
    }

    private static (int? Unit, List<ExpressionDiagnostic> Diagnostics) Check(string text, TestBindingContext context)
    {
        var parsed = Expr.Parse(text);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var diagnostics = new List<ExpressionDiagnostic>();
        var unit = Checker.Check(parsed.Expression!.Root, context, diagnostics);
        return (unit, diagnostics);
    }
}
