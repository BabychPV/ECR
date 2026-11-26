// tests/Ecr.Calculations.Tests/Scripting/SgFlaringBindingEngineTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.MethodologyScriptMigration.Flaring;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests.Scripting;

/// <summary>
/// ✎ SGF-flaring (картка integ-sg-flaring-composition): скрипт, який дає міст GCV (<see cref="SgFlaringBinding"/>), на
/// справжньому рушії скриптів і довідниках складу ECR з версіями: версія — на дату події (H4/H11), бракує складу чи кейсу —
/// рядок відхилено з причиною, ніколи не 0 (AC-2, H9/H12), ТОх EOR — склад EOR (H3), поїзд → свій 481 (H10), сезон (H6).
/// </summary>
/// <remarks>
/// Тіло — мініатюра перенесених формул <c>Flert</c>/<c>Thermaloxidizer</c> у тому вигляді, який дає конвертер
/// (<c>row.GCV_&lt;X&gt;</c>). Межа версій кейсу «KE01» — 2099-01-15 (H2S 10 → 20). Мутаційні докази — у звіті лінії.
/// </remarks>
public sealed class SgFlaringBindingEngineTests
{
    private const string IntBody = """
        output MolecularWeight : one = row.GCV_Methane * 16 + row.GCV_H2S * 34;
        output TotalSulphurContent : one = row.GCV_H2S + row.GCV_COS;
        output EC_Wzv : one = row.GCV_Temperature + 273;
        output H2S_tonn : t = row.TOTAL * row.GCV_H2S / 100;
        """;

    private const string ToxBody = """
        output HSE_TO_SG_481_SOR_H2S : one = 100 - row.GCV_SOR_Methane;
        output HSE_TO_SG_481_EOR_H2S : one = 100 - row.GCV_EOR_Methane;
        output HSE_TO_SG_SOR_CH4 : one = row.GCV_SOR_Methane + row.GCV_SOR_Helium;
        output HSE_TO_SG_EOR_CH4 : one = row.GCV_EOR_Methane + row.GCV_EOR_C2H6S;
        """;

    private const string Tox481Body = """
        output HSE_TO_SG_481_SOR_H2S : one = 100 - row.GCV_SOR_Methane;
        output HSE_TO_SG_SOR_H2S : one = row.GCV_SOR_H2S + row.GCV_SOR_Helium;
        """;

    private const string ContBody = """
        output Sour_H2S : one = row.GCV_H2S;
        visible var Coef : one = split_coef;
        """;

