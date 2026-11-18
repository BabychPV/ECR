using System.Globalization;
using Ecr.Domain.Enums;
using Ecr.MethodologyScriptMigration.Conversion;
using Xunit;

namespace Ecr.MethodologyScriptMigration.Tests;

/// <summary>
/// Вірність перетворювача старому рушію там, де вона залежить не від виразу, а від ІНШИХ сутностей версії:
/// необов'язкові аргументи (<c>ArgumentsCsv</c>), «відомі категорії» (категорії КОНСТАНТ, а не таблиця категорій),
/// непридатні константи. Раніше такі розбіжності перетворювач пропускав мовчки.
/// </summary>
public sealed class ConverterFidelityTests
{
    private const MethodologyFormulaScope Row = MethodologyFormulaScope.Row;

    private static SourceFormula F(string code, string expr, int order, string? csv, MethodologyFormulaScope scope = Row)
        => new(code, expr, order, scope, false, FormulaResultType.Number, null, csv);

    // ---- необов'язкові аргументи ----

    [Fact]
    public void Необовязковість_береться_зі_списків_усіх_формул_версії_а_не_з_маркера_у_виразі()
    {
        // Рантайм (OptionalArguments.Of) об'єднує маркери ArgumentsCsv усіх формул: A необов'язковий і в B, де маркера немає.
        var source = Sup.Version(
            [
                F("FirstOne", "@A? + 1", 1, "@A?"),
                F("SecondOne", "@A * 2", 2, "@A"),
            ]);

        var text = Sup.ConvertOk(source).Text!;

        Assert.Contains("var FirstOne = row.A? + 1;", text);
        Assert.Contains("var SecondOne = row.A? * 2;", text);
    }

    [Fact]
    public void Маркер_лише_у_виразі_при_збереженому_списку_без_маркера_не_робить_аргумент_необовязковим()
    {
        var source = Sup.Version([F("OnlyOne", "@A? + 1", 1, "@A")]);

        var result = Sup.ConvertOk(source);

        Assert.Contains("var OnlyOne = row.A + 1;", result.Text);
        Assert.True(Sup.HasIssue(result, "optionalMarkerIgnored", IssueSeverity.Warning));
    }

    [Fact]
    public void Формула_без_збереженого_списку_дає_маркери_зі_свого_виразу()
    {
        var source = Sup.Version([F("OnlyOne", "@A? + 1", 1, null)]);

        Assert.Contains("var OnlyOne = row.A? + 1;", Sup.ConvertOk(source).Text);
    }

    [Fact]
    public void Необовязковість_через_бібліотеку_діє_на_імпортера_і_лишає_Info()
    {
        var source = Sup.Version(
            [F("Own", "@A + 1", 1, "@A")],
            imports: [new SourceImport("Lib", ["Dens"], [], ["A"])]);

        var result = Sup.ConvertOk(source);

        Assert.Contains("var Own = row.A? + 1;", result.Text);
        Assert.True(Sup.HasIssue(result, "libraryOptionalArgument", IssueSeverity.Info));
    }

    [Fact]
    public void Правило_категорії_теж_читає_необовязковість_версії()
    {
        var source = Sup.Version(
            [F("K", "CST.K", 1, "@Fuel?")],
            [SourceConstant.OfNumber("K", 1, "X")],
            categoryRule: "@Fuel",
            categories: ["X"]);

        Assert.Contains("var Category = key(row.Fuel?);", Sup.ConvertOk(source).Text);
    }

    // ---- відомі категорії ----

    [Fact]
    public void Відомі_категорії_це_категорії_констант_а_не_таблиця_категорій()
    {
        // Таблиця категорій має «C», констант з нею немає: старий рушій (KnownCategoriesOf) такий ключ відхиляє.
        var source = Sup.Version(
            [Sup.F("A", "CST.K", 1)],
            [SourceConstant.OfNumber("K", 1, "X"), SourceConstant.OfNumber("K", 2, "Y")],
            categoryRule: "@Fuel",
            categories: ["X", "Y", "C"]);

        var text = Sup.ConvertOk(source).Text!;

        Assert.Contains("reject when Category == null || Category == \"\" || !(Category in (\"X\", \"Y\"));", text);
        Assert.DoesNotContain("\"C\"", text);
    }

    [Fact]
    public void Категорія_константи_якої_немає_в_таблиці_категорій_приймається()
    {
        var source = Sup.Version(
            [Sup.F("A", "CST.K", 1)],
            [SourceConstant.OfNumber("K", 1, "X"), SourceConstant.OfNumber("K", 2, "Z")],
            categoryRule: "@Fuel",
            categories: ["X"]);

        var text = Sup.ConvertOk(source).Text!;

        Assert.Contains("!(Category in (\"X\", \"Z\"))", text);
        Assert.Contains("Z: 2", text);
    }

