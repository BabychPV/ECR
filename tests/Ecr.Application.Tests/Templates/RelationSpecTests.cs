using Ecr.Application.Templates;
using Ecr.Domain.Enums;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>Схеми MatchJson/MapJson видів Rollup і Check (D-230; схеми — ПРИПУЩЕННЯ, див. RelationSpec.cs).</summary>
public sealed class RelationSpecTests
{
    private static readonly IReadOnlyDictionary<string, CellDataType> Source = new Dictionary<string, CellDataType>
    {
        ["Unit"] = CellDataType.String, ["Fact"] = CellDataType.Decimal, ["Qty"] = CellDataType.Int,
    };

    private static readonly IReadOnlyDictionary<string, CellDataType> Target = new Dictionary<string, CellDataType>
    {
        ["Unit"] = CellDataType.String, ["Total"] = CellDataType.Formula, ["Manual"] = CellDataType.Decimal, ["Name"] = CellDataType.String,
    };

    private const string Match = """{"keys":[{"source":"Unit","target":"Unit"}]}""";
    private const string Roll = """{"sourceColumn":"Fact","targetColumn":"Total","aggregate":"sum"}""";
    private const string Chk = """{"left":"Fact","right":"Total","tolerance":"0.5","toleranceKind":"rel","severity":"Block"}""";

    [Fact]
    public void Match_розбирає_ключі_і_порожній_об_єкт_дає_нуль_ключів()
    {
        var m = RelationSpecParser.ParseMatch(Match);
        Assert.True(m.IsOk);
        Assert.Equal(new RelationKey("Unit", "Unit"), Assert.Single(m.Value!.Keys));
        Assert.Empty(RelationSpecParser.ParseMatch("{}").Value!.Keys);
    }

    [Theory]
    [InlineData("""{"keys":{}}""")]
    [InlineData("""{"keys":[{"source":"A"}]}""")]
    [InlineData("""{"keys":[{"source":"A","target":" "}]}""")]
    [InlineData("""{"keys":[{"source":"A","target":"B"},{"source":"A","target":"B"}]}""")]
    [InlineData("[1]")]
    [InlineData("не json")]
    public void Match_відхиляє_хибну_форму(string json)
        => Assert.False(RelationSpecParser.ParseMatch(json).IsOk);

    [Theory]
    [InlineData("sum", RollupAggregate.Sum)]
    [InlineData("AVG", RollupAggregate.Avg)]
    [InlineData("Min", RollupAggregate.Min)]
    [InlineData("max", RollupAggregate.Max)]
    [InlineData("count", RollupAggregate.Count)]
    public void Rollup_приймає_п_ять_агрегатів_без_огляду_на_регістр(string agg, RollupAggregate expected)
    {
        var r = RelationSpecParser.ParseRollup($$"""{"sourceColumn":"A","targetColumn":"B","aggregate":"{{agg}}"}""");
        Assert.True(r.IsOk);
        Assert.Equal(expected, r.Value!.Aggregate);
    }

    [Theory]
    [InlineData("median")]
    [InlineData("1")]
    [InlineData("")]
    public void Rollup_відхиляє_невідомий_агрегат_у_тому_числі_число_замість_імені(string agg)
    {
        var r = RelationSpecParser.ParseRollup($$"""{"sourceColumn":"A","targetColumn":"B","aggregate":"{{agg}}"}""");
        Assert.False(r.IsOk);
    }

    [Fact]
    public void Rollup_без_MapJson_або_поля_відхиляється()
    {
        Assert.False(RelationSpecParser.ParseRollup(null).IsOk);
        Assert.False(RelationSpecParser.ParseRollup("""{"sourceColumn":"A","aggregate":"sum"}""").IsOk);
    }

    [Fact]
    public void Check_має_типові_значення_допуск_0_abs_Warn()
    {
        var c = RelationSpecParser.ParseCheck("""{"left":"A","right":"B"}""").Value!;
        Assert.Equal(0m, c.Tolerance);
        Assert.Equal(CheckToleranceKind.Abs, c.ToleranceKind);
        Assert.Equal(CheckSeverity.Warn, c.Severity);
    }

    [Fact]
    public void Check_розбирає_допуск_рядком_і_числом_та_серйозність()
    {
        var s = RelationSpecParser.ParseCheck(Chk).Value!;
        Assert.Equal(0.5m, s.Tolerance);
        Assert.Equal(CheckToleranceKind.Rel, s.ToleranceKind);
        Assert.Equal(CheckSeverity.Block, s.Severity);
        Assert.Equal(2.5m, RelationSpecParser.ParseCheck("""{"left":"A","right":"B","tolerance":2.5}""").Value!.Tolerance);
    }