    // ───── генератор ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-BRIDGE")]
    public void Міст_ставить_свій_відбір_і_row_GCV_стає_змінною_складу()
    {
        var text = SgFlaringBinding.Bind(IntBody, SgFlaringConsumer.IntOnshore);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, @"^rows from", System.Text.RegularExpressions.RegexOptions.Multiline));
        Assert.StartsWith("// SGF bridge", text, StringComparison.Ordinal);
        Assert.Contains("rows from INT_SG_EVENTS where row.LOCATION in (null, entry(\"FLARE_LOCATION\", \"ONSHORE_HP\")", text, StringComparison.Ordinal);
        Assert.Contains("registry as of row.EVENT_START;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("row.GCV_", text, StringComparison.Ordinal);
        Assert.DoesNotContain("??", text, StringComparison.Ordinal);
        Assert.Contains("var GCV_H2S = REGSUM(\"GAS_COMPOSITION\", ROW.CASE == gas_case && ROW.COMPONENT == REGFIND(\"COMPONENT\", \"H2S\"), ROW.MOL_PCT);", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-AC-2")]
    public void Невідомий_GCV_не_прив_язується_до_нуля__генератор_відмовляє()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => SgFlaringBinding.Bind("output X : one = row.GCV_Unobtainium;", SgFlaringConsumer.IntOnshore));

        Assert.Contains("GCV_Unobtainium", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-BRIDGE")]
    public void Довгі_аліаси_FLERT_читають_короткий_код_компонента()
    {
        var text = SgFlaringBinding.Bind("output X : one = row.GCV_Carbon_Monoxide;", SgFlaringConsumer.IntOnshore);

        Assert.Contains("REGFIND(\"COMPONENT\", \"CO\")", text, StringComparison.Ordinal);
    }

    /// <summary>Огляд L5: власний відбір тіла не відкидається мовчки.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-REVIEW-L5")]
    public void Тіло_з_власним_rows_from__генератор_відмовляє()
    {
        var error = Assert.Throws<InvalidOperationException>(() => SgFlaringBinding.Bind(
            "rows from ANY where row.X == \"a\" active when row.Y == \"b\";\noutput A : one = row.GCV_H2S;\n", SgFlaringConsumer.IntOnshore));

        Assert.Contains("rows from", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Огляд M2: тіло ТОх читає H2S типу, а передформули 481 цього типу немає — генератор відмовляє.</summary>
    [Theory]
    [InlineData("SOR")]
    [InlineData("EOR")]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-REVIEW-M2")]
    public void ТОх__H2S_без_передформули_481__генератор_відмовляє(string type)
    {
        var other = type == "SOR" ? "EOR" : "SOR";
        var body = $"output HSE_TO_SG_481_{other}_H2S : one = 100 - row.GCV_{other}_Methane;\n"
                   + $"output A : one = row.GCV_{type}_H2S;\n";

        var error = Assert.Throws<InvalidOperationException>(() => SgFlaringBinding.Bind(body, SgFlaringConsumer.ToxDaily));

        Assert.Contains($"HSE_TO_SG_481_{type}_H2S", error.Message, StringComparison.Ordinal);
    }

    // ───── огляд H1: відбір рядків — кожен рядок бере рівно одна методологія, порожнє місце/поїзд видно ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-REVIEW-H1")]
    public async Task INT__порожнє_місце_відбирає_гілка_суші_і_відхиляє__гілка_островів_не_бере()
    {
        var onshore = IntStand();
        var offshore = IntStand(SgFlaringBinding.Bind(IntBody, SgFlaringConsumer.IntOffshore));

        var onshoreRows = (await onshore.SelectAsync()).Matches.Select(m => m.RowKey).ToList();
        var offshoreRows = (await offshore.SelectAsync()).Matches.Select(m => m.RowKey).ToList();

        Assert.Contains("NULL_LOCATION", onshoreRows);
        Assert.Contains("BEFORE", onshoreRows);
        Assert.DoesNotContain("NULL_LOCATION", offshoreRows);
        Assert.Contains("D_ISLAND", offshoreRows);
        Assert.DoesNotContain("D_ISLAND", onshoreRows);
        Assert.Empty(onshoreRows.Intersect(offshoreRows));

        // Відібраний рядок без місця — видимо відхилений, а не пропущений.
        var error = await Assert.ThrowsAsync<DomainException>(() => onshore.RunAsync("NULL_LOCATION"));
        Assert.Contains("flare_location_not_mapped", string.Join(" ", error.Details!.Values), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NULL_TRAIN", "tox_row_without_column_train_or_type")]
    [InlineData("TRAIN_3", "tox_train_unknown")]
    [InlineData("TYPE_XOR", "tox_type_unknown")]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-REVIEW-H1-M1")]
    public async Task ТОх__порожній_чи_невідомий_поїзд_тип__відібрано_і_відхилено__сміття_Train_0_не_відбирається(string row, string reason)
    {
        var stand = ToxStand();

        var selected = (await stand.SelectAsync()).Matches.Select(m => m.RowKey).ToList();
        var error = await Assert.ThrowsAsync<DomainException>(() => stand.RunAsync(row));

        Assert.Contains(row, selected);
        Assert.DoesNotContain("JUNK_TRAIN_0", selected);
        Assert.Contains(reason, string.Join(" ", error.Details!.Values), StringComparison.Ordinal);
    }

    // ───── огляд H2 + L3: розподіл 2024 зі значень провізії проходить перевірку Σ ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-REVIEW-H2")]
    public async Task Острів_D__розподіл_2024_зі_значень_AF_провізії__рядок_рахується()
    {
        var coefs = Ecr.Infrastructure.Jobs.FlaringRegistryProvisioner.Slots.Select(s => s.Coef2024).ToArray();
        Assert.NotEqual(1m, coefs.Sum());

        var stand = ContStand(coefs: coefs);
        var (_, values) = await stand.RunAsync("APRIL");

        Assert.Equal(coefs[0].ToString(System.Globalization.CultureInfo.InvariantCulture), values.Single(v => v.Name == "Coef").Text);
    }

    // ───── огляд L6: місячний ТОх ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-REVIEW-L6")]
    public async Task ТОх_місячний__статичний_рядок_місяця_читає_кейс_типу_на_1_число()
    {
        var text = SgFlaringBinding.Bind(ToxBody, SgFlaringConsumer.ToxMonthly);
        var stand = Stand(text, new Registries(), (new DateOnly(2099, 1, 1), new DateOnly(2099, 1, 31)));
        stand.AddTable("TOX_MONTHLY")
            .Column("MONTH", CellDataType.Date).Column("TRAIN", CellDataType.String).Column("TYPE", CellDataType.String)
            .Column("GAS_COLUMN", CellDataType.String)
            .Row("M_T2_EOR", ("MONTH", new DateTime(2099, 1, 1)), ("TRAIN", "Train 2"), ("TYPE", "EOR"), ("GAS_COLUMN", "481"));

        var (output, _) = await stand.RunAsync("M_T2_EOR");

        Assert.Contains("rows from TOX_MONTHLY", text, StringComparison.Ordinal);
        Assert.Contains("registry as of row.MONTH;", text, StringComparison.Ordinal);
        Assert.Equal(68m, ScriptStand.Value(output, "HSE_TO_SG_EOR_CH4"));
    }

    // ───── H4/H11: версія — на дату початку події ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-H11")]
    public async Task Періодичний__події_по_різні_боки_межі_версій_читають_свою_версію_складу()
    {
        var stand = IntStand();

        var (before, _) = await stand.RunAsync("BEFORE");
        var (after, _) = await stand.RunAsync("AFTER");

        Assert.Equal((80m * 16) + (10m * 34), ScriptStand.Value(before, "MolecularWeight"));
        Assert.Equal((70m * 16) + (20m * 34), ScriptStand.Value(after, "MolecularWeight"));
        Assert.Equal(1000m * 10m / 100, ScriptStand.Value(before, "H2S_tonn"));
        Assert.Equal(1000m * 20m / 100, ScriptStand.Value(after, "H2S_tonn"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-BRIDGE")]
    public async Task Компонент_без_рядка_в_наявному_кейсі__0__порожня_T_C__0_і_рядок_рахується()
    {
        var stand = IntStand();

        var (before, _) = await stand.RunAsync("BEFORE");
        var (after, _) = await stand.RunAsync("AFTER");

        Assert.Equal(10m, ScriptStand.Value(before, "TotalSulphurContent"));   // COS — рядка немає → 0
        Assert.Equal(30m + 273, ScriptStand.Value(before, "EC_Wzv"));
        Assert.Equal(0m + 273, ScriptStand.Value(after, "EC_Wzv"));             // T_C порожня → 0, як стара система
    }

    // ───── AC-2: бракує складу/кейсу → відхилено з причиною, ніколи не 0 ─────

    [Theory]
    [InlineData("NO_STREAM", "stream_not_found")]
    [InlineData("NO_CASE", "composition_case_not_found")]
    [InlineData("NO_COMPOSITION", "case_marked_no_composition")]
    [InlineData("EMPTY_SUM", "case_composition_empty")]
    [InlineData("NULL_ID", "event_without_stream_or_case")]
    [InlineData("NULL_LOCATION", "flare_location_not_mapped")]
    [InlineData("ONSHORE_MISMATCH", "stream_onshore_mismatch")]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-AC-2")]
    public async Task Бракує_складу_чи_кейсу__рядок_відхилено_з_причиною_а_не_нуль(string row, string reason)
    {
        var stand = IntStand();

        var error = await Assert.ThrowsAsync<DomainException>(() => stand.RunAsync(row));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Contains(reason, string.Join(" ", error.Details!.Values), StringComparison.Ordinal);
    }

    // ───── ТОх: H3 (EOR — склад EOR), H10 (поїзд → свій 481), Helium/C2H6S = 0 ─────

    [Theory]
    [InlineData("T1_SOR", 50, 0)]
    [InlineData("T1_EOR", 0, 58)]
    [InlineData("T2_SOR", 69, 0)]
    [InlineData("T2_EOR", 0, 68)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-H3-H10")]
    public async Task ТОх__тип_рядка_обирає_кейс_SOR_EOR__поїзд_обирає_потік_481(string row, int sor, int eor)
    {
        var stand = ToxStand();

        var (output, _) = await stand.RunAsync(row);

        // Обидві родини формул читають кейс ТИПУ РЯДКА: SOR-рядок — склад SOR, EOR-рядок — склад EOR (не SOR, як L1).
        var expected = sor + eor;
        Assert.Equal(expected, ScriptStand.Value(output, "HSE_TO_SG_SOR_CH4"));
        Assert.Equal(expected, ScriptStand.Value(output, "HSE_TO_SG_EOR_CH4"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-AC-2")]
    public async Task ТОх__стовпець_без_закріплення__відхилено_а_не_нуль()
    {
        var error = await Assert.ThrowsAsync<DomainException>(() => ToxStand().RunAsync("NO_PIN"));

        Assert.Contains("stream_pin_not_found", string.Join(" ", error.Details!.Values), StringComparison.Ordinal);
    }

    // ───── ТОх: передформула 481 підміняє H2S (рішення людини 26.11), порядок обчислення ─────

    [Theory]
    [InlineData("T1_481", 50)]
    [InlineData("T1_417", 17)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-481")]
    public async Task ТОх__стовпець_481_бере_H2S_з_передформули__інші_стовпці_зі_складу(string row, int h2s)
    {
        var stand = ToxStand(Tox481Body);

        var (output, _) = await stand.RunAsync(row);

        Assert.Equal(h2s, ScriptStand.Value(output, "HSE_TO_SG_SOR_H2S"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-481")]
    public void ТОх__підміна_стоїть_одразу_після_передформули_і_до_першого_читання()
    {
        var text = SgFlaringBinding.Bind(Tox481Body, SgFlaringConsumer.ToxDaily);

        var formula = text.IndexOf("output HSE_TO_SG_481_SOR_H2S", StringComparison.Ordinal);
        var @override = text.IndexOf("var GCV_SOR_H2S = row.GAS_COLUMN == \"481\" ? HSE_TO_SG_481_SOR_H2S : GCV_SOR_H2S_FLERT;", StringComparison.Ordinal);
        var read = text.IndexOf("output HSE_TO_SG_SOR_H2S", StringComparison.Ordinal);
        Assert.True(formula >= 0 && formula < @override && @override < read, text);
        Assert.Contains("var GCV_SOR_H2S_FLERT = REGSUM(", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-481")]
    public void ТОх__H2S_читається_до_передформули__генератор_відмовляє()
    {
        var error = Assert.Throws<InvalidOperationException>(() => SgFlaringBinding.Bind(
            "output A : one = row.GCV_SOR_H2S;\noutput HSE_TO_SG_481_SOR_H2S : one = 100 - row.GCV_SOR_Methane;\n",
            SgFlaringConsumer.ToxDaily));

        Assert.Contains("GCV_SOR_H2S", error.Message, StringComparison.Ordinal);
    }

    // ───── Постійні острів D: сезон з asof.Month (H6), розподіл — довідник (H7) ─────

    [Theory]
    [InlineData("MARCH", 5, 0.4)]
    [InlineData("APRIL", 6, 0.4)]
    [InlineData("SEPTEMBER", 6, 0.4)]
    [InlineData("OCTOBER", 5, 0.4)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-H6")]
    public async Task Постійні_острів_D__квітень_вересень_літній_кейс_інакше_зимовий(string row, int h2s, double coef)
    {
        var stand = ContStand();

        var (output, values) = await stand.RunAsync(row);

        Assert.Equal(h2s, ScriptStand.Value(output, "Sour_H2S"));
        Assert.Equal(h2s == 6 ? 996m : 997m, ScriptStand.Value(output, SgFlaringBinding.FcidOutput));   // огляд H4: ключ EFID|FCID
        Assert.Equal(((decimal)coef).ToString(System.Globalization.CultureInfo.InvariantCulture), values.Single(v => v.Name == "Coef").Text);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-AC-2")]
    public async Task Постійні_острів_D__розподіл_не_з_трьох_слотів__відхилено()
    {
        var stand = ContStand(slots: 2);

        var error = await Assert.ThrowsAsync<DomainException>(() => stand.RunAsync("APRIL"));

        Assert.Contains("split_not_three_slots_or_sum_not_one", string.Join(" ", error.Details!.Values), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-AC-2")]
    public async Task Постійні_острів_D__рядок_без_слота__відхилено()
    {
        var error = await Assert.ThrowsAsync<DomainException>(() => ContStand().RunAsync("NO_SLOT"));

        Assert.Contains("split_slot_empty", string.Join(" ", error.Details!.Values), StringComparison.Ordinal);
    }

    // ───── стенди ─────

    private static ScriptStand IntStand(string? text = null)
    {
        var registries = new Registries();
        var stand = Stand(text ?? SgFlaringBinding.Bind(IntBody, SgFlaringConsumer.IntOnshore), registries, (new DateOnly(2099, 1, 1), new DateOnly(2099, 1, 31)));
        stand.WithEntry("FLARE_LOCATION", "ONSHORE_HP", Registries.OnshoreHp).WithEntry("FLARE_LOCATION", "ONSHORE_LP", 99_001)
            .WithEntry("FLARE_LOCATION", "A_ISLAND_HP", 99_002).WithEntry("FLARE_LOCATION", "D_ISLAND_HP", Registries.DIslandHp)
            .WithEntry("FLARE_LOCATION", "D_ISLAND_LP", 99_004);
        stand.AddTable("INT_SG_EVENTS")
            .Column("EVENT_START", CellDataType.Date).Column("LOCATION", CellDataType.Lookup).Column("GAS_COMPOSITION_ID")
            .Column("STREAM_TYPE", CellDataType.String).Column("TOTAL")
            .Row("BEFORE", Event(new DateTime(2099, 1, 10, 19, 30, 0), Registries.OnshoreHp, 26, "KE01"))
            .Row("AFTER", Event(new DateTime(2099, 1, 15), Registries.OnshoreHp, 26, "KE01"))
            .Row("NO_STREAM", Event(new DateTime(2099, 1, 10), Registries.OnshoreHp, 404, "KE01"))
            .Row("NO_CASE", Event(new DateTime(2099, 1, 10), Registries.OnshoreHp, 26, "KE01 typo"))
            .Row("NO_COMPOSITION", Event(new DateTime(2099, 1, 10), Registries.OnshoreHp, 26, "No composition"))
            .Row("EMPTY_SUM", Event(new DateTime(2099, 1, 10), Registries.OnshoreHp, 26, "Empty"))
            .Row("NULL_ID", Event(new DateTime(2099, 1, 10), Registries.OnshoreHp, null, "KE01"))
            .Row("NULL_LOCATION", Event(new DateTime(2099, 1, 10), null, 26, "KE01"))
            .Row("ONSHORE_MISMATCH", Event(new DateTime(2099, 1, 10), Registries.OnshoreHp, 19, "370 Summer"))
            .Row("D_ISLAND", Event(new DateTime(2099, 1, 10), Registries.DIslandHp, 19, "370 Summer"));
        return stand;
    }

    private static (string, object?)[] Event(DateTime start, long? location, int? streamId, string type)
        => [("EVENT_START", start), ("LOCATION", location is { } l ? new StandEntry(l) : null), ("GAS_COMPOSITION_ID", streamId),
            ("STREAM_TYPE", type), ("TOTAL", 1000m)];

    private static ScriptStand ToxStand(string? body = null)
    {
        var registries = new Registries();
        var stand = Stand(SgFlaringBinding.Bind(body ?? ToxBody, SgFlaringConsumer.ToxDaily), registries, (new DateOnly(2099, 1, 1), new DateOnly(2099, 1, 31)));
        stand.AddTable("TOX_DAILY")
            .Column("DATE", CellDataType.Date).Column("TRAIN", CellDataType.String).Column("TYPE", CellDataType.String)
            .Column("GAS_COLUMN", CellDataType.String)
            .Row("T1_SOR", ("DATE", new DateTime(2099, 1, 5)), ("TRAIN", "Train 1"), ("TYPE", "SOR"), ("GAS_COLUMN", "481"))
            .Row("T1_EOR", ("DATE", new DateTime(2099, 1, 5)), ("TRAIN", "Train 1"), ("TYPE", "EOR"), ("GAS_COLUMN", "481"))
            .Row("T2_SOR", ("DATE", new DateTime(2099, 1, 5)), ("TRAIN", "Train 2"), ("TYPE", "SOR"), ("GAS_COLUMN", "481"))
            .Row("T2_EOR", ("DATE", new DateTime(2099, 1, 5)), ("TRAIN", "Train 2"), ("TYPE", "EOR"), ("GAS_COLUMN", "481"))
            .Row("NO_PIN", ("DATE", new DateTime(2099, 1, 5)), ("TRAIN", "Train 1"), ("TYPE", "SOR"), ("GAS_COLUMN", "999"))
            .Row("T1_481", ("DATE", new DateTime(2099, 1, 5)), ("TRAIN", "Train 1"), ("TYPE", "SOR"), ("GAS_COLUMN", "481"))
            .Row("T1_417", ("DATE", new DateTime(2099, 1, 5)), ("TRAIN", "Train 1"), ("TYPE", "SOR"), ("GAS_COLUMN", "417"))
            .Row("NULL_TRAIN", ("DATE", new DateTime(2099, 1, 5)), ("TYPE", "SOR"), ("GAS_COLUMN", "481"))
            .Row("TRAIN_3", ("DATE", new DateTime(2099, 1, 5)), ("TRAIN", "Train 3"), ("TYPE", "SOR"), ("GAS_COLUMN", "481"))
            .Row("TYPE_XOR", ("DATE", new DateTime(2099, 1, 5)), ("TRAIN", "Train 1"), ("TYPE", "XOR"), ("GAS_COLUMN", "481"))
            .Row("JUNK_TRAIN_0", ("DATE", new DateTime(2099, 1, 5)), ("TRAIN", "0"), ("TYPE", "SOR"), ("GAS_COLUMN", "481"));
        return stand;
    }

    private static ScriptStand ContStand(int slots = 3, decimal[]? coefs = null)
    {
        var registries = new Registries { SplitSlots = slots, SplitCoefs = coefs ?? [0.4m, 0.4m, 0.2m] };
        var stand = Stand(SgFlaringBinding.Bind(ContBody, SgFlaringConsumer.ContOffshore), registries, (new DateOnly(2099, 1, 1), new DateOnly(2099, 12, 31)));
        stand.AddTable("CONT_SG_DISLAND").Column("DAY", CellDataType.Date).Column("SLOT")
            .Row("MARCH", ("DAY", new DateTime(2099, 3, 31)), ("SLOT", 1))
            .Row("APRIL", ("DAY", new DateTime(2099, 4, 1)), ("SLOT", 1))
            .Row("SEPTEMBER", ("DAY", new DateTime(2099, 9, 30)), ("SLOT", 1))
            .Row("OCTOBER", ("DAY", new DateTime(2099, 10, 1)), ("SLOT", 1))
            .Row("NO_SLOT", ("DAY", new DateTime(2099, 4, 1)));
        return stand;
    }

    private static ScriptStand Stand(string text, Registries registries, (DateOnly, DateOnly) bounds)
    {
        var source = registries.Build();
        var stand = new ScriptStand(text, NumericMode.Legacy) { RegistryLoader = new SourceLoader(source), Bounds = bounds };
        stand.RegistryDefinitions.AddRange(source.Registries);
        return stand;
    }

    /// <summary>Довідники складу ECR (STREAM/STREAM_CASE/GAS_COMPOSITION/COMPONENT) + довідники факелів SGF.</summary>
    private sealed class Registries
    {
        public const long OnshoreHp = 99_000;

        public const long DIslandHp = 99_003;

        private const int Stream = 1, Case = 2, Component = 3, Composition = 4, Location = 5, Pin = 6, Split = 7;

        private readonly List<RegistryDef> _defs = [];
        private readonly List<RegistryKeyDef> _keys = [];
        private readonly List<RegistryEntry> _entries = [];
        private readonly List<RegistrySnapshotValue> _values = [];
        private readonly Dictionary<(int Registry, string Field), int> _fields = [];
        private long _next = 10_000;

        public int SplitSlots { get; init; } = 3;

        public decimal[] SplitCoefs { get; init; } = [0.4m, 0.4m, 0.2m];

        public RegistrySnapshotSource Build()
        {
            Def(Stream, "STREAM", false, ("LEGACY_ID", CellDataType.Int, null), ("IS_ONSHORE", CellDataType.Bool, null));
            Def(Case, "STREAM_CASE", true, ("STREAM", CellDataType.Lookup, Stream), ("CASE_NAME", CellDataType.String, null),
                ("T_C", CellDataType.Decimal, null), ("NO_COMPOSITION", CellDataType.Bool, null), ("LEGACY_COMPOSITION_ID", CellDataType.Int, null));
            Key(Case, "STREAM", "CASE_NAME");
            Def(Component, "COMPONENT", false);
            Def(Composition, "GAS_COMPOSITION", false, ("CASE", CellDataType.Lookup, Case), ("COMPONENT", CellDataType.Lookup, Component),
                ("MOL_PCT", CellDataType.Decimal, null));
            Key(Composition, "CASE", "COMPONENT");
            Def(Location, "FLARE_LOCATION", false, ("IS_ONSHORE", CellDataType.Bool, null));
            Def(Pin, "TOX_GAS_STREAM", true, ("COLUMN", CellDataType.String, null), ("TRAIN", CellDataType.String, null),
                ("STREAM", CellDataType.Lookup, Stream), ("CASE_SOR", CellDataType.String, null), ("CASE_EOR", CellDataType.String, null));
            Key(Pin, "COLUMN", "TRAIN");
            Def(Split, "LP_SOUR_SPLIT", true, ("SLOT", CellDataType.Int, null), ("STREAM", CellDataType.Lookup, Stream),
                ("COEF", CellDataType.Decimal, null), ("CASE_SUMMER", CellDataType.String, null), ("CASE_WINTER", CellDataType.String, null));
            Key(Split, "SLOT");

            var components = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var code in (string[])["H2S", "Methane", "COS", "CO"])
            {
                components[code] = Entry(Component, code);
            }

            var s26 = Entry(Stream, "S26", ("LEGACY_ID", 26m), ("IS_ONSHORE", true));
            var s19 = Entry(Stream, "S19", ("LEGACY_ID", 19m), ("IS_ONSHORE", false));
            var s574 = Entry(Stream, "S574", ("LEGACY_ID", 574m), ("IS_ONSHORE", true));
            var s573 = Entry(Stream, "S573", ("LEGACY_ID", 573m), ("IS_ONSHORE", true));
            var s575 = Entry(Stream, "S575", ("LEGACY_ID", 575m), ("IS_ONSHORE", true));

            var boundary = new DateOnly(2099, 1, 15);
            CaseWith(s26, "KE01", null, boundary, 30m, components, ("H2S", 10m), ("Methane", 80m));
            CaseWith(s26, "KE01", boundary, null, null, components, ("H2S", 20m), ("Methane", 70m));
            CaseWith(s26, "No composition", null, null, 30m, components, ("H2S", 1m)).NoComposition();
            CaseWith(s26, "Empty", null, null, 30m, components);
            CaseWith(s19, "370 Summer", null, null, 30m, components, ("H2S", 6m)).Fcid(996);
            CaseWith(s19, "370 Winter", null, null, 30m, components, ("H2S", 5m)).Fcid(997);
            CaseWith(s574, "481A SOR", null, null, null, components, ("H2S", 41m), ("Methane", 50m));
            CaseWith(s575, "417 SOR", null, null, null, components, ("H2S", 17m), ("Methane", 70m));
            CaseWith(s575, "417 EOR", null, null, null, components, ("H2S", 18m));
            CaseWith(s574, "481A EOR", null, null, null, components, ("H2S", 42m), ("Methane", 58m));
            CaseWith(s573, "481B SOR", null, null, null, components, ("H2S", 31m), ("Methane", 69m));
            CaseWith(s573, "481B EOR", null, null, null, components, ("H2S", 32m), ("Methane", 68m));

            // Огляд L3: місця й закріплення — з тих самих сталих, що пише провізія (FlaringRegistryProvisioner), без дублювання.
            foreach (var (code, _, onshore, _, _) in Ecr.Infrastructure.Jobs.FlaringRegistryProvisioner.Locations)
            {
                Entry(Location, code, code switch { "ONSHORE_HP" => OnshoreHp, "D_ISLAND_HP" => DIslandHp, _ => (long?)null }, ("IS_ONSHORE", onshore));
            }

            var pinStreams = new Dictionary<int, (long Entry, string Case)> { [574] = (s574, "481A"), [573] = (s573, "481B"), [575] = (s575, "417") };
            foreach (var (column, train, streamId) in Ecr.Infrastructure.Jobs.FlaringRegistryProvisioner.Pins)
            {
                if (pinStreams.TryGetValue(streamId, out var pin) && (column != "417" || train == "Train 1"))
                {
                    Entry(Pin, Ecr.Infrastructure.Jobs.FlaringRegistryProvisioner.PinCode(column, train), null, ("COLUMN", column), ("TRAIN", train),
                        ("STREAM", new Ref(pin.Entry)), ("CASE_SOR", pin.Case + " SOR"), ("CASE_EOR", pin.Case + " EOR"));
                }
            }

            var coefs = SplitCoefs;
            for (var slot = 1; slot <= SplitSlots; slot++)
            {
                Entry(Split, $"SLOT{slot}", null, ("SLOT", (decimal)slot), ("STREAM", new Ref(s19)), ("COEF", coefs[slot - 1]),
                    ("CASE_SUMMER", "370 Summer"), ("CASE_WINTER", "370 Winter"));
            }

            return new RegistrySnapshotSource(_defs, _keys, _entries, _values);
        }

        private CaseBuilder CaseWith(
            long stream, string name, DateOnly? from, DateOnly? to, decimal? tc, Dictionary<string, long> components,
            params (string Component, decimal Mol)[] rows)
        {
            var id = Entry(Case, $"C{_next}", null, ("STREAM", new Ref(stream)), ("CASE_NAME", name), ("T_C", tc));
            _entries.Last().SetValidity(from, to);
            foreach (var (component, mol) in rows)
            {
                Entry(Composition, $"R{_next}", null, ("CASE", new Ref(id)), ("COMPONENT", new Ref(components[component])), ("MOL_PCT", mol));
            }

            return new CaseBuilder(this, id);
        }

        private sealed record Ref(long Id);

        private sealed class CaseBuilder(Registries owner, long id)
        {
            public void NoComposition() => owner._values.Add(new(id, owner._fields[(Case, "NO_COMPOSITION")], null, null, null, true, null, null, null));

            public void Fcid(int value) => owner._values.Add(new(id, owner._fields[(Case, "LEGACY_COMPOSITION_ID")], value, null, null, null, null, null, null));
        }

        private void Def(int id, string code, bool temporal, params (string Code, CellDataType Type, int? Target)[] fields)
        {
            var def = new RegistryDef(EcrCode.Create(code), Text(code), temporal);
            typeof(Entity<int>).GetProperty("Id")!.SetValue(def, id);
            var ordinal = 0;
            foreach (var (fieldCode, type, target) in fields)
            {
                var fieldId = (id * 100) + (++ordinal);
                var field = new RegistryFieldDef(id, EcrCode.Create(fieldCode), Text(fieldCode), type, ordinal);
                typeof(Entity<int>).GetProperty("Id")!.SetValue(field, fieldId);
                if (target is { } t)
                {
                    field.PointTo(t);
                    if (code == "GAS_COMPOSITION" && fieldCode == "CASE")
                    {
                        field.ComposeInto(ParentDeletePolicy.Cascade);
                    }
                }

                def.AddField(field);
                _fields[(id, fieldCode)] = fieldId;
            }

            _defs.Add(def);
        }

        private void Key(int registry, params string[] fieldCodes)
        {
            var def = _defs.Single(d => d.Id == registry);
            var fields = fieldCodes.Select(c => def.Fields.Single(f => f.Code == c)).ToList();
            foreach (var field in fields)
            {
                field.Update(Text(field.Code), field.Ordinal, isRequired: true);
            }

            _keys.Add(new RegistryKeyDef(registry, EcrCode.Create("PK"), Text("PK"), fields, isPrimary: true, ignoreCase: false, 1, DateTime.UnixEpoch));
        }

        private long Entry(int registry, string code, params (string Field, object? Value)[] values) => Entry(registry, code, null, values);

        private long Entry(int registry, string code, long? fixedId, params (string Field, object? Value)[] values)
        {
            var id = fixedId ?? _next++;
            var entry = new RegistryEntry(registry, EcrCode.Create(code), Text(code));
            typeof(Entity<long>).GetProperty("Id")!.SetValue(entry, id);
            _entries.Add(entry);
            foreach (var (field, value) in values)
            {
                var fieldId = _fields[(registry, field)];
                _values.Add(value switch
                {
                    null => new(id, fieldId, null, null, null, null, null, null, null),
                    Ref r => new(id, fieldId, null, null, null, null, r.Id, null, null),
                    bool b => new(id, fieldId, null, null, null, b, null, null, null),
                    string s => new(id, fieldId, null, s, null, null, null, null, null),
                    decimal d => new(id, fieldId, d, null, null, null, null, null, null),
                    _ => throw new ArgumentException(field),
                });
            }

            return id;
        }

        private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });
    }

    private sealed class SourceLoader(RegistrySnapshotSource source) : IRegistrySnapshotLoader
    {
        public Task<IRegistrySnapshot> LoadAsync(
            IReadOnlyCollection<int> registryDefIds, DateOnly businessDate, DateTime? registryAsOfUtc, CancellationToken ct)
            => Task.FromResult(source.Build(businessDate));

        public Task<IRegistrySnapshotSource> LoadSourceAsync(
            IReadOnlyCollection<int> registryDefIds, DateTime? registryAsOfUtc, CancellationToken ct)
            => Task.FromResult<IRegistrySnapshotSource>(source);
    }
}