    [Fact]
    public void Без_категорійних_констант_старий_рушій_ключ_не_перевіряє_і_скрипт_теж()
    {
        // Таблиця категорій є, констант за категорією немає: KnownCategoriesOf = null.
        var source = Sup.Version(
            [Sup.F("A", "CST.K", 1)],
            [SourceConstant.OfNumber("K", 1)],
            categoryRule: "@Fuel",
            categories: ["X", "Y"]);

        var text = Sup.ConvertOk(source).Text!;

        Assert.Contains("reject when Category == null || Category == \"\";", text);
        Assert.DoesNotContain(" in (", text);
    }

    [Fact]
    public void Категорія_Common_не_вважається_відомою()
    {
        var source = Sup.Version(
            [Sup.F("A", "CST.K", 1)],
            [SourceConstant.OfNumber("K", 1, "Common"), SourceConstant.OfNumber("K", 2, "X")],
            categoryRule: "@Fuel");

        var text = Sup.ConvertOk(source).Text!;

        Assert.Contains("!(Category in (\"X\"))", text);
        Assert.DoesNotContain("\"Common\"", text);
    }

    [Fact]
    public void Категорії_констант_бібліотеки_додаються_до_відомих()
    {
        var source = Sup.Version(
            [Sup.F("A", "CST.K", 1)],
            [SourceConstant.OfNumber("K", 1, "X")],
            categoryRule: "@Fuel",
            imports: [new SourceImport("Lib", ["Dens"], ["Common", "L"], [])]);

        var result = Sup.ConvertOk(source);

        Assert.Contains("!(Category in (\"L\", \"X\"))", result.Text);
        Assert.True(Sup.HasIssue(result, "libraryCategories", IssueSeverity.Info));
    }

    [Fact]
    public void Префікс_до_підкреслення_приймається_як_у_IsKnownCategory()
    {
        var source = Sup.Version(
            [Sup.F("A", "CST.K", 1)],
            [SourceConstant.OfNumber("K", 1, "6640_0301")],
            categoryRule: "@Fuel");

        Assert.Contains("!(Category in (\"6640_0301\", \"6640\"))", Sup.ConvertOk(source).Text);
    }

    [Fact]
    public void Пробіл_у_категорії_дає_Warning_і_обидва_написання()
    {
        var source = Sup.Version(
            [Sup.F("A", "CST.K", 1)],
            [SourceConstant.OfNumber("K", 1, "Over hauled")],
            categoryRule: "@Fuel");

        var result = Sup.ConvertOk(source);

        Assert.Contains("\"Over hauled\", \"Overhauled\"", result.Text);
        Assert.True(Sup.HasIssue(result, "categoryWhitespace", IssueSeverity.Warning));
    }

    // ---- непридатна константа ----

    [Fact]
    public void Константа_яку_не_можна_завантажити_зупиняє_перетворення_а_не_губиться()
    {
        var source = Sup.Version(
            [Sup.F("A", "CST.K", 1)],
            [new SourceConstant("K", ConstantKind.Text, null, string.Empty)]);

        var result = ScriptConverter.Convert(source);

        Assert.Null(result.Text);
        Assert.True(Sup.HasIssue(result, "constantInvalid", IssueSeverity.Error));
    }

    // ---- стабільність ----

    [Theory]
    [InlineData("uk-UA")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    public void Текст_і_відбиток_не_залежать_від_культури_потоку(string culture)
    {
        VersionSource Source() => Sup.Version(
            [Sup.F("A", "CST.K * 1.5 + CST.M + 0.001 - CST.Dated", 1)],
            [
                SourceConstant.OfNumber("K", 1.25m, "A", null, new DateOnly(2025, 3, 1)),
                SourceConstant.OfNumber("K", 2.5m, "B"),
                SourceConstant.OfNumber("M", 0.0010m),
                SourceConstant.OfNumber("Dated", 3.14159m, validTo: new DateOnly(2025, 6, 1)),
                SourceConstant.OfNumber("Dated", 2.71828m, validFrom: new DateOnly(2025, 6, 1)),
            ],
            categoryRule: "@F",
            categories: ["A", "B"]) with { EffectiveFrom = new DateOnly(2024, 1, 1), NextEffectiveFrom = new DateOnly(2026, 1, 1) };

        (string? Text, string Fingerprint) Run(string name)
        {
            var saved = CultureInfo.CurrentCulture;
            var savedUi = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(name);
            try
            {
                return (ScriptConverter.Convert(Source()).Text, Fingerprint.ComputeHex(Source()));
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
                CultureInfo.CurrentUICulture = savedUi;
            }
        }

        var reference = Run("en-US");

        Assert.NotNull(reference.Text);
        Assert.Equal(reference, Run(culture));
    }

    [Fact]
    public void Текст_не_залежить_від_порядку_констант_у_вході()
    {
        var constants = new[]
        {
            SourceConstant.OfNumber("K", 1, "B"), SourceConstant.OfNumber("K", 2, "A"), SourceConstant.OfNumber("M", 3, "A"),
        };
        var formulas = new[] { Sup.F("A", "CST.K + CST.M", 1) };

        var one = Sup.ConvertOk(Sup.Version(formulas, constants, categoryRule: "@F")).Text;
        var two = Sup.ConvertOk(Sup.Version(formulas, constants.Reverse(), categoryRule: "@F")).Text;

        Assert.Equal(one, two);
    }
}
