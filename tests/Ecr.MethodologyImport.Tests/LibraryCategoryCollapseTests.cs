using Ecr.MethodologyImport.Export;
using Xunit;

namespace Ecr.MethodologyImport.Tests;

/// <summary>
/// Згортання однакових значень категорій у бібліотеці: СИНТЕТИЧНІ константи (вигадані коди й числа), без БД.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні точки: прибери перевірку <c>isLibrary</c> — червоніє <see cref="Методологія_не_бібліотека_лишає_усі_категорії"/>;
/// заміни <c>items.All(...)</c> на <c>items.Any(...)</c> — червоніють <see cref="Різні_значення_лишаються_усі"/> і
/// <see cref="Інші_дати_не_згортаються_разом"/>; прибери вимогу спільної категорії — червоніє
/// <see cref="Без_спільної_категорії_нічого_не_згортається"/>.
/// </remarks>
public sealed class LibraryCategoryCollapseTests
{
    private static PackageConstantValue V(string category, string value, string start = "2024-01-01", string end = "9999-01-01", string version = "V1")
        => new(category, version, value, start, end);

    private static IReadOnlyList<PackageConstantValue> Collapse(bool isLibrary, params PackageConstantValue[] values)
        => MethodologyPackage.CollapseRedundantCategories(values, "Common", isLibrary);

    [Fact]
    public void Однакові_значення_шести_категорій_зводяться_до_спільної()
    {
        var result = Collapse(
            true,
            V("Flare A", "12"), V("Common", "12"), V("Flare B", "12"), V("Flare C", "12"), V("Flare D", "12"), V("Flare E", "12"));

        var only = Assert.Single(result);
        Assert.Equal("Common", only.Category);
        Assert.Equal("12", only.Value);
    }

    [Fact]
    public void Різні_значення_лишаються_усі()
    {
        var input = new[] { V("Flare A", "12"), V("Common", "12"), V("Flare B", "13") };

        var result = Collapse(true, input);

        Assert.Equal(input, result);
    }

    [Fact]
    public void Без_спільної_категорії_нічого_не_згортається()
    {
        var input = new[] { V("Flare A", "12"), V("Flare B", "12") };

        Assert.Equal(input, Collapse(true, input));
    }

    [Fact]
    public void Методологія_не_бібліотека_лишає_усі_категорії()
    {
        var input = new[] { V("Common", "12"), V("NAE 1", "12") };

        Assert.Equal(input, Collapse(false, input));
    }

    [Fact]
    public void Інші_дати_не_згортаються_разом()
    {
        var result = Collapse(
            true,
            V("Common", "12", "2024-01-01", "2025-01-01"),
            V("Flare A", "12", "2024-01-01", "2025-01-01"),
            V("Common", "14", "2025-01-02"),
            V("Flare A", "15", "2025-01-02"));

        // Перший проміжок: однакові — лишається спільна; другий: різні — лишаються обидві.
        Assert.Equal(3, result.Count);
        Assert.Single(result, v => v.Category == "Flare A" && v.Value == "15");
        Assert.Single(result, v => v.Category == "Common" && v.Value == "14");
        Assert.Single(result, v => v.Category == "Common" && v.Value == "12");
        Assert.DoesNotContain(result, v => v.Category == "Flare A" && v.Value == "12");
    }

    [Fact]
    public void Одне_значення_не_змінюється()
    {
        var input = new[] { V("Common", "1") };

        Assert.Same(input[0], Collapse(true, input)[0]);
    }

    [Fact]
    public void Пакет_бібліотеки_несе_одне_значення_а_методологія_усі()
    {
        var builder = new AfXmlBuilder()
            .Formula("Common", "V1", "F1", "CST.k", "CST.k")
            .Constant("Common", "V1", "k", "12", "Common")
            .Constant("Common", "V1", "k", "12", "Flare A")
            .Constant("Common", "V1", "k", "12", "Flare B")
            .Formula("M", "V1", "G1", "CST.k", "CST.k")
            .Constant("M", "V1", "k", "12", "Common")
            .Constant("M", "V1", "k", "12", "Flare A");
        var (model, report) = AnalyzeCommand.Run(AfXmlBuilder.ToStream(builder.Build()));

        var package = MethodologyPackage.From(model, report, "Common");

        var lib = package.Methodologies.Single(m => m.Name == "Common").Versions.Single().Constants.Single();
        Assert.Equal(["Common"], lib.Values.Select(v => v.Category).ToArray());
        var other = package.Methodologies.Single(m => m.Name == "M").Versions.Single().Constants.Single();
        Assert.Equal(2, other.Values.Count);
    }
}
