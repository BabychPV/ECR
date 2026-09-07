// tests/Ecr.Expressions.Tests/Binding/TypeCheckerTests.cs
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// Типізація при публікації. Мета — щоб несумісність типів була **помилкою
/// публікації**, а не дивним числом у звіті через місяць.
/// </summary>
public sealed class TypeCheckerTests
{
    private static readonly TypeChecker Checker = new();

    private static (ExpressionValueType Type, List<ExpressionDiagnostic> Diagnostics) Check(
        string expression, TestBindingContext? context = null)
    {
        var parsed = Expr.Parse(expression);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var diagnostics = new List<ExpressionDiagnostic>();
        var type = Checker.Check(parsed.Expression!.Root, context ?? new TestBindingContext(), diagnostics);
        return (type, diagnostics);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Число_плюс_текст_відхиляється()
    {
        // ⚠ Excel тут вгадав би і дав «майже правильне» число. Мовчазне
        // приведення — рівно те, від чого система відходить (02b §5).
        var (_, diagnostics) = Check("1 + 'a'");

        Assert.Contains(diagnostics, d => d.Code == "ECR-TMPL-4222");

        // Конкатенація того самого — дозволена: там приведення оголошене.
        Assert.Empty(Check("1 & 'a'").Diagnostics);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Різниця_дат_дає_число()
    {
        var context = new TestBindingContext();
        context.ColumnTypes["Start"] = ExpressionValueType.Date;
        context.ColumnTypes["End"] = ExpressionValueType.Date;

        var (type, diagnostics) = Check("[End] - [Start]", context);

        Assert.Equal(ExpressionValueType.Number, type);
        Assert.Empty(diagnostics);

        // …а додавання дат сенсу не має і відхиляється.
        Assert.NotEmpty(Check("[End] + [Start]", context).Diagnostics);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Boolean_в_арифметиці_відхиляється()
    {
        var context = new TestBindingContext();
        context.ColumnTypes["IsActive"] = ExpressionValueType.Boolean;

        var (_, diagnostics) = Check("[IsActive] + 1", context);

        // TRUE = 1 — конвенція Excel, і саме вона робить «суму галочок»
        // непомітною помилкою.
        Assert.Contains(diagnostics, d => d.Code == "ECR-TMPL-4222");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Порівняння_числа_з_текстом_дає_помилку_публікації()
    {
        var context = new TestBindingContext();
        context.ColumnTypes["Note"] = ExpressionValueType.Text;

        var (type, diagnostics) = Check("[Note] > 1", context);

        Assert.Equal(ExpressionValueType.Boolean, type);
        Assert.Contains(diagnostics, d => d.Code == "ECR-TMPL-4222");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Несумісні_одиниці_без_CONVERT_дають_ECR_TMPL_4223()
    {
        // Тонни (1) і кілограми (2) однієї розмірності — але неявних
        // конверсій не буває (D-74): або CONVERT, або відмова публікації.
        var context = new TestBindingContext();
        context.ColumnUnits["Tons"] = 1;
        context.ColumnUnits["Kilos"] = 2;
        context.Dimensions[1] = 1;
        context.Dimensions[2] = 1;

        var parsed = Expr.Parse("[Tons] + [Kilos]");
        var diagnostics = new List<ExpressionDiagnostic>();
        new UnitChecker().Check(parsed.Expression!.Root, context, diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "ECR-TMPL-4223");

        // Та сама одиниця з обох боків — жодних зауважень.
        var same = new List<ExpressionDiagnostic>();
        new UnitChecker().Check(
            Expr.Parse("[Tons] + [Tons]").Expression!.Root, context, same);
        Assert.Empty(same);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Текстова_константа_у_порівнянні_не_є_порівнянням_різних_типів()
    {
        // ⛔ Тут стояло «константа — завжди Number», і це неправда після
        // появи `ConstantKind`: з 6507 констант корпусу 108 нечислові, ~90 із
        // них ужиті рівно в цій позиції. Жорстке `Number` перетворює кожну на
        // «порівняння різних типів» — хибну відмову публікації, щойно
        // перевірку типів увімкнуть для методологій.
        var context = new MethodologyTypes();
        context.Constants["k1_CategorySelection_"] = ExpressionValueType.Text;

        var (type, diagnostics) = CheckMethodology(
            "CST.k1_CategorySelection_ = 'Summer'", context);

        Assert.Equal(ExpressionValueType.Boolean, type);
        Assert.Empty(diagnostics);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Текстова_константа_в_арифметиці_лишається_помилкою()
    {
        // ⚠ Межа правила, і без неї виправлення було б гіршим за дефект:
        // «нічого не знаю про константи» пропустило б `'-' * 2` — той самий
        // `n_ECW_C11_13_`, що читають 16 формул корпусу.
        var context = new MethodologyTypes();
        context.Constants["n_ECW_C11_13_"] = ExpressionValueType.Text;

        var (_, diagnostics) = CheckMethodology("CST.n_ECW_C11_13_ * 2", context);

        Assert.Contains(diagnostics, d => d.Code == "ECR-TMPL-4222");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Числова_константа_в_арифметиці_проходить()
    {
        // Другий бік межі: 6399 числових констант корпусу мусять проходити.
        var context = new MethodologyTypes();
        context.Constants["EF_CO2"] = ExpressionValueType.Number;

        var (type, diagnostics) = CheckMethodology("CST.EF_CO2 * 2", context);

        Assert.Equal(ExpressionValueType.Number, type);
        Assert.Empty(diagnostics);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Контекст_без_знання_про_константи_нічого_про_них_не_стверджує()
    {
        // ⛔ Перевіряється саме ЗАМОВЧУВАННЯ інтерфейсу: контекст нижче
        // `GetConstantType` не реалізує взагалі. `Null` сумісний з усім, тобто
        // мовчання; `Number` — твердження, і саме воно давало ~90 хибних
        // помилок. Контекст версії ШАБЛОНУ констант методології не знає, і
        // судити про них не має права.
        var (_, diagnostics) = CheckMethodology(
            "CST.k1_CategorySelection_ = 'Summer'", new BareTypes());

        Assert.Empty(diagnostics);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static (ExpressionValueType Type, List<ExpressionDiagnostic> Diagnostics) CheckMethodology(
        string expression, ITypeContext context)
    {
        var parsed = Expr.Parse(expression, ExpressionDialect.Methodology);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var diagnostics = new List<ExpressionDiagnostic>();
        var type = Checker.Check(parsed.Expression!.Root, context, diagnostics);
        return (type, diagnostics);
    }

    /// <summary>
    /// Типи діалекту методологій — таблиця замість знімка версії шаблону.
    /// </summary>
    /// <remarks>
    /// ⚠ Власний, а не <c>TestBindingContext</c>: той віддає <c>Number</c> на
    /// будь-яку невідому колонку, і саме таке замовчування тут перевіряється.
    /// Контекст, що мовчки називає все числом, зробив би тести зеленими з
    /// хибної причини.
    /// </remarks>
    private sealed class MethodologyTypes : ITypeContext
    {
        public Dictionary<string, ExpressionValueType> Constants { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, ExpressionValueType> Arguments { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public ExpressionValueType GetReferenceType(CellReferenceNode reference)
            => ExpressionValueType.Null;

        public ExpressionValueType GetColumnType(int tableDefId, int columnDefId)
            => ExpressionValueType.Null;

        public ExpressionValueType GetArgumentType(string name)
            => Arguments.GetValueOrDefault(name, ExpressionValueType.Number);

        public ExpressionValueType GetConstantType(string code)
            => Constants.GetValueOrDefault(code, ExpressionValueType.Null);
    }

    /// <summary>
    /// Контекст, який <c>GetConstantType</c> <b>не реалізує</b> — рівно так, як
    /// це роблять контексти версії шаблону.
    /// </summary>
    /// <remarks>
    /// ⚠ Існує окремо від <see cref="MethodologyTypes"/> навмисно: без нього
    /// замовчування інтерфейсу не перевіряв би жоден тест, і зміна `Null` на
    /// `Number` пройшла б непоміченою.
    /// </remarks>
    private sealed class BareTypes : ITypeContext
    {
        public ExpressionValueType GetReferenceType(CellReferenceNode reference)
            => ExpressionValueType.Null;

        public ExpressionValueType GetColumnType(int tableDefId, int columnDefId)
            => ExpressionValueType.Null;

        public ExpressionValueType GetArgumentType(string name)
            => ExpressionValueType.Number;
    }
}
