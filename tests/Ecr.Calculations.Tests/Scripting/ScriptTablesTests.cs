using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests.Scripting;

/// <summary>
/// Агрегати над таблицями-псевдонімами (§13.1), правило копії аркуша, бюджет комірок, місячні колонки (§13.2),
/// колонки <c>Calculated</c> як поля рядка (§13.9) і вибір рядків <c>rows … where … active when</c> (§3.6).
/// </summary>
public sealed class ScriptTablesTests
{
    private const string Header = "rows from T1;\ntable G = T2;\n";

    private static ScriptStand Aggregates(string body, NumericMode mode = NumericMode.Strict)
    {
        var stand = new ScriptStand(Header + body, mode);
        stand.AddTable("T1").Column("K", CellDataType.String).Column("A")
            .Row("R1", ("K", "a"), ("A", 1m))
            .Row("R2", ("K", "zzz"), ("A", 1m))
            .Row("R3", ("K", "b"), ("A", 1m));
        stand.AddTable("T2").Column("K", CellDataType.String).Column("V").Column("D").Column("Hr_M01", month: 1).Column("Hr_M02", month: 2)
            .Row("G1", ("K", "a"), ("V", 1m), ("D", 1m), ("Hr_M02", 5m))
            .Row("G2", ("K", "a"), ("V", 3m), ("D", 1m), ("Hr_M02", 6m))
            .Row("G3", ("K", "a"), ("D", 1m))
            .Row("G4", ("K", "b"), ("V", 10m), ("D", 0m));
        return stand;
    }

