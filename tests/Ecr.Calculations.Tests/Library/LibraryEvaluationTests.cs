// tests/Ecr.Calculations.Tests/Library/LibraryEvaluationTests.cs
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests.Library;

/// <summary>
/// HSE301 L3: модуль обчислює <c>!Code</c> у формулу імпортованої методології — у контексті
/// рядка викликача, з константами бібліотеки і в області формули бібліотеки.
/// </summary>
/// <remarks>
/// Мутаційні докази (кожен — окремо, після відкату зелені):
/// <list type="bullet">
/// <item>константи бібліотеки брати з викликача (<c>LibraryRun.Create</c> →
/// <c>binding.Constants</c>) — <see cref="Бібліотечна_формула_рахується_з_константами_бібліотеки"/>
/// червоний (M_total 100 замість 160, tons не пишеться: EF немає у викликача);</item>
/// <item>ігнорувати область (усі формули бібліотеки — у фазі речовини) —
/// <see cref="Row_формула_бібліотеки_рахується_раз_на_рядок"/> червоний (два кроки Common_M);</item>
/// <item>джерело в трейсі <c>null</c> — <see cref="Крок_бібліотечної_формули_несе_джерело_у_TraceJson"/>
/// червоний.</item>
/// </list>
/// </remarks>
public sealed class LibraryEvaluationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Бібліотечна_формула_рахується_з_константами_бібліотеки()
    {
        var output = await new LibraryStand().RunAsync();

        // Common_M = 100 · 0.8 (RHO бібліотеки, не 0.5 викликача) = 80; M_total = 2 · 80.
        var total = Assert.Single(output.Values, v => v.OutputCode == "M_total");
        Assert.Null(total.SubstanceEntryId);
        Assert.Equal(160m, total.Value);

        // tons = Common_W + RHO викликача: 80 · 0.1 + 0.5 і 80 · 0.2 + 0.5.
        var tons = output.Values.Where(v => v.OutputCode == "tons").OrderBy(v => v.SubstanceEntryId).ToList();
        Assert.Equal([901, 902], tons.Select(v => v.SubstanceEntryId!.Value));
        Assert.Equal([8.5m, 16.5m], tons.Select(v => v.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Row_формула_бібліотеки_рахується_раз_на_рядок()
    {
        var output = await new LibraryStand().RunAsync();

        var mass = Assert.Single(output.Trace, s => s.StepCode == "Common_M");
        Assert.Null(mass.SubstanceEntryId);

        // Формула речовини бібліотеки — у кожної речовини своя, зі своєю адресою.
        Assert.Equal(
            [901L, 902L],
            output.Trace.Where(s => s.StepCode == "Common_W").Select(s => s.SubstanceEntryId!.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Крок_бібліотечної_формули_несе_джерело_у_TraceJson()
    {
        var output = await new LibraryStand().RunAsync();

        var step = Assert.Single(output.Trace, s => s.StepCode == "Common_M");
        using var json = JsonDocument.Parse(step.Detail!);
        var source = json.RootElement.GetProperty("source");
        Assert.Equal("Common", source.GetProperty("methodology").GetString());
        Assert.Equal(LibraryStand.CommonVersionId, source.GetProperty("versionId").GetInt32());

        // Аргумент бібліотечної формули — комірка рядка ВИКЛИКАЧА.
        var input = Assert.Single(json.RootElement.GetProperty("inputs").EnumerateArray(), i => i.GetProperty("kind").GetString() == "arg");
        Assert.Equal("E-2026-01-001", input.GetProperty("cell").GetProperty("row").GetString());

        // Крок своєї формули джерела не має — його JSON той самий, що до кроку.
        var own = Assert.Single(output.Trace, s => s.StepCode == "M_total" && s.Detail is not null && s.Expression is not null);
        using var ownJson = JsonDocument.Parse(own.Detail!);
        Assert.False(ownJson.RootElement.TryGetProperty("source", out _));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Видимі_формули_бібліотеки_не_пишуться_результатами_викликача()
    {
        var output = await new LibraryStand().RunAsync();

        Assert.DoesNotContain(output.Values, v => v.OutputCode.StartsWith("Common_", StringComparison.Ordinal));

        // Формула поза замиканням не рахується зовсім: її «1 / 0» ніде не видно.
        Assert.DoesNotContain(output.Trace, s => s.StepCode == "Common_Unused");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Посилання_на_бібліотеку_дає_той_самий_результат_що_формула_вписана_вручну()
    {
        // ⛔ Умова інтегратора: той самий рядок, та сама формула — у бібліотеці чи вписана в
        // методологію руками — дають ТЕ САМЕ число, точно, без допуску.
        var viaLibrary = new LibraryStand();
        viaLibrary.Caller(
            [
                LibraryStand.Formula(LibraryStand.CallerVersionId, "M_total", "!Common_M + @Volume", 1, MethodologyFormulaScope.Row),
                LibraryStand.Formula(LibraryStand.CallerVersionId, "tons", "!Common_W * 3", 2, MethodologyFormulaScope.Substance),
            ],
            [LibraryStand.Output("M_total", perSubstance: false), LibraryStand.Output("tons", perSubstance: true)]);
        viaLibrary.Store.GetConstantsAsync(LibraryStand.CallerVersionId, Arg.Any<CancellationToken>())
                  .Returns(new List<MethodologyConstant>());

        var inlined = new LibraryStand();
        inlined.Caller(
            [
                .. LibraryStand.CommonFormulas(visible: false)
                    .Where(f => f.Code != "Common_Unused")
                    .Select(f => LibraryStand.Formula(
                        LibraryStand.CallerVersionId, f.Code, f.Expression, f.EvaluationOrder, f.Scope)),
                LibraryStand.Formula(LibraryStand.CallerVersionId, "M_total", "!Common_M + @Volume", 3, MethodologyFormulaScope.Row),
                LibraryStand.Formula(LibraryStand.CallerVersionId, "tons", "!Common_W * 3", 4, MethodologyFormulaScope.Substance),
            ],
            [LibraryStand.Output("M_total", perSubstance: false), LibraryStand.Output("tons", perSubstance: true)]);
        inlined.Store.GetConstantsAsync(LibraryStand.CallerVersionId, Arg.Any<CancellationToken>())
               .Returns(LibraryStand.CommonConstants(LibraryStand.CallerVersionId));
        inlined.Imports(LibraryStand.CallerVersionId);

        var expected = Outputs(await inlined.RunAsync());
        var actual = Outputs(await viaLibrary.RunAsync());

        Assert.Equal(3, expected.Count);
        Assert.Equal(expected, actual);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Бібліотека_перевидана_з_імпортом_викликача_дає_CYCLE_а_не_виняток()
    {
        // ⛔ Умова інтегратора: викликача опубліковано, потім `Common` перевидали з імпортом
        // самого викликача. Цикл з'являється лише в рантаймі — прогін мусить дати помилку
        // з ключем у трейсі, а не StackOverflow, зависання чи виняток.
        var stand = new LibraryStand();
        var common = new List<MethodologyFormula>
        {
            LibraryStand.Formula(LibraryStand.CommonVersionId, "Common_M", "!Own_F * 2", 1, MethodologyFormulaScope.Row),
        };
        stand.Imports(LibraryStand.CallerVersionId, LibraryStand.CommonContent(common));

        var own = new List<MethodologyFormula>
        {
            LibraryStand.Formula(LibraryStand.CallerVersionId, "Own_F", "!Common_M + 1", 1, MethodologyFormulaScope.Row),
        };
        stand.Imports(
            LibraryStand.CommonVersionId,
            new MethodologyLibraryContent(
                new MethodologyLibrary(LibraryStand.CallerId, "HSE400", LibraryStand.CallerVersionId, ["Own_F"]),
                NumericMode.Strict,
                CalendarMode.Actual,
                own,
                []));
        stand.Caller(
            [LibraryStand.Formula(LibraryStand.CallerVersionId, "M_total", "!Common_M * 2", 1, MethodologyFormulaScope.Row)],
            [LibraryStand.Output("M_total", perSubstance: false)]);

        var output = await stand.RunAsync(TraceLevel.ErrorsOnly);

        Assert.DoesNotContain(output.Values, v => v.OutputCode == "M_total");

        var library = Assert.Single(output.Trace, s => s.StepCode == "Common_M");
        Assert.Equal("#CYCLE", library.TraceJson);
        Assert.Contains("\"source\"", library.Detail!, StringComparison.Ordinal);

        // І крок формули, і крок виходу називають ту саму причину.
        Assert.All(output.Trace.Where(s => s.StepCode == "M_total"), s => Assert.Equal("#CYCLE", s.TraceJson));
        Assert.Equal(2, output.Trace.Count(s => s.StepCode == "M_total"));
    }

    private static List<(string, int?, decimal)> Outputs(CalculationOutput output)
        => [.. output.Values
            .Where(v => v.Kind == CalculationResultKind.Output)
            .OrderBy(v => v.OutputCode, StringComparer.Ordinal)
            .ThenBy(v => v.SubstanceEntryId)
            .Select(v => (v.OutputCode, v.SubstanceEntryId, v.Value))];
}