    [Theory]
    [InlineData("""{"left":"A","right":"B","tolerance":"-1"}""")]
    [InlineData("""{"left":"A","right":"B","tolerance":"abc"}""")]
    [InlineData("""{"left":"A","right":"B","tolerance":true}""")]
    [InlineData("""{"left":"A","right":"B","toleranceKind":"pct"}""")]
    [InlineData("""{"left":"A","right":"B","severity":"Fatal"}""")]
    [InlineData("""{"left":"A","right":"B","severity":"2"}""")]
    [InlineData("""{"left":"A"}""")]
    public void Check_відхиляє_хибні_поля(string json)
        => Assert.False(RelationSpecParser.ParseCheck(json).IsOk);

    [Fact]
    public void Види_без_схеми_проходять_без_перевірки()
    {
        foreach (var kind in new[] { TableRelationKind.Mirror, TableRelationKind.Reference, TableRelationKind.Cascade, TableRelationKind.Copy })
        {
            Assert.Null(RelationSpecValidator.Validate(kind, "not even json", "garbage", Source, Target));
        }
    }

    [Fact]
    public void Коректні_Rollup_і_Check_проходять()
    {
        Assert.Null(RelationSpecValidator.Validate(TableRelationKind.Rollup, Match, Roll, Source, Target));
        Assert.Null(RelationSpecValidator.Validate(TableRelationKind.Check, "{}", Chk, Source, Target));
    }

    [Fact]
    public void Count_допускає_нечислову_колонку_джерела_а_Sum_ні()
    {
        var count = """{"sourceColumn":"Unit","targetColumn":"Total","aggregate":"count"}""";
        var sum = """{"sourceColumn":"Unit","targetColumn":"Total","aggregate":"sum"}""";
        Assert.Null(RelationSpecValidator.Validate(TableRelationKind.Rollup, Match, count, Source, Target));
        Assert.Equal("columnNotNumeric", RelationSpecValidator.Validate(TableRelationKind.Rollup, Match, sum, Source, Target)!.Reason);
    }

    [Theory]
    [InlineData("""{"keys":[{"source":"Nope","target":"Unit"}]}""", Roll)]
    [InlineData("""{"keys":[{"source":"Unit","target":"Nope"}]}""", Roll)]
    [InlineData(Match, """{"sourceColumn":"Nope","targetColumn":"Total","aggregate":"sum"}""")]
    [InlineData(Match, """{"sourceColumn":"Fact","targetColumn":"Nope","aggregate":"sum"}""")]
    [InlineData(Match, """{"sourceColumn":"Fact","targetColumn":"Name","aggregate":"sum"}""")]
    public void Rollup_відхиляє_неіснуючі_і_нечислові_колонки(string match, string map)
        => Assert.NotNull(RelationSpecValidator.Validate(TableRelationKind.Rollup, match, map, Source, Target));

    /// <summary>
    /// D-230: приймач Rollup — колонка Formula (її не правлять руками). Мутація: прибрати перевірку типу в
    /// <c>RelationSpecValidator</c> — тест червоніє.
    /// </summary>
    [Theory]
    [InlineData("Manual")]
    [InlineData("Name")]
    public void Приймач_Rollup_не_Formula_відхиляється(string targetColumn)
    {
        var map = $$"""{"sourceColumn":"Fact","targetColumn":"{{targetColumn}}","aggregate":"sum"}""";
        var failure = RelationSpecValidator.Validate(TableRelationKind.Rollup, Match, map, Source, Target);
        Assert.NotNull(failure);
        Assert.Contains("Formula", failure!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Приймач_Rollup_Decimal_відхиляється_з_причиною_targetNotFormula()
        => Assert.Equal("targetNotFormula", RelationSpecValidator.Validate(
            TableRelationKind.Rollup, Match, """{"sourceColumn":"Fact","targetColumn":"Manual","aggregate":"sum"}""", Source, Target)!.Reason);
    [Fact]
    public void Колонки_джерела_і_приймача_не_плутаються_місцями()
    {
        // Total є лише в приймачі: як sourceColumn він має бути відхилений.
        var map = """{"sourceColumn":"Total","targetColumn":"Total","aggregate":"sum"}""";
        Assert.Equal("columnMissing", RelationSpecValidator.Validate(TableRelationKind.Rollup, Match, map, Source, Target)!.Reason);
        Assert.NotNull(RelationSpecValidator.Validate(TableRelationKind.Check, "{}", """{"left":"Total","right":"Fact"}""", Source, Target));
    }

    [Fact]
    public void Check_відхиляє_хибний_допуск_і_нечислову_колонку()
    {
        Assert.NotNull(RelationSpecValidator.Validate(TableRelationKind.Check, "{}", """{"left":"Fact","right":"Total","tolerance":"-1"}""", Source, Target));
        Assert.Equal("columnNotNumeric", RelationSpecValidator.Validate(TableRelationKind.Check, "{}", """{"left":"Unit","right":"Total"}""", Source, Target)!.Reason);
    }
}
