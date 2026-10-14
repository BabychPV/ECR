// tests/Ecr.Expressions.Tests/Functions/SubstanceApplicableFunctionTests.cs
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Functions;

/// <summary>
/// <c>SUBSTANCE_APPLICABLE(registry, scope, kind, value)</c> (02b «Застосовність речовини до рядка»): чи є в
/// довіднику запис (методологія, вид селектора, значення селектора, код речовини ПОТОЧНОГО проходу).
/// </summary>
/// <remarks>
/// Мутаційні докази (лише цей клас): прибрати перевірку маркера (повертати FALSE при його відсутності) —
/// червоніє <see cref="Методологія_без_маркера_не_фільтрується"/>; брати код речовини з іншого місця (прибрати
/// його з ключа) — червоніють <see cref="Запис_є_лише_для_своїх_речовин_і_селектора"/> і
/// <see cref="Різні_методології_одного_довідника_не_видно_одна_одній"/>.
/// </remarks>
public sealed class SubstanceApplicableFunctionTests
{
    private const string Registry = "APPL";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Каталог_знає_функцію_як_розширення_з_булевим_результатом_і_чотирма_аргументами()
    {
        var signature = DialectCatalog.Find("SUBSTANCE_APPLICABLE");

        Assert.NotNull(signature);
        Assert.Equal(4, signature.MinArgs);
        Assert.Equal(4, signature.MaxArgs);
        Assert.Equal(ExpressionValueType.Boolean, signature.ResultType);
        Assert.Equal(FunctionTier.Extension, DialectCatalog.TierOf("SUBSTANCE_APPLICABLE"));
        Assert.True(DialectCatalog.IsAllowedIn("SUBSTANCE_APPLICABLE", NumericMode.Legacy)); // D-426
        Assert.True(DialectCatalog.IsAllowedIn("SUBSTANCE_APPLICABLE", NumericMode.Strict));

        Assert.True(Expr.Parse("SUBSTANCE_APPLICABLE('APPL', 'M', 'InvNum', @X)", ExpressionDialect.Methodology).IsSuccess);
        Assert.False(Expr.Parse("SUBSTANCE_APPLICABLE('APPL', 'M', 'InvNum')", ExpressionDialect.Methodology).IsSuccess);
        Assert.False(Expr.Parse("SUBSTANCE_APPLICABLE('APPL', 'M', 'InvNum', 'x')", ExpressionDialect.Template).IsSuccess);
    }

