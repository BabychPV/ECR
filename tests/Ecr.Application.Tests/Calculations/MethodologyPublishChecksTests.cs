// tests/Ecr.Application.Tests/Calculations/MethodologyPublishChecksTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
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
    [Trait("Requirement", "ФВ-9.14")]
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
    [Trait("Requirement", "ФВ-9.14")]
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
    [Trait("Requirement", "ФВ-9.14")]
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
    [Trait("Requirement", "ФВ-9.14")]
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
    [Trait("Requirement", "ФВ-9.14")]
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

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Токен_поза_списком_аргументів_відхиляє_публікацію_кодом_0432()
    {
        // ⛔ Упаде, щойно джерелом істини про аргументи стане текст виразу.
        // Збірка підставить рівно `Total` і `Density`; `@Duration` у вираз не
        // потрапить, і формула поверне правдоподібне число — не помилку.
        // Замір корпусу: 38 таких токенів у двох формулах `Flert`.
        var thrown = Assert.Throws<BusinessRuleException>(() => CheckWithArguments(
            "Flert_Emission", "@Total * @Density / @Duration", "Total;Density"));

        Assert.Equal("ECR-CALC-0432", thrown.ErrorCode);

        // Поіменно: методологу треба знати, ЯКИЙ токен дописати в список.
        Assert.Contains("Duration", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("Flert_Emission", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Оголошений_і_невжитий_аргумент_публікацію_не_блокує()
    {
        // ⚠ Межа правила, і вона дорожча за саме правило: 359 таких аргументів
        // у 186 формулах корпусу. Відмова тут зупинила б міграцію.
        var warnings = new List<string>();

        var problems = CheckWithArguments(
            "Total", "@Fuel * 2", "Fuel;Density", warnings);

        Assert.Empty(problems);
        Assert.Contains(warnings, w => w.Contains("Density", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Контекстний_аргумент_попередження_не_дає()
    {
        // ⛔ Глушник обов'язковий: без нього кожна публікація дає 186
        // попереджень про системний контекст, їх перестають читати — і разом
        // із ними перестають бачити ті кілька, що означають описку.
        var warnings = new List<string>();

        CheckWithArguments("Total", "@Fuel * 2", "Fuel;CalculationDate", warnings);

        Assert.Empty(warnings);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Формула_без_оголошеного_списку_звірки_аргументів_не_проходить()
    {
        // ⛔ «Списку немає» і «список порожній» — різні стани. Колонки під
        // список у `calc.MethodologyFormula` сьогодні немає (`D2-101`), і
        // зведення їх до одного відхиляло б КОЖНУ формулу кожної версії.
        var problems = Check(
            [Formula("Total", "@Fuel * @Density", FormulaResultType.Number)], [], []);

        Assert.Empty(problems);
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

    /// <summary>Одна формула з оголошеним <c>;</c>-списком аргументів.</summary>
    private IReadOnlyList<string> CheckWithArguments(
        string code, string expression, string declaration, ICollection<string>? warnings = null)
        => MethodologyPublishChecks.Check(
            [
                new ParsedFormula(
                    code,
                    FormulaResultType.Number,
                    Root(expression),
                    ArgumentDeclarationChecker.ParseDeclaration(declaration)),
            ],
            [],
            [],
            contextualArguments: null,
            warnings: warnings);

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
