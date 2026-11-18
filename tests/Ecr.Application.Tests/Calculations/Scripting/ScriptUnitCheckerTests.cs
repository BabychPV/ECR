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
}
