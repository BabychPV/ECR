// tests/Ecr.Expressions.Tests/Functions/CurrentSubstanceSymbolTests.cs
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Functions;

/// <summary>
/// Символ «поточна речовина» діалекту методологій: <c>CURRENT_SUBSTANCE()</c> повертає код речовини
/// ПОТОЧНОГО проходу рахунку (текст), тож <c>CURRENT_SUBSTANCE() + '_mass'</c> будує ключ показника
/// усередині <c>REGFIND</c>.
/// </summary>
/// <remarks>
/// Мутаційні докази (лише цей клас): повернути з <c>MethodologyFunctions.CurrentSubstance</c> порожній
/// рядок замість <c>#REF</c> — червоніє <see cref="Без_речовини_символ_дає_REF_а_не_порожній_ключ"/>;
/// прибрати делегування <c>CurrentSubstanceCode</c> у <c>RegistryRowScope</c> — червоніє
/// <see cref="Символ_видно_всередині_агрегата_довідника"/>.
/// </remarks>
public sealed class CurrentSubstanceSymbolTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Каталог_знає_символ_як_розширення_без_аргументів_з_текстовим_результатом()
    {
        var signature = DialectCatalog.Find("CURRENT_SUBSTANCE");

        Assert.NotNull(signature);
        Assert.Equal(0, signature.MinArgs);
        Assert.Equal(0, signature.MaxArgs);
        Assert.Equal(ExpressionValueType.Text, signature.ResultType);
        Assert.Equal(FunctionTier.Extension, DialectCatalog.TierOf("CURRENT_SUBSTANCE"));

        // ✎ D-426: дозволений і в Legacy (на відміну від SUBSTANCE).
        Assert.True(DialectCatalog.IsAllowedIn("CURRENT_SUBSTANCE", NumericMode.Legacy));
        Assert.False(DialectCatalog.IsAllowedIn("SUBSTANCE", NumericMode.Legacy));
        Assert.True(DialectCatalog.IsAllowedIn("CURRENT_SUBSTANCE", NumericMode.Strict));

        // Регістр значущий, як у решти каталогу.
        Assert.Null(DialectCatalog.Find("current_substance"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Символ_повертає_код_речовини_і_будує_ключ_конкатенацією_текстів()
    {
        var context = new TestEvaluationContext { CurrentSubstanceCode = "301" };

        var code = Eval("CURRENT_SUBSTANCE()", context);
        var key = Eval("CURRENT_SUBSTANCE() + '_mass'", context);

        Assert.Equal(ExpressionValueType.Text, code.Type);
        Assert.Equal("301", code.Value);
        Assert.Equal("301_mass", key.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Без_речовини_символ_дає_REF_а_не_порожній_ключ()
    {
        // Версія без складу речовин, Row-фаза або речовина без коду: жодного ключа не вигадуємо.
        var none = Eval("CURRENT_SUBSTANCE()", new TestEvaluationContext());
        var blank = Eval("CURRENT_SUBSTANCE()", new TestEvaluationContext { CurrentSubstanceCode = string.Empty });

        Assert.Equal(ExpressionErrors.BadReference, none.ErrorCode);
        Assert.Equal(ExpressionErrors.BadReference, blank.ErrorCode);

        // Помилка поширюється крізь конкатенацію: '#REF' + '_mass' не стає ключем «_mass».
        Assert.Equal(
            ExpressionErrors.BadReference,
            Eval("CURRENT_SUBSTANCE() + '_mass'", new TestEvaluationContext()).ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void У_діалекті_шаблонів_символу_немає_а_аргументи_відхиляються()
    {
        Assert.False(Expr.Parse("CURRENT_SUBSTANCE()", ExpressionDialect.Template).IsSuccess);
        Assert.False(Expr.Parse("CURRENT_SUBSTANCE(1)", ExpressionDialect.Methodology).IsSuccess);
        Assert.True(Expr.Parse("CURRENT_SUBSTANCE()", ExpressionDialect.Methodology).IsSuccess);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Вкладений_REGFIND_адресує_показник_за_речовиною_проходу()
    {
        var snapshot = Composition();
        const string formula =
            "REGFIELD(REGFIND('COMP', [Period].Year, [Period].Sequence, @Stream, CURRENT_SUBSTANCE() + '_mass'), 'VALUE')";

        decimal? For(string substance, int month = 2)
        {
            var inner = new TestEvaluationContext
            {
                CurrentSubstanceCode = substance,
                Period = new PeriodContext(
                    new DateOnly(2026, month, 1), new DateOnly(2026, month, 28), CalendarMode.Actual, 2026, (byte)month),
            };
            inner.Arguments["Stream"] = ExpressionValue.Text("FG");
            return Eval(formula, snapshot.Attach(inner)).AsNumber();
        }
        // Той самий рядок, той самий потік, той самий місяць — різні речовини дають різні числа.
        Assert.Equal(0.9m, For("Methane"));
        Assert.Equal(0.1m, For("Ethane"));

        // Інший місяць — інше число; речовини, якої в складі немає, — #N/A (null як число), а не нуль.
        Assert.Equal(0.8m, For("Methane", month: 3));
        Assert.Null(For("Propane"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Символ_видно_всередині_агрегата_довідника()
    {
        // REGCOUNT відкриває область рядка (RegistryRowScope): символ мусить бути видимим і там.
        var snapshot = Composition();
        var context = snapshot.Attach(new TestEvaluationContext { CurrentSubstanceCode = "Methane" });

        var value = Eval("REGCOUNT('COMP', ROW.INDICATOR = CURRENT_SUBSTANCE() + '_mass')", context);

        Assert.Equal(2m, value.AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Фрагмент_CompositionMass_для_методології_Land_потік_за_текстом_а_показник_за_речовиною()
    {
        // Рекомендований текст аргументу CompositionMass у формулі методології Land (LP-10): потік - префікс
        // за текстом палива зі словника, показник - `{код речовини}_mass`; обидва REGFIND вкладені.
        const string fragment =
            "REGFIELD(REGFIND('COMP', [Period].Year, [Period].Sequence, "
            + "REGFIELD(REGFIND('STREAM', @TypeOfFuel), 'PREFIX'), CURRENT_SUBSTANCE() + '_mass'), 'VALUE')";
        var snapshot = Composition();

        decimal? For(string substance)
        {
            var inner = new TestEvaluationContext
            {
                CurrentSubstanceCode = substance,
                Period = new PeriodContext(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), CalendarMode.Actual, 2026, 2),
            };
            inner.Arguments["TypeOfFuel"] = ExpressionValue.Text("Fuel gas (Internal)");
            return Eval(fragment, snapshot.Attach(inner)).AsNumber();
        }

        Assert.Equal(0.9m, For("Methane"));
        Assert.Equal(0.1m, For("Ethane"));
    }

    private static ExpressionValue Eval(string expression, IEvaluationContext context)
        => Expr.Eval(expression, context, ExpressionDialect.Methodology);

    private static InMemoryRegistrySnapshot Composition()
    {
        var snapshot = new InMemoryRegistrySnapshot()
            .AddRegistry("COMP", ["YEAR", "MONTH", "STREAM", "INDICATOR", "VALUE"], primaryKey: ["YEAR", "MONTH", "STREAM", "INDICATOR"]);

        snapshot.AddRegistry("STREAM", ["TEXT", "PREFIX"], primaryKey: ["TEXT"]);
        snapshot.AddEntry(
            "STREAM", 900, "S1",
            new() { ["TEXT"] = ExpressionValue.Text("Fuel gas (Internal)"), ["PREFIX"] = ExpressionValue.Text("FG") },
            ordinal: 1);

        long id = 100;
        void Add(int year, int month, string stream, string indicator, decimal value)
            => snapshot.AddEntry(
                "COMP", ++id, $"E{id}",
                new()
                {
                    ["YEAR"] = ExpressionValue.Number(year),
                    ["MONTH"] = ExpressionValue.Number(month),
                    ["STREAM"] = ExpressionValue.Text(stream),
                    ["INDICATOR"] = ExpressionValue.Text(indicator),
                    ["VALUE"] = ExpressionValue.Number(value),
                },
                ordinal: (int)id);

        Add(2026, 2, "FG", "Methane_mass", 0.9m);
        Add(2026, 2, "FG", "Ethane_mass", 0.1m);
        Add(2026, 3, "FG", "Methane_mass", 0.8m);
        return snapshot;
    }
}
