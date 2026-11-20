using Ecr.Application.Calculations.Scripting;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Scripting;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations.Scripting;

/// <summary>
/// Перевірка одиниць скрипта (MS 13.4), доповнення: <c>row.X</c> бере одиницю колонки живих таблиць; тіла власних
/// функцій перевіряються з параметрами невідомої одиниці.
/// </summary>
public sealed class ScriptUnitCheckerTests
{
    private static LiveColumn WithUnit(int id, string code, int? unit)
        => new(id, code, CellDataType.Decimal, false, null, unit);

    private static LiveTable Table(string code, int tableId, params LiveColumn[] columns)
        => ScriptTestKit.Table(code, columns, tableId, template: "TPL" + tableId, templateId: tableId);

    private static IReadOnlyList<ScriptDiagnostic> Check(string text, params LiveTable[] tables)
    {
        var compilation = ScriptTestKit.Compile(text);
        Assert.NotNull(compilation.Script);
        Assert.Empty(compilation.Diagnostics);
        return ScriptUnitChecker.Check(compilation.Script, ScriptTestKit.Units(), tables);
    }

    private static bool Blocks(IReadOnlyList<ScriptDiagnostic> diagnostics)
        => diagnostics.Any(d => d.Code == ScriptDiagnosticCodes.UnitMismatch);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public void Поле_рядка_бере_одиницю_колонки_живої_таблиці()
    {
        var table = Table("T1", 1, WithUnit(1, "Mass", ScriptTestKit.Kg));

        // кг + т без CONVERT: розбіжність видно лише тому, що row.Mass має одиницю колонки.
        Assert.True(Blocks(Check("rows from T1;\nconst b : t = 2;\nvar x = row.Mass + b;\noutput O : t = 1;", table)));

        // Оголошена інша розмірність, ніж у колонки.
        Assert.True(Blocks(Check("rows from T1;\nvar x : m3 = row.Mass;\noutput O : t = 1;", table)));

        // Та сама одиниця й та сама розмірність не блокують.
        Assert.False(Blocks(Check("rows from T1;\nconst b : kg = 2;\nvar x = row.Mass + b;\noutput O : t = 1;", table)));
        Assert.False(Blocks(Check("rows from T1;\nvar x : t = row.Mass;\noutput O : t = 1;", table)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public void Поле_рядка_невідоме_коли_таблиці_розходяться_або_в_колонці_немає_одиниці()
    {
        const string text = "rows from A;\nrows from B;\nconst b : t = 2;\nvar x = row.Mass + b;\noutput O : t = 1;";

        // Таблиці розходяться (кг проти м3) — «невідомо», відмови немає.
        Assert.False(Blocks(Check(
            text, Table("A", 1, WithUnit(1, "Mass", ScriptTestKit.Kg)), Table("B", 2, WithUnit(2, "Mass", ScriptTestKit.CubicMetre)))));

        // В одній з таблиць колонка без одиниці — теж невідомо.
        Assert.False(Blocks(Check(
            text, Table("A", 1, WithUnit(1, "Mass", ScriptTestKit.Kg)), Table("B", 2, WithUnit(2, "Mass", null)))));

        // Одна колонка без одиниці — невідомо, а не безрозмірне.
        Assert.False(Blocks(Check(
            "rows from A;\nconst b : t = 2;\nvar x = row.Mass + b;\noutput O : t = 1;", Table("A", 1, WithUnit(1, "Mass", null)))));

        // Обидві таблиці згодні (кг й кг) — одиниця відома.
        Assert.True(Blocks(Check(
            text, Table("A", 1, WithUnit(1, "Mass", ScriptTestKit.Kg)), Table("B", 2, WithUnit(2, "Mass", ScriptTestKit.Kg)))));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public void Без_живих_таблиць_поле_рядка_лишається_невідомим_як_було()
    {
        var compilation = ScriptTestKit.Compile("rows from T1;\nconst b : t = 2;\nvar x = row.Mass + b;\noutput O : t = 1;");
        Assert.NotNull(compilation.Script);

        Assert.Empty(ScriptUnitChecker.Check(compilation.Script, ScriptTestKit.Units()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public void Тіло_власної_функції_перевіряється_з_параметрами_невідомої_одиниці()
    {
        // Константи в тілі розходяться — це видно, не знаючи параметрів.
        var bad = Check(
            "rows from T1;\nconst a : kg = 1;\nconst b : m3 = 2;\nfunction f(p) = p * 2 + a + b;\nvar x = f(1);\noutput O : t = x;");
        var diagnostic = Assert.Single(bad, d => d.Code == ScriptDiagnosticCodes.UnitMismatch);
        Assert.Equal("f", diagnostic.Params["name"]);
        Assert.Equal(4, diagnostic.Line);

        // Параметр невідомий: він не «безрозмірний», тож сума з розмірною константою не блокує.
        Assert.False(Blocks(Check(
            "rows from T1;\nconst k : kg = 1;\nfunction f(p) = p + k;\nvar x = f(1);\noutput O : t = x;")));
    }

    // ---- безрозмірні одиниці (D-556, репетиція кола 4): `one` не є «розмірною величиною» ----

    private const int One = 7;

    private static UnitCatalogSnapshot UnitsWithOne()
        => new(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
            {
                ["kg"] = new(ScriptTestKit.Kg, "kg", 1, DimensionCode: "Mass"),
                ["t"] = new(ScriptTestKit.Tonne, "t", 1, DimensionCode: "Mass"),
                ["m3"] = new(ScriptTestKit.CubicMetre, "m3", 2, DimensionCode: "Volume"),
                ["one"] = new(One, "one", 7, DimensionCode: ScriptUnitChecker.DimensionlessDimensionCode),
            },
            new Dictionary<string, int>(StringComparer.Ordinal));

    private static IReadOnlyList<ScriptDiagnostic> CheckWithOne(string text, params LiveTable[] tables)
    {
        var compilation = ScriptTestKit.Compile(text);
        Assert.NotNull(compilation.Script);
        Assert.Empty(compilation.Diagnostics);
        return ScriptUnitChecker.Check(compilation.Script, UnitsWithOne(), tables);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public void Константи_одиниці_one_не_змінюють_розмірність_добутку()
    {
        var table = Table("T1", 1, WithUnit(1, "Mass", ScriptTestKit.Tonne));

        // Непарна кількість безрозмірних множників (t x one x one): раніше «t x one» давало «добуток без одиниці»,
        // а наступний множник підміняв одиницю на `one` -> «інша розмірність, ніж t».
        Assert.False(Blocks(CheckWithOne(
            "rows from T1;\nconst a : one = 2;\nconst b : one = 3;\noutput O : t = row.Mass * a * b;", table)));
        Assert.False(Blocks(CheckWithOne(
            "rows from T1;\nconst a : one = 2;\nconst b : one = 3;\nconst c : one = 4;\noutput O : t = a * row.Mass * b * c;", table)));

        // Не послаблено: інша розмірність виходу блокує й з безрозмірними множниками.
        Assert.True(Blocks(CheckWithOne(
            "rows from T1;\nconst a : one = 2;\noutput O : m3 = row.Mass * a;", table)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public void Колонка_з_одиницею_one_безрозмірна_як_літерал()
    {
        var table = Table(
            "T1", 1, WithUnit(1, "Mass", ScriptTestKit.Tonne), WithUnit(2, "Frac", One), WithUnit(3, "Frac2", One));

        Assert.False(Blocks(CheckWithOne(
            "rows from T1;\noutput O : t = row.Mass * row.Frac * row.Frac2;", table)));
        Assert.False(Blocks(CheckWithOne(
            "rows from T1;\nvar f : one = row.Frac * row.Frac2;\noutput O : t = row.Mass * f;", table)));

        // Розмірність виходу й далі перевіряється.
        Assert.True(Blocks(CheckWithOne(
            "rows from T1;\noutput O : m3 = row.Mass * row.Frac;", table)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public void Оголошена_безрозмірна_змінна_не_ховає_розбіжність_виходу()
    {
        // F13 (мутація: прибрати `!dimensionless` з VarInfo): змінна `one` із виразу без відомої одиниці
        // (колонка без одиниці) лишалася б «невідомою» і глушила перевірку множення - m3 = t * one не блокувалося.
        var table = Table("T1", 1, WithUnit(1, "Mass", ScriptTestKit.Tonne), WithUnit(2, "Raw", null));

        Assert.True(Blocks(CheckWithOne(
            "rows from T1;\nvar f : one = row.Raw;\noutput O : m3 = row.Mass * f;", table)));
        Assert.False(Blocks(CheckWithOne(
            "rows from T1;\nvar f : one = row.Raw;\noutput O : t = row.Mass * f;", table)));
    }
}