    [Theory]
    [InlineData("333", "FG_Makat", true)]
    [InlineData("1728", "FG_Makat", true)]
    [InlineData("370", "FG_Makat", false)]   // у словнику FG_Makat немає 370
    [InlineData("370", "FG_Internal", true)]
    [InlineData("333", "Невідомий", false)]  // значення селектора без запису
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Запис_є_лише_для_своїх_речовин_і_селектора(string substance, string selector, bool expected)
    {
        var context = With(substance, selector);

        var value = Eval("SUBSTANCE_APPLICABLE('APPL', 'M1', 'Stream', @Stream)", context);

        Assert.Equal(ExpressionValueType.Boolean, value.Type);
        Assert.Equal(expected, value.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Різні_методології_одного_довідника_не_видно_одна_одній()
    {
        // FG_Makat записано лише для M1: M2 має маркер, але FG_Makat у ній немає -> FALSE.
        var context = With("333", "FG_Makat");

        Assert.Equal(true, Eval("SUBSTANCE_APPLICABLE('APPL', 'M1', 'Stream', @Stream)", context).Value);
        Assert.Equal(false, Eval("SUBSTANCE_APPLICABLE('APPL', 'M2', 'Stream', @Stream)", context).Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Методологія_без_маркера_не_фільтрується()
    {
        // M3 у довіднику немає зовсім: правило не діє (TRUE), навіть якщо значення селектора невідоме.
        var context = With("333", "Будь-що");

        Assert.Equal(true, Eval("SUBSTANCE_APPLICABLE('APPL', 'M3', 'Stream', @Stream)", context).Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Без_речовини_проходу_функція_дає_TRUE_щоб_Row_фаза_бачила_ключ()
    {
        var context = With(substance: null, "FG_Makat");

        Assert.Equal(true, Eval("SUBSTANCE_APPLICABLE('APPL', 'M1', 'Stream', @Stream)", context).Value);

        // Навіть коли значення селектора порожнє: Row-фаза відмовляє рядку за порожнім ключем правила, а не функція.
        var blank = With(substance: string.Empty, "Невідомий");
        Assert.Equal(true, Eval("SUBSTANCE_APPLICABLE('APPL', 'M1', 'Stream', @Stream)", blank).Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Порожнє_значення_селектора_при_наявному_маркері_дає_FALSE()
    {
        var context = With("333", string.Empty);

        Assert.Equal(false, Eval("SUBSTANCE_APPLICABLE('APPL', 'M1', 'Stream', @Stream)", context).Value);
        Assert.Equal(false, Eval("SUBSTANCE_APPLICABLE('APPL', 'M1', '', 'x')", context).Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Помилка_аргументу_поширюється_а_відсутній_довідник_дає_REF()
    {
        var context = With("333", "FG_Makat");

        var failed = Eval("SUBSTANCE_APPLICABLE('APPL', 'M1', 'Stream', 1 / 0)", context);
        Assert.Equal(ExpressionErrors.DivideByZero, failed.ErrorCode);

        var missing = Eval("SUBSTANCE_APPLICABLE('NO_SUCH', 'M1', 'Stream', @Stream)", context);
        Assert.Equal(ExpressionErrors.BadReference, missing.ErrorCode);

        // Контекст без знімка довідників - теж #REF, а не виняток.
        var noSnapshot = new TestEvaluationContext { CurrentSubstanceCode = "333" };
        noSnapshot.Arguments["Stream"] = ExpressionValue.Text("FG_Makat");
        Assert.Equal(
            ExpressionErrors.BadReference,
            Eval("SUBSTANCE_APPLICABLE('APPL', 'M1', 'Stream', @Stream)", noSnapshot).ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void У_правилі_категорії_порожній_ключ_для_речовини_без_запису()
    {
        // Типова форма правила (02b): if(SUBSTANCE_APPLICABLE(...), <ключ>, '').
        const string rule = "if(SUBSTANCE_APPLICABLE('APPL', 'M1', 'Stream', @Stream), 'KEY_' + @Stream, '')";

        Assert.Equal("KEY_FG_Makat", Eval(rule, With("333", "FG_Makat")).Value);
        Assert.Equal(string.Empty, Eval(rule, With("370", "FG_Makat")).Value);

        // Без речовини (Row-фаза) - ключ є завжди.
        Assert.Equal("KEY_FG_Makat", Eval(rule, With(null, "FG_Makat")).Value);
    }

    private static ExpressionValue Eval(string expression, IEvaluationContext context)
        => Expr.Eval(expression, context, ExpressionDialect.Methodology);

    private static IEvaluationContext With(string? substance, string selector)
    {
        var inner = new TestEvaluationContext { CurrentSubstanceCode = substance };
        inner.Arguments["Stream"] = ExpressionValue.Text(selector);
        return Snapshot().Attach(inner);
    }

    private static InMemoryRegistrySnapshot Snapshot()
    {
        var snapshot = new InMemoryRegistrySnapshot()
            .AddRegistry(
                Registry, ["METHODOLOGY", "SELECTOR_KIND", "SELECTOR_VALUE", "SUBSTANCE_CODE"],
                primaryKey: ["METHODOLOGY", "SELECTOR_KIND", "SELECTOR_VALUE", "SUBSTANCE_CODE"]);

        long id = 0;
        void Add(string scope, string kind, string value, string code)
            => snapshot.AddEntry(
                Registry, ++id, $"A{id}",
                new()
                {
                    ["METHODOLOGY"] = ExpressionValue.Text(scope),
                    ["SELECTOR_KIND"] = ExpressionValue.Text(kind),
                    ["SELECTOR_VALUE"] = ExpressionValue.Text(value),
                    ["SUBSTANCE_CODE"] = ExpressionValue.Text(code),
                },
                ordinal: (int)id);

        // Маркери методологій і записи.
        Add("M1", "*", "*", "*");
        Add("M2", "*", "*", "*");
        foreach (var code in new[] { "333", "415", "416", "1728" })
        {
            Add("M1", "Stream", "FG_Makat", code);
        }

        foreach (var code in new[] { "333", "370", "415" })
        {
            Add("M1", "Stream", "FG_Internal", code);
        }

        Add("M2", "Stream", "FG_Export", "333");
        return snapshot;
    }
}
