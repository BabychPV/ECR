// tests/Ecr.Application.Tests/Calculations/MethodologyPublishChecksTests.cs
using Ecr.Application.Calculations;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Ast;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Перевірки типів при публікації версії методології (директива ПК-1 №05,
/// поправка 2-біс).
/// </summary>
/// <remarks>
/// ⛔ Половина цих тестів стереже правило, а половина — його <b>межу</b>.
/// Початкове формулювання пакета («нечислова константа — помилка публікації»)
/// було перевернуте: воно відхилило б 108 законних рядків. Тому тут перевіряється
/// не тільки що заборонене відхиляється, а й що дозволене проходить — інакше
/// «заборонити все» пройшло б половину набору.
/// </remarks>
public sealed class MethodologyPublishChecksTests
{
    private const int VersionId = 51;
    private const int TonneUnit = 23;

    private readonly RealFormulaEngine _engine = new();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.15")]
    public void Мітка_категорії_у_виразі_відхиляє_публікацію()
    {
        // ⛔ Упаде, якщо мітку зрівняти з текстовою константою. Підставлена у
        // вираз, вона порівнює ключ звуження зі значенням: результат — число,
        // помилки немає, і побачать це через місяць на звірці.
        var problems = Check(
            [Formula("Total", "if(@Land_Category = CST.k1_Season_, 1, 2)", FormulaResultType.Number)],
            [Label("k1_Season_", "<1500")],
            []);

        Assert.Contains(problems, p => p.Contains("k1_Season_", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.15")]
    public void Текстова_константа_в_порівнянні_проблемою_НЕ_є()
    {
        // ⛔ Найважливіший тест набору: він стереже саме те, що директива
        // сформулювала навпаки. ~90 текстових констант корпусу стоять рівно в
        // цій позиції; правило «нечислове — помилка» зробило б помилкою кожну.
        var problems = Check(
            [Formula(
                "Total",
                "if(@Land_Category = CST.k1_CategorySelection_, 1, 2)",
                FormulaResultType.Number)],
            [Text("k1_CategorySelection_", "Summer")],
            []);

        Assert.Empty(problems);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.15")]
    public void Текстова_константа_в_арифметиці_відхиляється()
    {
        // ⚠ Той самий `'-'`, лише класифікований як текст. У порівнянні він
        // законний, у множенні — ні, і різницю видно тільки з позиції у виразі.
        var problems = Check(
            [Formula("Total", "CST.n_ECW_C11_13_ * 2", FormulaResultType.Number)],
            [Text("n_ECW_C11_13_", "-")],
            []);

        Assert.Contains(problems, p => p.Contains("арифметичній", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.12")]
    public void Нерозібрана_числова_константа_названа_поіменно()
    {
        // ⛔ Упаде, щойно нерозібраний рядок почне ставати нулем. 16 формул
        // корпусу читають `n_ECW_C11_13_`, і нуль у них — правдоподібне число,
        // яке не викликає жодного питання.
        var dash = MethodologyConstant.FromImport(
            VersionId, EcrCode.Create("n_ECW_C11_13_"), "-", ConstantKind.Numeric, TonneUnit);

        var problems = Check([Formula("Total", "1 + 2", FormulaResultType.Number)], [dash], []);

        Assert.Contains(problems, p => p.Contains("n_ECW_C11_13_", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.15")]
    public void Формула_що_повертає_лише_текст_не_може_бути_числовою()
    {
        const string verdict = "if(@Excess > 0, 'Сверхнорматив', 'В пределе норматива')";

        // ⛔ Оголошена числовою, така формула пише текст у
        // `calc.CalculationResult.Value decimal(28,10)`.
        var wrong = Check([Formula("Verdict", verdict, FormulaResultType.Number)], [], []);
        Assert.Contains(wrong, p => p.Contains("лише текст", StringComparison.Ordinal));

        // ⚠ Межа правила: та сама формула з правильним оголошенням проходить.
        Assert.Empty(Check([Formula("Verdict", verdict, FormulaResultType.Text)], [], []));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.15")]
    public void Числова_формула_оголошена_текстовою_відхиляється()
    {
        // Зворотний бік: число, оголошене текстом, тихо проходить у звіт рядком
        // і ламає сортування та підсумки — помилки при цьому немає ніде.
        var problems = Check(
            [Formula("Total", "@Fuel * 2", FormulaResultType.Text)], [], []);

        Assert.Contains(problems, p => p.Contains("повертає число", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-16.6")]
    public void Текстова_формула_оголошена_виходом_методології_відхиляється()
    {
        // ⛔ Оголошений вихід лягає в числову колонку — іншої в
        // `calc.CalculationResult` немає. Текстовий результат методології
        // сьогодні нікуди подіти, і мовчазна спроба дала б нуль у звіті.
        var problems = Check(
            [Formula("Verdict", "'Превышение!!!'", FormulaResultType.Text)],
            [],
            [new MethodologyOutput(VersionId, EcrCode.Create("Verdict"), TonneUnit)]);

        Assert.Contains(problems, p => p.Contains("числовій колонці", StringComparison.Ordinal));
    }

    // ─────────────────────────────────────────────────────────────────────────

    private IReadOnlyList<string> Check(
        IReadOnlyList<(string Code, string Expression, FormulaResultType ResultType)> formulas,
        IReadOnlyList<MethodologyConstant> constants,
        IReadOnlyList<MethodologyOutput> outputs)
        => MethodologyPublishChecks.Check(
            formulas.Select(f => new ParsedFormula(f.Code, f.ResultType, Root(f.Expression))).ToList(),
            constants,
            outputs);

    /// <summary>
    /// Розбір — СПРАВЖНІЙ: заглушити його означало б перевіряти заглушку.
    /// </summary>
    private AstNode? Root(string expression)
        => _engine.Parse(expression, ExpressionDialect.Methodology).Expression?.Root;

    private static (string Code, string Expression, FormulaResultType ResultType) Formula(
        string code, string expression, FormulaResultType resultType)
        => (code, expression, resultType);

    private static MethodologyConstant Text(string code, string value)
        => MethodologyConstant.OfText(VersionId, EcrCode.Create(code), value, ConstantKind.Text);

    private static MethodologyConstant Label(string code, string value)
        => MethodologyConstant.OfText(VersionId, EcrCode.Create(code), value, ConstantKind.CategoryLabel);
}
