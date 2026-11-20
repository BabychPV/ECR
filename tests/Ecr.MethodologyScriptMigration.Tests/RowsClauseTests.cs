using Ecr.Domain.Enums;
using Ecr.MethodologyScriptMigration.Conversion;
using Ecr.Scripting;
using Xunit;

namespace Ecr.MethodologyScriptMigration.Tests;

public sealed class RowsClauseTests
{
    private static SourceRule Rule(params (string Column, SourceMatchValue Value)[] pairs)
        => new([.. pairs.Select(p => new SourceMatchPair(p.Column, p.Value))]);

    private static SourceMatchValue Text(string v) => SourceMatchValue.OfText(v);

    private static SourceMatchValue Entry(string r, string e) => SourceMatchValue.OfEntry(r, e);

    private static RowsClause CompileRows(string text)
    {
        var c = ScriptCompiler.Compile(text, new ScriptCompileOptions(NumericMode.Legacy, false));
        Assert.True(c.Diagnostics.Count == 0, string.Join("; ", c.Diagnostics.Select(d => d.Code + ": " + d.Message)) + "\n" + text);
        return Assert.Single(c.Script!.Rows);
    }

    [Fact]
    public void Одне_правило_з_двох_рівностей()
    {
        var text = RowsClauseWriter.Write(new SourceRowsClause(
            ["T3_00"], [Rule(("Land_Type", Text("Pump")), ("Land_Fuel", Entry("LAND_FUEL", "Gasoline")))], []));

        Assert.Equal(
            "rows from T3_00\n    where row.Land_Type == \"Pump\" && row.Land_Fuel == entry(\"LAND_FUEL\", \"Gasoline\");\n",
            text);
        var clause = CompileRows(text);
        Assert.IsType<MatchAnd>(clause.Where);
        Assert.Null(clause.Active);
    }

    [Fact]
    public void Кілька_правил_з_єднуються_або_у_дужках()
    {
        var text = RowsClauseWriter.Write(new SourceRowsClause(
            ["T1"],
            [Rule(("A", Text("x")), ("B", Text("y"))), Rule(("A", Text("z")))],
            []));

        Assert.Equal(
            "rows from T1\n    where (row.A == \"x\" && row.B == \"y\")\n       || (row.A == \"z\");\n",
            text);
        Assert.IsType<MatchOr>(CompileRows(text).Where);
    }

    [Fact]
    public void Значення_запис_довідника_пишеться_через_entry()
    {
        var text = RowsClauseWriter.Write(new SourceRowsClause(
            ["T1"], [Rule(("Land_Fuel", Entry("LAND_FUEL", "Gasoline")))], []));

        Assert.Contains("row.Land_Fuel == entry(\"LAND_FUEL\", \"Gasoline\")", text);
        var clause = CompileRows(text);
        var eq = Assert.IsType<MatchEquals>(clause.Where);
        var value = Assert.IsType<MatchValue.Entry>(eq.Value);
        Assert.Equal(("LAND_FUEL", "Gasoline"), (value.RegistryCode, value.EntryCode));
    }

    [Fact]
    public void Порожнє_правило_робить_відбір_без_where()
    {
        var text = RowsClauseWriter.Write(new SourceRowsClause(
            ["T1"], [Rule(("A", Text("x"))), Rule()], []));

        Assert.Equal("rows from T1;\n", text);
        Assert.Null(CompileRows(text).Where);
    }

    [Fact]
    public void Статус_колонки_дають_active_when_через_І()
    {
        var text = RowsClauseWriter.Write(new SourceRowsClause(
            ["T1"],
            [Rule()],
            [
                new SourceActiveColumn("Land_Status", "STATUS", "Running"),
                new SourceActiveColumn("Land_Flag", "FLAG", "On"),
            ]));

        Assert.Equal(
            "rows from T1\n    active when row.Land_Status == entry(\"STATUS\", \"Running\") && row.Land_Flag == entry(\"FLAG\", \"On\");\n",
            text);
        Assert.IsType<MatchAnd>(CompileRows(text).Active);
    }

    [Fact]
    public void Таблиця_без_статус_колонки_дає_окреме_речення_без_active()
    {
        var withStatus = new SourceRowsClause(
            ["T1"], [Rule(("A", Text("x")))], [new SourceActiveColumn("Status", "S", "Run")]);
        var withoutStatus = new SourceRowsClause(["T2"], [Rule(("A", Text("x")))], []);

        var source = Sup.Version([Sup.F("Calc", "@A", 1)]) with { RowsClauses = [withStatus, withoutStatus] };
        var result = Sup.ConvertOk(source);

        var script = Sup.Compile(result.Text!);
        Assert.Equal(2, script.Rows.Count);
        Assert.NotNull(script.Rows[0].Active);
        Assert.Null(script.Rows[1].Active);
        Assert.Contains("rows from T2\n    where row.A == \"x\";", result.Text);
    }

    [Fact]
    public void Кілька_таблиць_і_шаблонні_коди()
    {
        var text = RowsClauseWriter.Write(new SourceRowsClause(["T1", "Land.T2"], [Rule()], []));
        Assert.Equal("rows from T1, Land.T2;\n", text);
        Assert.Equal(2, CompileRows(text).Tables.Count);
    }

    [Fact]
    public void Лапки_у_текстовому_значенні_екрануються()
    {
        var text = RowsClauseWriter.Write(new SourceRowsClause(
            ["T1"], [Rule(("A", Text("He said \"x\" \\ done")))], []));

        var eq = Assert.IsType<MatchEquals>(CompileRows(text).Where);
        Assert.Equal("He said \"x\" \\ done", Assert.IsType<MatchValue.Text>(eq.Value).Value);
    }

    [Fact]
    public void Без_правил_без_таблиць_і_з_хибною_колонкою_виняток()
    {
        Assert.Throws<ConversionException>(() => RowsClauseWriter.Write(new SourceRowsClause(["T1"], [], [])));
        Assert.Throws<ConversionException>(() => RowsClauseWriter.Write(new SourceRowsClause([], [Rule()], [])));
        Assert.Throws<ConversionException>(
            () => RowsClauseWriter.Write(new SourceRowsClause(["T1"], [Rule(("bad-name", Text("x")))], [])));
    }
}
