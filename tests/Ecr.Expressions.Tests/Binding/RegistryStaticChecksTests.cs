// tests/Ecr.Expressions.Tests/Binding/RegistryStaticChecksTests.cs
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// Статичні перевірки функцій довідників при публікації — перевірки 15–19 і
/// попередження 21а (`02b` §12, FEATURE-REGISTRY-TABLES §5.5, крок RT-21).
/// </summary>
/// <remarks>
/// ⚠ Перевірку 18 (<c>ROW.</c>/<c>THIS</c> поза областю) робить парсер —
/// <c>RowReferenceParsingTests</c> (RT-07); тут її немає навмисно.
/// Одиниці (перевірка 20) — <c>RegistryUnitChecksTests</c>.
/// </remarks>
public sealed class RegistryStaticChecksTests
{
    private static readonly TypeChecker Checker = new();

    // ——— Д-5 і перевірка 16: шлях поля ———

    /// <remarks>
    /// ⛔ <c>Д-5</c>: до RT-21 описка в коді поля <c>REGFIELD</c> проходила
    /// публікацію й виявлялася лише <c>#REF</c> у прогоні.
    /// Мутація: прибрати звіт <c>expr.registryFieldUnknown</c> у
    /// <c>ReferenceResolver.ResolveRegistryPath</c> — тест червоний.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("REGFIELD(REGFIND('STREAM_CASE', [Stream], [CaseName]), 'T_CC')", ExpressionDialect.Template)]
    [InlineData("REGFIELD(REGFIND('STREAM_CASE', @Stream, @HmbCase), 'T_CC')", ExpressionDialect.Methodology)]
    public void Описка_в_полі_REGFIELD_помилка_публікації(string text, ExpressionDialect dialect)
    {
        var (_, diagnostics, _) = Check(text, Context(), dialect);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("ECR-TMPL-4222", diagnostic.Code);
        Assert.Equal("expr.registryFieldUnknown", diagnostic.MessageKey);
        Assert.Equal("STREAM_CASE", diagnostic.MessageParams!["registry"]);
        Assert.Equal("T_CC", diagnostic.MessageParams["field"]);

        // Позиція — сам код поля всередині літерала, а не весь виклик.
        Assert.Equal(text.IndexOf("T_CC", StringComparison.Ordinal), diagnostic.Position);
        Assert.Equal(4, diagnostic.Length);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Правильне_поле_REGFIELD_дає_тип_поля_без_зауважень()
    {
        var (type, diagnostics, _) = Check("REGFIELD(REGFIND('STREAM_CASE', [Stream], [CaseName]), 'T_C') * 2", Context());

        Assert.Empty(diagnostics);
        Assert.Equal(ExpressionValueType.Number, type);

        // Дата — дата, а не «щось»: REGFIELD більше не Null, коли поле відоме.
        var (date, dateDiagnostics, _) = Check("REGFIELD(REGFIND('STREAM_CASE', [Stream], [CaseName]), 'VALID_FROM')", Context());
        Assert.Empty(dateDiagnostics);
        Assert.Equal(ExpressionValueType.Date, date);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Шлях_REGFIELD_через_Lookup_проходить_а_через_число_ні()
    {
        const string good = "REGFIELD(REGFIND('STREAM_CASE', [Stream], [CaseName]), 'STREAM.NAME')";
        var (type, diagnostics, _) = Check(good, Context());
        Assert.Empty(diagnostics);
        Assert.Equal(ExpressionValueType.Text, type);

        const string bad = "REGFIELD(REGFIND('COMPONENT', 'H2S'), 'MW.X')";
        var (_, badDiagnostics, _) = Check(bad, Context());

        var diagnostic = Assert.Single(badDiagnostics);
        Assert.Equal("expr.registryFieldNotLookup", diagnostic.MessageKey);
        Assert.Equal("MW", diagnostic.MessageParams!["field"]);
        Assert.Equal(bad.IndexOf("MW.X", StringComparison.Ordinal), diagnostic.Position);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Описка_в_середині_шляху_ROW_називає_довідник_сегмента()
    {
        const string text = "REGSUM('GAS_COMPOSITION', ROW.CASE = REGFIND('STREAM_CASE', [Stream], [CaseName]), ROW.MOL_PCT * ROW.COMPONENT.MWW)";
        var (_, diagnostics, _) = Check(text, Context());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("expr.registryFieldUnknown", diagnostic.MessageKey);
        Assert.Equal("COMPONENT", diagnostic.MessageParams!["registry"]);
        Assert.Equal("MWW", diagnostic.MessageParams["field"]);
        Assert.Equal(text.IndexOf("ROW.COMPONENT.MWW", StringComparison.Ordinal), diagnostic.Position);
        Assert.Equal("ROW.COMPONENT.MWW".Length, diagnostic.Length);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void ROW_через_не_Lookup_поле_відхиляється()
    {
        const string text = "REGCOUNT('GAS_COMPOSITION', ROW.MOL_PCT.X > 0)";
        var (_, diagnostics, _) = Check(text, Context());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("expr.registryFieldNotLookup", diagnostic.MessageKey);
        Assert.Equal("GAS_COMPOSITION", diagnostic.MessageParams!["registry"]);
        Assert.Equal("MOL_PCT", diagnostic.MessageParams["field"]);
        Assert.Equal(text.IndexOf("ROW.", StringComparison.Ordinal), diagnostic.Position);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Формула_μ_форми_301_проходить_без_зауважень()
    {
        // FEATURE-REGISTRY-TABLES §5.6, та сама μ у формулі шаблону.
        var (type, diagnostics, _) = Check(
            "REGSUM('GAS_COMPOSITION', ROW.CASE = REGFIND('STREAM_CASE', [Stream], [CaseName]), "
            + "ROW.MOL_PCT * ROW.COMPONENT.MW) / 100",
            Context());

        Assert.Empty(diagnostics);
        Assert.Equal(ExpressionValueType.Number, type);
    }

    // ——— Перевірка 15: довідник ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Невідомий_довідник_з_позицією_літерала_і_без_каскаду()
    {
        const string text = "REGSUM('GAS_COMPOSITON', ROW.MOL_PCT > 0, ROW.MOL_PCT)";
        var (_, diagnostics, _) = Check(text, Context());

        // Одне зауваження: поля ROW.* невідомого довідника окремо не лаються.
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("ECR-TMPL-4222", diagnostic.Code);
        Assert.Equal("expr.registryUnknown", diagnostic.MessageKey);
        Assert.Equal("GAS_COMPOSITON", diagnostic.MessageParams!["registry"]);
        Assert.Equal(text.IndexOf('\''), diagnostic.Position);
        Assert.Equal("'GAS_COMPOSITON'".Length, diagnostic.Length);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("REGCOUNT([Code], ROW.MOL_PCT > 0)", "[Code]")]
    [InlineData("REGFIND([Code], 'H2S')", "[Code]")]
    [InlineData("REGONE('GAS_' & 'COMPOSITION', ROW.MOL_PCT > 0)", "'GAS_'")]
    public void Код_довідника_не_літерал_відхиляється(string text, string argument)
    {
        var context = Context();
        context.ColumnTypes["Code"] = ExpressionValueType.Text;

        var (_, diagnostics, _) = Check(text, context);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("expr.registryCodeMustBeLiteral", diagnostic.MessageKey);
        Assert.Equal(text.IndexOf(argument, StringComparison.Ordinal), diagnostic.Position);
    }

    // ——— Перевірка 17: ключ REGFIND ———

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("REGFIND('STREAM_CASE', [Stream])", "STREAM_CASE", "2", "1")]
    [InlineData("REGFIND('STREAM_CASE', [Stream], [CaseName], 'x')", "STREAM_CASE", "2", "3")]
    [InlineData("REGFIND('COMPONENT', 'H2S', 'x')", "COMPONENT", "1", "2")]
    public void Кількість_частин_ключа_не_збігається_з_первинним(
        string text, string registry, string expected, string actual)
    {
        var (_, diagnostics, _) = Check(text, Context());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("ECR-TMPL-0422", diagnostic.Code);
        Assert.Equal("expr.registryKeyArity", diagnostic.MessageKey);
        Assert.Equal(registry, diagnostic.MessageParams!["registry"]);
        Assert.Equal(expected, diagnostic.MessageParams["expected"]);
        Assert.Equal(actual, diagnostic.MessageParams["actual"]);
        Assert.Equal(0, diagnostic.Position);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Тип_частини_ключа_звіряється_з_полем()
    {
        // Переставлені частини: текст на місці Lookup і запис на місці тексту.
        const string swapped = "REGFIND('STREAM_CASE', [CaseName], [Stream])";
        var (_, diagnostics, _) = Check(swapped, Context());

        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, d => Assert.Equal("expr.registryKeyPartType", d.MessageKey));
        Assert.All(diagnostics, d => Assert.Equal("ECR-TMPL-0422", d.Code));
        Assert.Equal(swapped.IndexOf("[CaseName]", StringComparison.Ordinal), diagnostics[0].Position);
        Assert.Equal("EntryRef<STREAM>", diagnostics[0].MessageParams!["expected"]);
        Assert.Equal("Text", diagnostics[0].MessageParams!["actual"]);
        Assert.Equal(swapped.IndexOf("[Stream]", StringComparison.Ordinal), diagnostics[1].Position);

        // Запис ІНШОГО довідника на місці Lookup — теж не той тип.
        var (_, foreign, _) = Check("REGFIND('STREAM_CASE', REGFIND('COMPONENT', 'H2S'), [CaseName])", Context());
        Assert.Equal("EntryRef<COMPONENT>", Assert.Single(foreign).MessageParams!["actual"]);

        // Без первинного ключа — пошук за кодом, одна текстова частина.
        var (_, byCode, _) = Check("REGFIND('COMPONENT', 5)", Context());
        Assert.Equal("Text", Assert.Single(byCode).MessageParams!["expected"]);
    }

    // ——— Перевірка 19: EntryRef ———

    /// <remarks>
    /// Мутації (перевірено): прибрати гілку <c>ExpressionValueType.Number</c> у
    /// <c>TypeChecker.CompareEntries</c> — рядок <c>= 1</c> червоний; прибрати
    /// гілку <c>left.IsEntry || right.IsEntry</c> у <c>Add</c>/<c>Subtract</c> —
    /// рядок <c>[Start] + …</c> червоний (дата плюс число пройшла б як дата).
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("[Start] + REGFIND('COMPONENT', 'H2S')")]
    [InlineData("REGFIND('COMPONENT', 'H2S') + 1")]
    [InlineData("REGFIND('COMPONENT', 'H2S') * 2")]
    [InlineData("-REGFIND('COMPONENT', 'H2S')")]
    [InlineData("REGFIND('COMPONENT', 'H2S') = 1")]
    [InlineData("REGFIND('COMPONENT', 'H2S') > REGFIND('COMPONENT', 'CO2')")]
    [InlineData("REGFIND('COMPONENT', 'H2S') = REGFIND('STREAM', '1D-2')")]
    [InlineData("SUM(REGFIND('COMPONENT', 'H2S'), 1)")]
    [InlineData("REGFIELD(REGFIND('STREAM_CASE', [Stream], [CaseName]), 'STREAM') * 2")]
    [InlineData("REGSUM('GAS_COMPOSITION', ROW.CASE = [Stream], ROW.MOL_PCT)")]
    public void EntryRef_в_арифметиці_й_порівнянні_з_числом_відхиляється(string text)
    {
        var (_, diagnostics, _) = Check(text, Context());

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("ECR-TMPL-4222", diagnostic.Code);
        Assert.Equal("expr.entryRefMisuse", diagnostic.MessageKey);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("REGFIND('COMPONENT', 'H2S') = REGFIND('COMPONENT', 'CO2')")]
    [InlineData("REGFIND('COMPONENT', 'H2S') <> REGFIND('component', 'CO2')")]
    [InlineData("REGSUM('GAS_COMPOSITION', ROW.COMPONENT = REGFIND('COMPONENT', 'H2S'), ROW.MOL_PCT)")]
    [InlineData("REGFIND('COMPONENT', 'H2S') = NULL")]
    [InlineData("IF(TRUE, REGFIND('COMPONENT', 'H2S'), REGFIND('COMPONENT', 'CO2')) = REGFIND('COMPONENT', 'N2')")]
    public void EntryRef_з_EntryRef_того_самого_довідника_порівнюється(string text)
    {
        var (_, diagnostics, _) = Check(text, Context());

        Assert.Empty(diagnostics);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void EntryRef_у_методології_Abs_відхиляється()
    {
        var context = Context();

        var (_, diagnostics, _) = Check("Abs(REGFIND('COMPONENT', 'H2S'))", context, ExpressionDialect.Methodology);
        Assert.Equal("expr.entryRefMisuse", Assert.Single(diagnostics).MessageKey);

        // `@Stream` з Lookup-колонки — теж EntryRef (лише Strict, §5.3).
        var (_, argument, _) = Check("@Stream + 1", context, ExpressionDialect.Methodology);
        Assert.Equal("STREAM", Assert.Single(argument).MessageParams!["registry"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Без_форм_довідників_EntryRef_і_літерал_коду_однаково_перевіряються()
    {
        // Перевіркам 15 (літерал) і 19 форма не потрібна: код видно в тексті.
        var bare = new TestBindingContext();

        var (_, misuse, _) = Check("REGFIND('ANY', 'x') * 2", bare);
        Assert.Equal("expr.entryRefMisuse", Assert.Single(misuse).MessageKey);

        // А поля й ключі без форм не перевіряються зовсім — жодних вигаданих відмов.
        var (_, fields, _) = Check("REGSUM('ANY', ROW.X > 0, ROW.Y) + REGFIELD(REGFIND('ANY', 1, 2), 'Z')", bare);
        Assert.Empty(fields);
    }

    // ——— Типи результатів ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void REGMIN_і_REGMAX_мають_тип_виразу()
    {
        Assert.Equal(ExpressionValueType.Date,
            Check("REGMAX('STREAM_CASE', TRUE, ROW.VALID_FROM)", Context()).Type);
        Assert.Equal(ExpressionValueType.Number,
            Check("REGMIN('GAS_COMPOSITION', TRUE, ROW.MOL_PCT)", Context()).Type);

        // Дата з REGMAX у порівнянні з числом — звичайна помилка типів.
        var (_, diagnostics, _) = Check("REGMAX('STREAM_CASE', TRUE, ROW.VALID_FROM) > 1", Context());
        Assert.Equal("expr.type.incomparable", Assert.Single(diagnostics).MessageKey);

        var (_, entry, _) = Check("REGMAX('GAS_COMPOSITION', TRUE, ROW.COMPONENT)", Context());
        Assert.Equal("expr.entryRefMisuse", Assert.Single(entry).MessageKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Фільтр_агрегата_має_бути_умовою()
    {
        var (_, diagnostics, _) = Check("REGSUM('GAS_COMPOSITION', ROW.MOL_PCT, ROW.MOL_PCT)", Context());

        Assert.Equal("expr.type.conditionNeedsBoolean", Assert.Single(diagnostics).MessageKey);
    }

    // ——— Правило довідника: THIS і ROW верхнього рівня ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Правило_довідника_бачить_THIS_і_власні_поля()
    {
        var context = Context();
        context.RuleRegistryCode = "STREAM_CASE";

        // §5.6: Σ складу кейсу — 100 %.
        var (_, sum, _) = CheckRule("ABS(REGSUM('GAS_COMPOSITION', ROW.CASE = THIS, ROW.MOL_PCT) - 100) <= 0.5", context);
        Assert.Empty(sum);

        const string typo = "ROW.T_CX > 0";
        var (_, field, _) = CheckRule(typo, context);
        var diagnostic = Assert.Single(field);
        Assert.Equal("expr.registryFieldUnknown", diagnostic.MessageKey);
        Assert.Equal("STREAM_CASE", diagnostic.MessageParams!["registry"]);

        // Правило ІНШОГО довідника: THIS — запис COMPONENT, а не кейс.
        context.RuleRegistryCode = "COMPONENT";
        var (_, foreign, _) = CheckRule("REGCOUNT('GAS_COMPOSITION', ROW.CASE = THIS) > 0", context);
        Assert.Equal("expr.entryRefMisuse", Assert.Single(foreign).MessageKey);
    }

    // ——— 21а: попередження про повний перегляд ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Фільтр_без_індексного_шляху_лише_попередження()
    {
        const string text = "REGSUM('GAS_COMPOSITION', ROW.MOL_PCT > 0, ROW.MOL_PCT)";
        var (_, diagnostics, warnings) = Check(text, Context());

        Assert.Empty(diagnostics);
        var warning = Assert.Single(warnings);
        Assert.Equal("expr.registryScanUnindexed", warning.MessageKey);
        Assert.Equal(text.IndexOf("ROW.", StringComparison.Ordinal), warning.Position);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("REGSUM('GAS_COMPOSITION', ROW.CASE = REGFIND('STREAM_CASE', [Stream], [CaseName]), ROW.MOL_PCT)")]
    [InlineData("REGSUM('GAS_COMPOSITION', ROW.MOL_PCT > 0 && REGFIND('COMPONENT', 'H2S') = ROW.COMPONENT, ROW.MOL_PCT)")]
    [InlineData("REGCOUNT('GAS_COMPOSITION', ROW.COMPONENT = REGONE('COMPONENT', ROW.N_S > 0))")]
    public void Фільтр_з_індексним_шляхом_без_попередження(string text)
    {
        var (_, diagnostics, warnings) = Check(text, Context());

        Assert.Empty(diagnostics);
        // REGONE усередині фільтра — не агрегат: йому попередження не належить.
        Assert.Empty(warnings);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Рівність_через_шлях_ROW_не_індексна()
    {
        // `ROW.CASE.STREAM = …` — два сегменти: індекс знімка тримає лише пряме
        // Lookup-поле рядка (RegistryForms.Rows), тож рантайм перебирає все.
        var (_, _, warnings) = Check(
            "REGSUM('GAS_COMPOSITION', ROW.CASE.STREAM = [Stream], ROW.MOL_PCT)", Context());

        Assert.Equal("expr.registryScanUnindexed", Assert.Single(warnings).MessageKey);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static TestBindingContext Context()
    {
        var context = new TestBindingContext { Registries = InMemoryRegistryShapes.Hse301() };
        context.ColumnRegistries["Stream"] = "STREAM";
        context.ColumnTypes["Stream"] = ExpressionValueType.Text;
        context.ColumnTypes["CaseName"] = ExpressionValueType.Text;
        context.ColumnTypes["Start"] = ExpressionValueType.Date;
        context.ArgumentRegistries["Stream"] = "STREAM";
        context.ArgumentTypes["HmbCase"] = ExpressionValueType.Text;
        return context;
    }

    private static (ExpressionValueType Type, List<ExpressionDiagnostic> Diagnostics, List<ExpressionDiagnostic> Warnings)
        Check(string text, TestBindingContext context, ExpressionDialect dialect = ExpressionDialect.Template)
        => Run(Expr.Parse(text, dialect), context);

    private static (ExpressionValueType Type, List<ExpressionDiagnostic> Diagnostics, List<ExpressionDiagnostic> Warnings)
        CheckRule(string text, TestBindingContext context)
        => Run(new Parser().Parse(text, ExpressionDialect.Template, ExpressionParseMode.Editor, ExpressionHost.RegistryRule), context);

    private static (ExpressionValueType Type, List<ExpressionDiagnostic> Diagnostics, List<ExpressionDiagnostic> Warnings)
        Run(ParseResult parsed, TestBindingContext context)
    {
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var diagnostics = new List<ExpressionDiagnostic>();
        var warnings = new List<ExpressionDiagnostic>();
        var type = Checker.Check(parsed.Expression!.Root, context, diagnostics, warnings);
        return (type, diagnostics, warnings);
    }
}
