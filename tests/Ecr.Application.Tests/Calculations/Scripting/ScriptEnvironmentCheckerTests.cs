using Ecr.Application.Calculations.Scripting;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Scripting;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations.Scripting;

/// <summary>
/// Перевірки скрипта, яким потрібна база (MS T3.1): одиниці, речовини, таблиці, колонки, записи довідників,
/// читання результатів інших методологій, цикл між методологіями, одиниці виразів.
/// </summary>
public sealed class ScriptEnvironmentCheckerTests
{
    private static async Task<ScriptEnvironment> CheckAsync(
        string text, ScriptEnvironmentChecker checker, int methodologyId = 1, bool isLibrary = false)
    {
        var compilation = ScriptTestKit.Compile(text, isLibrary);
        Assert.NotNull(compilation.Script);
        Assert.Empty(compilation.Diagnostics);
        return await checker.CheckAsync(compilation.Script, text, methodologyId, CancellationToken.None);
    }

    private static string[] Codes(ScriptEnvironment environment) => [.. environment.Diagnostics.Select(d => d.Code)];

    private static ScriptEnvironmentChecker Checker(params LiveTable[] tables)
        => ScriptTestKit.Checker(ScriptTestKit.Store(tables));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Чистий_скрипт_без_зауважень_віддає_розв_язані_таблиці()
    {
        var table = ScriptTestKit.Table("T1", [ScriptTestKit.Column(1, "Hours"), ScriptTestKit.Column(2, "Status")]);

        var environment = await CheckAsync(
            "rows from T1 where row.Status == \"A\";\nvar h = row.Hours;\noutput OUT_A : t = h;",
            Checker(table));

        Assert.Empty(environment.Diagnostics);
        Assert.Equal(table.TableDefId, Assert.Single(environment.RowTables).TableDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Невідома_одиниця_дає_unknownUnit_з_позицією()
    {
        var table = ScriptTestKit.Table("T1", [ScriptTestKit.Column(1, "Hours")]);

        var environment = await CheckAsync("rows from T1;\noutput OUT_A : no_such_unit = 1;", Checker(table));

        var diagnostic = Assert.Single(environment.Diagnostics, d => d.Code == ScriptDiagnosticCodes.UnknownUnit);
        Assert.Equal(2, diagnostic.Line);
        Assert.Equal("no_such_unit", diagnostic.Params["unit"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Невідома_і_неоднозначна_таблиці()
    {
        var a = ScriptTestKit.Table("DUP", [ScriptTestKit.Column(1, "X")], tableId: 1, template: "TA", templateId: 1);
        var b = ScriptTestKit.Table("DUP", [ScriptTestKit.Column(2, "X")], tableId: 2, template: "TB", templateId: 2);

        var unknown = await CheckAsync("rows from MISSING;\noutput O : t = 1;", Checker(a, b));
        Assert.Equal([ScriptDiagnosticCodes.UnknownTable], Codes(unknown));

        var ambiguous = await CheckAsync("rows from DUP;\noutput O : t = 1;", Checker(a, b));
        Assert.Equal([ScriptDiagnosticCodes.AmbiguousTable], Codes(ambiguous));
        Assert.Equal("TA, TB", ambiguous.Diagnostics[0].Params["templates"]);

        // З кодом шаблону таблиця однозначна.
        var qualified = await CheckAsync("rows from TB.DUP;\noutput O : t = 1;", Checker(a, b));
        Assert.Empty(qualified.Diagnostics);
        Assert.Equal(2, Assert.Single(qualified.RowTables).TableDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Невідома_колонка_в_where_і_в_обовязковому_полі_але_не_в_необовязковому_і_не_в_похідному()
    {
        var table = ScriptTestKit.Table("T1", [ScriptTestKit.Column(1, "Hours")]);

        var environment = await CheckAsync(
            """
            rows from T1 where row.NoSuchColumn == "A";
            var a = row.Hours;
            var b = row.Missing;
            var c = row.OptionalMissing?;
            var d = row.Total;
            output O : t = a;
            """,
            Checker(table));

        Assert.Equal(
            [ScriptDiagnosticCodes.UnknownColumn, ScriptDiagnosticCodes.UnknownColumn],
            Codes(environment));
        Assert.Equal(["NoSuchColumn", "Missing"], environment.Diagnostics.Select(d => d.Params["column"]));
        Assert.Equal(1, environment.Diagnostics[0].Line);
        Assert.Equal(3, environment.Diagnostics[1].Line);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Місячні_входи_розв_язуються_за_псевдонімом_або_колонкою_X_Mmm()
    {
        var table = ScriptTestKit.Table(
            "T1",
            [
                ScriptTestKit.Column(1, "Base"),
                ScriptTestKit.Column(2, "Fuel_M01", month: 1),
                ScriptTestKit.Column(3, "Fuel_M02", month: 2),
            ],
            aliases: "Hours");

        var good = await CheckAsync(
            "rows from T1;\nvar a = row.Hours[period.Month];\nvar b = row.Fuel[period.Month];\noutput O : t = a + b;",
            Checker(table));
        Assert.Empty(good.Diagnostics);

        var bad = await CheckAsync(
            "rows from T1;\nvar a = row.Nothing[period.Month];\noutput O : t = a;",
            Checker(table));
        Assert.Equal([ScriptDiagnosticCodes.UnknownColumn], Codes(bad));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Колонки_таблиці_за_псевдонімом_перевіряються_у_своїй_таблиці()
    {
        var rows = ScriptTestKit.Table("T1", [ScriptTestKit.Column(1, "Unit")], tableId: 1);
        var other = ScriptTestKit.Table("T2", [ScriptTestKit.Column(2, "Amount"), ScriptTestKit.Column(3, "Land_Unit")], tableId: 2);

        var good = await CheckAsync(
            "rows from T1;\ntable Src = T2;\nvar s = Sum(Src.Amount where Src.Land_Unit == row.Unit);\noutput O : t = s;",
            Checker(rows, other));
        Assert.Empty(good.Diagnostics);

        var bad = await CheckAsync(
            "rows from T1;\ntable Src = T2;\nvar s = Sum(Src.NoColumn);\noutput O : t = s;",
            Checker(rows, other));
        var diagnostic = Assert.Single(bad.Diagnostics);
        Assert.Equal(ScriptDiagnosticCodes.UnknownColumn, diagnostic.Code);
        Assert.Equal("NoColumn", diagnostic.Params["column"]);

        var missingTable = await CheckAsync(
            "rows from T1;\ntable Src = NOPE;\nvar s = Sum(Src.Amount);\noutput O : t = s;",
            Checker(rows));
        Assert.Contains(ScriptDiagnosticCodes.UnknownTable, Codes(missingTable));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Невідомі_речовина_і_запис_довідника()
    {
        var table = ScriptTestKit.Table("T1", [ScriptTestKit.Column(1, "Hours"), ScriptTestKit.Column(2, "Fuel")]);
        var store = ScriptTestKit.Store(table);
        store.FindSubstanceEntryIdsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long> { ["301"] = 5 }));

        var registries = Substitute.For<IRegistryStore>();
        var definition = ScriptTestKit.WithId(
            new RegistryDef(Domain.ValueObjects.EcrCode.Create("REG_A"), new Domain.ValueObjects.LocalizedText(new Dictionary<string, string> { ["en"] = "a" }), false), 7);
        registries.FindDefinitionAsync("REG_A", Arg.Any<CancellationToken>()).Returns(definition);
        registries.FindDefinitionAsync("REG_NONE", Arg.Any<CancellationToken>()).Returns(Task.FromResult<RegistryDef?>(null));
        registries.FindEntryByCodeAsync(7, "GOOD", Arg.Any<CancellationToken>())
            .Returns(new Domain.Entities.Dictionaries.RegistryEntry(7, Domain.ValueObjects.EcrCode.Create("GOOD"), definition.NameL10n));
        registries.FindEntryByCodeAsync(7, "BAD", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Domain.Entities.Dictionaries.RegistryEntry?>(null));

        var checker = ScriptTestKit.Checker(store, registries: registries);
        var environment = await CheckAsync(
            """
            rows from T1 where row.Fuel == entry("REG_A", "GOOD") || row.Fuel == entry("REG_A", "BAD") || row.Fuel == entry("REG_NONE", "X");
            foreach substance S in ("301", "999") {
                output O per S : t = 1;
            }
            """,
            checker);

        Assert.Equal(
            [ScriptDiagnosticCodes.UnknownRegistryEntry, ScriptDiagnosticCodes.UnknownRegistryEntry, ScriptDiagnosticCodes.UnknownSubstance],
            Codes(environment).OrderBy(c => c == ScriptDiagnosticCodes.UnknownSubstance ? 1 : 0).ToArray());
        var substance = Assert.Single(environment.Diagnostics, d => d.Code == ScriptDiagnosticCodes.UnknownSubstance);
        Assert.Equal("999", substance.Params["code"]);
        Assert.Equal(2, substance.Line);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Код_виходу_що_не_є_кодом_сутності_дає_invalidOutputCode()
    {
        var table = ScriptTestKit.Table("T1", [ScriptTestKit.Column(1, "Hours")]);

        var environment = await CheckAsync("rows from T1;\noutput _bad : t = 1;", Checker(table));

        var diagnostic = Assert.Single(environment.Diagnostics);
        Assert.Equal(ScriptEnvironmentCodes.InvalidOutputCode, diagnostic.Code);
        Assert.Equal(2, diagnostic.Line);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Колонка_Calculated_без_активної_прив_язки_це_unknownColumn()
    {
        var table = ScriptTestKit.Table(
            "T1", [ScriptTestKit.Column(1, "Hours"), ScriptTestKit.Column(2, "Result", CellDataType.Calculated)]);

        var environment = await CheckAsync(
            "rows from T1;\nvar r = row.Result;\noutput O : t = r;", Checker(table));

        var diagnostic = Assert.Single(environment.Diagnostics);
        Assert.Equal(ScriptDiagnosticCodes.UnknownColumn, diagnostic.Code);
        Assert.Equal("noActiveBinding", diagnostic.Params["reason"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Читання_власного_виходу_дає_readsOwnOutput_а_чужого_реєструє_джерело()
    {
        var table = ScriptTestKit.Table(
            "T1",
            [
                ScriptTestKit.Column(1, "Hours"),
                ScriptTestKit.Column(2, "Own", CellDataType.Calculated),
                ScriptTestKit.Column(3, "Foreign", CellDataType.Calculated),
            ]);
        var store = ScriptTestKit.Store(table);
        store.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ColumnResultBinding>>(
            [
                new ColumnResultBinding(100, 2, 1, "OWN_OUT", "{}", [11]),
                new ColumnResultBinding(100, 3, 7, "FOREIGN_OUT", "{}", [70]),
            ]));
        var checker = ScriptTestKit.Checker(store);

        var own = await CheckAsync("rows from T1;\nvar r = row.Own;\noutput O : t = r;", checker, methodologyId: 1);
        Assert.Equal([ScriptEnvironmentCodes.ReadsOwnOutput], Codes(own));

        var foreign = await CheckAsync("rows from T1;\nvar r = row.Foreign;\noutput O : t = r;", checker, methodologyId: 1);
        Assert.Empty(foreign.Diagnostics);
        Assert.Equal([7], foreign.ResultSourceMethodologyIds);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Цикл_між_методологіями_через_залежності_дає_resultCycle_з_шляхом()
    {
        var table = ScriptTestKit.Table(
            "T1", [ScriptTestKit.Column(1, "Hours"), ScriptTestKit.Column(2, "Foreign", CellDataType.Calculated)]);
        var store = ScriptTestKit.Store(table);
        store.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ColumnResultBinding>>(
                [new ColumnResultBinding(100, 2, 7, "B_OUT", "{}", [70])]));

        // B (7) залежить від C (8), а C — від A (1): A → B → C → A.
        store.GetMethodologyDependenciesAsync(
                Arg.Is<IReadOnlyCollection<int>>(x => x.Contains(7)), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>(new Dictionary<int, IReadOnlyList<int>> { [7] = [8] }));
        store.GetMethodologyDependenciesAsync(
                Arg.Is<IReadOnlyCollection<int>>(x => x.Contains(8)), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>(new Dictionary<int, IReadOnlyList<int>> { [8] = [1] }));

        var drafts = Substitute.For<IMethodologyDraftStore>();
        foreach (var (id, code) in new[] { (1, "ALPHA"), (7, "BETA"), (8, "GAMMA") })
        {
            drafts.FindAsync(id, Arg.Any<CancellationToken>()).Returns(ScriptTestKit.NewMethodology(code, id));
        }

        var environment = await CheckAsync(
            "rows from T1;\nvar r = row.Foreign;\noutput O : t = r;",
            ScriptTestKit.Checker(store, drafts),
            methodologyId: 1);

        var diagnostic = Assert.Single(environment.Diagnostics);
        Assert.Equal(ScriptEnvironmentCodes.ResultCycle, diagnostic.Code);
        Assert.Equal("ALPHA -> BETA -> GAMMA -> ALPHA", diagnostic.Params["path"]);
    }

    // --- одиниці виразів (13.4): дзеркало перевірки одиниць методологій ---

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    [InlineData("const a : kg = 1;\nconst b : t = 2;\nvar x = a + b;", true)]                    // додавання кг + т без CONVERT
    [InlineData("const a : kg = 1;\nconst b : m3 = 2;\nvar x = a + b;", true)]                   // різні розмірності
    [InlineData("const a : kg = 1;\nvar x : m3 = a;", true)]                                      // оголошена інша розмірність
    [InlineData("const a : kg = 1;\nvar x : t = a;", false)]                                      // та сама розмірність (т замість кг) не блокує
    [InlineData("const a : kg = 1;\nconst b : kg = 2;\nvar x = a + b;", false)]                   // однакові одиниці
    [InlineData("const a : kg = 1;\nvar x = a * 1000;", false)]                                   // літерал безрозмірний
    [InlineData("const a : kg = 1;\nconst b : m3 = 2;\nvar x = a * b;", false)]                   // добуток розмірних не блокує
    [InlineData("const a : kg = 1;\nvar x = a + row.Hours;", false)]                              // аргумент рядка — одиниця невідома
    [InlineData("const a : kg = 1;\nconst EF = { A: 1 };\nvar x = a * EF.A;\noutput O : g_per_s = x;", false)] // пошук у мапі — невідоме
    [InlineData("const a : kg = 1;\nvar x = CONVERT(a, 'kg', 't');\nvar y : t = x;", false)]       // CONVERT дає цільову одиницю
    [InlineData("const a : kg = 1;\nvar x = CONVERT(a, 'kg', 'm3');", true)]                      // CONVERT між розмірностями
    public async Task Одиниці_виразів_блокують_рівно_те_що_блокує_перевірка_формул(string body, bool blocks)
    {
        var table = ScriptTestKit.Table("T1", [ScriptTestKit.Column(1, "Hours")]);

        var environment = await CheckAsync("rows from T1;\n" + body + "\noutput OUT : t = 1;", Checker(table));

        Assert.Equal(blocks, environment.Diagnostics.Any(d => d.Code == ScriptDiagnosticCodes.UnitMismatch));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Розбіжність_одиниць_має_позицію_оператора_і_ключ_причини()
    {
        var table = ScriptTestKit.Table("T1", [ScriptTestKit.Column(1, "Hours")]);

        var environment = await CheckAsync(
            "rows from T1;\nconst a : kg = 1;\nconst b : t = 2;\nvar x = a + b;\noutput OUT : t = x;", Checker(table));

        var diagnostic = Assert.Single(environment.Diagnostics, d => d.Code == ScriptDiagnosticCodes.UnitMismatch);
        Assert.Equal(4, diagnostic.Line);
        Assert.Equal("expr.unit.addNeedsConvert", diagnostic.Params["reason"]);
        Assert.Equal("x", diagnostic.Params["name"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Змінна_без_одиниці_з_невідомим_у_виразі_не_дає_хибної_відмови()
    {
        // `fuel` невідома одиниця (аргумент рядка): множення на розмірну константу не повинно вважатися
        // добутком із «безрозмірним», інакше вихід у g_per_s хибно відхилився б.
        var table = ScriptTestKit.Table("T1", [ScriptTestKit.Column(1, "Fuel")]);

        var environment = await CheckAsync(
            "rows from T1;\nconst k : kg = 1;\nvar fuel = row.Fuel;\noutput OUT : g_per_s = fuel * k;", Checker(table));

        Assert.DoesNotContain(environment.Diagnostics, d => d.Code == ScriptDiagnosticCodes.UnitMismatch);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Бібліотека_без_rows_перевіряється_без_таблиць()
    {
        var environment = await CheckAsync(
            "const k : kg = 1;\nfunction twice(x) = x * 2;", Checker(), isLibrary: true);

        Assert.Empty(environment.Diagnostics);
        Assert.Empty(environment.RowTables);
    }
}