    private const string AllKinds =
        "output S: kg = Sum(G.V where G.K == row.K);\n"
        + "output Mn: kg = Min(G.V where G.K == row.K);\n"
        + "output Mx: kg = Max(G.V where G.K == row.K);\n"
        + "output Av: kg = Avg(G.V where G.K == row.K);\n"
        + "output Fi: kg = First(G.V where G.K == row.K);\n"
        + "output Cn: one = Count(G where G.K == row.K);\n"
        + "output An: one = Any(G where G.K == row.K) ? 1 : 0;\n"
        + "output Mo: kg = Sum(G.Hr[2] where G.K == row.K);";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Агрегати_з_умовою_і_null_у_значеннях()
    {
        var stand = Aggregates(AllKinds);

        var (output, _) = await stand.RunAsync("R1");

        // Рядки K = a: V = 1, 3, null. null пропускається, але рядок у Count рахується.
        Assert.Equal(4m, ScriptStand.Value(output, "S"));
        Assert.Equal(1m, ScriptStand.Value(output, "Mn"));
        Assert.Equal(3m, ScriptStand.Value(output, "Mx"));
        Assert.Equal(2m, ScriptStand.Value(output, "Av"));
        Assert.Equal(1m, ScriptStand.Value(output, "Fi"));
        Assert.Equal(3m, ScriptStand.Value(output, "Cn"));
        Assert.Equal(1m, ScriptStand.Value(output, "An"));
        Assert.Equal(11m, ScriptStand.Value(output, "Mo"));
        Assert.Null(output.StepError);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Агрегати_порожньої_вибірки_Sum_нуль_Min_Max_Avg_First_null_Count_нуль_Any_хибність()
    {
        var stand = Aggregates(AllKinds);

        var (output, _) = await stand.RunAsync("R2");

        Assert.Equal(0m, ScriptStand.Value(output, "S"));
        Assert.Null(ScriptStand.Value(output, "Mn"));
        Assert.Null(ScriptStand.Value(output, "Mx"));
        Assert.Null(ScriptStand.Value(output, "Av"));
        Assert.Null(ScriptStand.Value(output, "Fi"));
        Assert.Equal(0m, ScriptStand.Value(output, "Cn"));
        Assert.Equal(0m, ScriptStand.Value(output, "An"));
        Assert.Equal(0m, ScriptStand.Value(output, "Mo"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Помилка_значення_стає_результатом_агрегату()
    {
        var stand = Aggregates("output E: kg = Sum(G.V / G.D where G.K == row.K);");

        var (output, _) = await stand.RunAsync("R3");

        Assert.Empty(output.Values);
        Assert.Equal("#DIV/0", output.StepError!.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Агрегати_у_Legacy_рахуються_арифметикою_double()
    {
        var stand = Aggregates("output S: kg = Sum(G.V where G.K == row.K);\noutput Av: kg = Avg(G.V where G.K == row.K);", NumericMode.Legacy);

        var (output, _) = await stand.RunAsync("R1");

        Assert.Equal(4m, ScriptStand.Value(output, "S"));
        Assert.Equal(2m, ScriptStand.Value(output, "Av"));
    }

    // ───── копія аркуша ─────

    private static ScriptStand WithCopies(bool tableCopy)
    {
        var stand = new ScriptStand(Header + "output S: kg = Sum(G.V where G.K == row.K);", NumericMode.Strict);
        stand.AddTable("T1").Column("K", CellDataType.String).Row("R1", ("K", "a"));
        stand.AddTable("T2").Column("K", CellDataType.String).Column("V").Row("G1", ("K", "a"), ("V", 1m));
        stand.AddTable("T1", instanceId: 503, tableDefId: 10, sheetCopyId: 77).Row("R1", ("K", "a"));
        if (tableCopy)
        {
            stand.AddTable("T2", instanceId: 502, tableDefId: 11, sheetCopyId: 77).Row("G1", ("K", "a"), ("V", 100m));
        }

        return stand;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Рядок_копії_аркуша_бачить_екземпляр_тієї_самої_копії_а_оригінал_лише_оригінал()
    {
        var stand = WithCopies(tableCopy: true);

        var original = (await stand.RunAsync("R1", stand.Tables[0])).Output;
        var copy = (await stand.RunAsync("R1", stand.Tables[2])).Output;

        Assert.Equal(1m, ScriptStand.Value(original, "S"));
        Assert.Equal(100m, ScriptStand.Value(copy, "S"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Копія_без_власної_таблиці_читає_оригінал()
    {
        var stand = WithCopies(tableCopy: false);

        var copy = (await stand.RunAsync("R1", stand.Tables[2])).Output;

        Assert.Equal(1m, ScriptStand.Value(copy, "S"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Бюджет_комірок_таблиць_псевдонімів_перевіряється_кодом_4222()
    {
        var stand = Aggregates("output S: kg = Sum(G.V where G.K == row.K);");
        stand.Limits = new CalculationLimits { MaxInputCellsPerBinding = 3 };

        var error = await Assert.ThrowsAsync<DomainException>(() => stand.RunAsync("R1"));

        Assert.Equal("ECR-CALC-4222", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-4222.inputCellsOverBudget", error.Details!["messageKey"]);
    }

    // ───── місячні колонки ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Місячна_колонка_за_псевдонімом_і_за_кодом_X_Mmm()
    {
        var stand = new ScriptStand(
            "rows from T1;\noutput ByAlias: kg = row.Hours[period.Month];\noutput ByAlias2: kg = row.Hours[2];\n"
            + "output ByCode: kg = row.Vol[2];\noutput ByCodeNext: kg = row.Vol[period.Month + 1];",
            NumericMode.Strict);
        stand.AddTable("T1")
            .Column("JanHours").Column("FebHours")
            .Column("Vol_M01", month: 1).Column("Vol_M02", month: 2)
            .Row("R1", ("JanHours", 5m), ("FebHours", 7m), ("Vol_M01", 11m), ("Vol_M02", 13m));
        stand.WithMonthAlias("T1", "Hours", 1, "JanHours").WithMonthAlias("T1", "Hours", 2, "FebHours");

        var (output, values) = await stand.RunAsync("R1");

        Assert.Equal(5m, ScriptStand.Value(output, "ByAlias"));
        Assert.Equal(7m, ScriptStand.Value(output, "ByAlias2"));
        Assert.Equal(13m, ScriptStand.Value(output, "ByCode"));
        Assert.Equal(13m, ScriptStand.Value(output, "ByCodeNext"));
        Assert.Null(output.StepError);
        Assert.Equal(4, values.Count);
    }

    [Theory]
    [InlineData("row.Vol[13]")]
    [InlineData("row.Vol[0]")]
    [InlineData("row.Vol[1.5]")]
    [InlineData("row.Vol[3]")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Місяць_поза_1_12_нецілий_або_без_колонки_дає_ARG(string expression)
    {
        var stand = new ScriptStand("rows from T1;\noutput Y: kg = " + expression + ";", NumericMode.Strict);
        stand.AddTable("T1").Column("Vol_M01", month: 1).Column("Vol_M02", month: 2).Row("R1", ("Vol_M01", 1m), ("Vol_M02", 2m));

        var (output, _) = await stand.RunAsync("R1");

        Assert.Empty(output.Values);
        Assert.Equal("#ARG", output.StepError!.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Порожня_місячна_комірка_обовязкова_дає_ARG_а_необовязкова_null()
    {
        var stand = new ScriptStand(
            "rows from T1;\nvar o = row.Vol?[2];\noutput Y: kg = row.A;\noutput Z: kg = row.Vol[2];", NumericMode.Strict);
        stand.AddTable("T1").Column("A").Column("Vol_M01", month: 1).Column("Vol_M02", month: 2).Row("R1", ("A", 1m), ("Vol_M01", 1m));

        var (output, values) = await stand.RunAsync("R1");

        Assert.Equal("Null", values.Single(v => v.Name == "o").ValueType);
        Assert.Equal(1m, ScriptStand.Value(output, "Y"));
        Assert.Null(ScriptStand.Value(output, "Z"));
        Assert.Equal("#ARG", output.StepError!.ErrorCode);
    }

    // ───── колонки Calculated: результати інших методологій ─────

    private static ScriptStand WithCalculated(string body, out int columnId)
    {
        var stand = new ScriptStand("rows from T1;\n" + body, NumericMode.Strict);
        stand.AddTable("T1").Column("K", CellDataType.String).Column("A").Column("Res", CellDataType.Calculated)
            .Row("R1", ("K", "a"), ("A", 1m))
            .Row("R2", ("K", "a"), ("A", 1m));
        columnId = stand.ColumnId("T1", "Res");
        stand.WithBinding(new ColumnResultBinding(stand.Main.TableDefId, columnId, 99, "X", "{}", [9901]));
        return stand;
    }

    private static CalculationResultRow Result(string row, decimal value, long instance = 500)
        => new(9901, row, "X", value, 1, null, instance);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Колонка_Calculated_читає_результат_того_самого_прогону()
    {
        var stand = WithCalculated("output Y: kg = row.Res * 2;", out _);
        stand.Run.CalculationRunId = 5;
        stand.Results.ReadRunAsync(5, ScriptStand.DocumentId, ScriptStand.PeriodValue, Arg.Any<CurrentResultFilter>(), Arg.Any<CancellationToken>())
            .Returns([Result("R1", 12.5m)]);
        // Актуальний прогін має ІНШЕ число — воно не береться, бо джерело в цьому прогоні порахувалось.
        stand.Results.ReadCurrentAsync(ScriptStand.DocumentId, ScriptStand.PeriodValue, Arg.Any<CancellationToken>())
            .Returns([Result("R1", 99m), Result("R2", 3m)]);

        var found = (await stand.RunAsync("R1")).Output;
        var absent = (await stand.RunAsync("R2")).Output;

        Assert.Equal(25m, ScriptStand.Value(found, "Y"));

        // Результату для рядка немає → #ARG (рядок «без входів»).
        Assert.Empty(absent.Values);
        Assert.Equal(["Res"], absent.MissingArguments);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Поза_прогоном_і_для_джерела_без_прогону_читаються_актуальні_результати()
    {
        var outside = WithCalculated("output Y: kg = row.Res * 2;", out _);
        outside.Results.ReadCurrentAsync(ScriptStand.DocumentId, ScriptStand.PeriodValue, Arg.Any<CancellationToken>())
            .Returns([Result("R1", 7m)]);

        var inRunWithoutSource = WithCalculated("output Y: kg = row.Res * 2;", out _);
        inRunWithoutSource.Run.CalculationRunId = 6;
        inRunWithoutSource.Results.ReadCurrentAsync(ScriptStand.DocumentId, ScriptStand.PeriodValue, Arg.Any<CancellationToken>())
            .Returns([Result("R1", 8m)]);

        Assert.Equal(14m, ScriptStand.Value((await outside.RunAsync("R1")).Output, "Y"));
        Assert.Equal(16m, ScriptStand.Value((await inRunWithoutSource.RunAsync("R1")).Output, "Y"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Необовязкова_колонка_Calculated_без_результату_дає_null()
    {
        var stand = WithCalculated("var r = row.Res?;\noutput Y: kg = row.A;", out _);

        var (output, values) = await stand.RunAsync("R1");

        Assert.Equal(1m, ScriptStand.Value(output, "Y"));
        Assert.Equal("Null", values.Single(v => v.Name == "r").ValueType);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Колонка_Calculated_таблиці_псевдоніма_читається_в_агрегаті()
    {
        var stand = new ScriptStand(Header + "output S: kg = Sum(G.Res where G.K == row.K);", NumericMode.Strict);
        stand.AddTable("T1").Column("K", CellDataType.String).Row("R1", ("K", "a"));
        stand.AddTable("T2").Column("K", CellDataType.String).Column("Res", CellDataType.Calculated)
            .Row("G1", ("K", "a")).Row("G2", ("K", "a")).Row("G3", ("K", "b"));
        stand.WithBinding(new ColumnResultBinding(stand.Tables[1].TableDefId, stand.ColumnId("T2", "Res"), 99, "X", "{}", [9901]));
        stand.Run.CalculationRunId = 5;
        stand.Results.ReadRunAsync(5, ScriptStand.DocumentId, ScriptStand.PeriodValue, Arg.Any<CurrentResultFilter>(), Arg.Any<CancellationToken>())
            .Returns([Result("G1", 2m, 501), Result("G2", 3m, 501), Result("G3", 100m, 501)]);

        var (output, _) = await stand.RunAsync("R1");

        Assert.Equal(5m, ScriptStand.Value(output, "S"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Читання_результатів_визначає_методології_джерела_і_ігнорує_власну()
    {
        var stand = WithCalculated("output Y: kg = row.Res * 2;", out var columnId);
        stand.WithBinding(new ColumnResultBinding(stand.Main.TableDefId, columnId, ScriptStand.MethodologyId, "Own", "{}", [7001]));
        var plain = new ScriptStand("rows from T1;\noutput Y: kg = row.A;", NumericMode.Strict);
        plain.AddTable("T1").Column("A").Column("Res", CellDataType.Calculated).Row("R1", ("A", 1m));
        plain.WithBinding(new ColumnResultBinding(plain.Main.TableDefId, plain.ColumnId("T1", "Res"), 99, "X", "{}", [9901]));

        var reads = await stand.Module.ReadsMethodologyIdsAsync(
            stand.Methodology, stand.Main.InstanceId, new DateOnly(2099, 1, 31), CancellationToken.None);
        var none = await plain.Module.ReadsMethodologyIdsAsync(
            plain.Methodology, plain.Main.InstanceId, new DateOnly(2099, 1, 31), CancellationToken.None);

        Assert.Equal([99], reads.Order().ToArray());
        Assert.Empty(none);
    }

    // ───── вибір рядків ─────

    private const string RowsClause =
        "rows from T1 where row.Fuel == \"gas\" && row.Eq in (\"a\", \"b\") active when row.Status == entry(\"REG\", \"Running\");\n"
        + "output Y: kg = 1;";

    private static ScriptStand Selection(string text)
    {
        var stand = new ScriptStand(text, NumericMode.Strict).WithEntry("REG", "Running", 555);
        stand.AddTable("T1").Column("Fuel", CellDataType.String).Column("Eq", CellDataType.String).Column("Status", CellDataType.Lookup)
            .Row("R1", ("Fuel", "gas"), ("Eq", "a"), ("Status", new StandEntry(555)))
            .Row("R2", ("Fuel", "gas"), ("Eq", "a"), ("Status", new StandEntry(777)))
            .Row("R3", ("Fuel", "oil"), ("Eq", "a"), ("Status", new StandEntry(555)))
            .Row("R4", ("Fuel", "gas"), ("Eq", "c"), ("Status", new StandEntry(555)));
        return stand;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Селектор_відбирає_неактивні_і_не_відібрані_рядки()
    {
        var stand = Selection(RowsClause);

        var outcome = await stand.SelectAsync();

        Assert.Equal([new RowMatchOutcomeKey("R1", "T1")], outcome.Matches.Select(m => new RowMatchOutcomeKey(m.RowKey, m.RuleCode)).ToArray());
        Assert.Equal(["R2"], outcome.Excluded);
        Assert.Equal(["R1", "R2", "R3", "R4"], outcome.AllRowKeys);
        Assert.NotNull(outcome.Read);
        Assert.Equal(4, outcome.Read!.RowIds.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Без_where_беруться_усі_активні_рядки_а_без_active_when_ніхто_не_виключається()
    {
        var noWhere = Selection("rows from T1 active when row.Status == entry(\"REG\", \"Running\");\noutput Y: kg = 1;");
        var noActive = Selection("rows from T1 where row.Fuel == \"gas\";\noutput Y: kg = 1;");

        var a = await noWhere.SelectAsync();
        var b = await noActive.SelectAsync();

        Assert.Equal(["R1", "R3", "R4"], a.Matches.Select(m => m.RowKey).ToArray());
        Assert.Equal(["R2"], a.Excluded);
        Assert.Equal(["R1", "R2", "R4"], b.Matches.Select(m => m.RowKey).ToArray());
        Assert.Empty(b.Excluded);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Кілька_речень_rows_кожне_для_своєї_таблиці_а_чужа_таблиця_без_правил()
    {
        var stand = new ScriptStand(
            "rows from T1 where row.Fuel == \"gas\";\nrows from T3 where row.Z == \"1\";\noutput Y: kg = 1;", NumericMode.Strict);
        stand.AddTable("T1").Column("Fuel", CellDataType.String).Row("R1", ("Fuel", "gas")).Row("R2", ("Fuel", "oil"));
        stand.AddTable("T2").Column("Fuel", CellDataType.String).Row("R1", ("Fuel", "gas"));
        stand.AddTable("T3").Column("Z", CellDataType.String).Row("Q1", ("Z", "1")).Row("Q2", ("Z", "2"));

        var first = await stand.SelectAsync(stand.Tables[0]);
        var third = await stand.SelectAsync(stand.Tables[2]);
        var foreign = await stand.SelectAsync(stand.Tables[1]);

        Assert.Equal(["R1"], first.Matches.Select(m => m.RowKey).ToArray());
        Assert.Equal(["Q1"], third.Matches.Select(m => m.RowKey).ToArray());
        Assert.Equal("T3", third.Matches.Single().RuleCode);

        // Таблиці, коду якої немає в скрипті, рядки не потрібні: так само, як версія без правил.
        Assert.Empty(foreign.Matches);
        Assert.Null(foreign.AllRowKeys);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Нерозвязаний_entry_нічому_не_дорівнює_і_колонки_нема_тексту()
    {
        var stand = Selection("rows from T1 where row.Status == entry(\"REG\", \"Nope\") || row.NoSuchColumn == \"x\";\noutput Y: kg = 1;");

        var outcome = await stand.SelectAsync();

        Assert.Empty(outcome.Matches);
    }

    private sealed record RowMatchOutcomeKey(string RowKey, string Rule);
}
