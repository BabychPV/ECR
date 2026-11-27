using Ecr.Application.Calculations.Scripting;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Scripting;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations.Scripting;

/// <summary>Матеріалізація скрипта в «дитячі» таблиці версії й автоприв'язка виходів (MS T3.1).</summary>
public sealed class ScriptMaterializerTests
{
    private const int MethodologyId = 5;
    private const int VersionId = 50;
    private static readonly long[] SubstanceIds = [304, 301];
    private static readonly int[] ImportIds = [9];
    private static readonly string[] ReadTables = ["T2"];
    private static readonly int[] Dependencies = [9, 77];
    private static readonly int[] OnlyForeign = [77];

    private readonly IMethodologyStore _store = ScriptTestKit.Store();
    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly ICalculationBindingStore _bindings = Substitute.For<ICalculationBindingStore>();
    private readonly List<CalculationBinding> _added = [];
    private List<CalculationBinding> _existing = [];

    public ScriptMaterializerTests()
    {
        _bindings.ListAsync(MethodologyId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<CalculationBinding>>(_existing));
        _bindings.When(b => b.Add(Arg.Any<CalculationBinding>())).Do(call => _added.Add(call.Arg<CalculationBinding>()));
    }

    private ScriptMaterializer Materializer() => new(_store, _drafts, ScriptTestKit.Catalog(), _bindings);

    private static (CompiledScript Script, ScriptEnvironment Environment) Prepare(string text, params LiveTable[] tables)
    {
        var compilation = ScriptTestKit.Compile(text);
        Assert.Empty(compilation.Diagnostics);
        return (compilation.Script!, new ScriptEnvironment([], tables, []));
    }

    private static LiveTable Table() => ScriptTestKit.Table(
        "T1",
        [
            ScriptTestKit.Column(1, "Hours"),
            ScriptTestKit.Column(10, "OUT_A", CellDataType.Calculated),
            ScriptTestKit.Column(11, "OUT_B_M01", CellDataType.Calculated, month: 1),
            ScriptTestKit.Column(12, "OUT_B_M02", CellDataType.Calculated, month: 2),
            ScriptTestKit.Column(13, "OUT_B_M13", CellDataType.Calculated),   // не місячна колонка — суфікс не рахується
            ScriptTestKit.Column(14, "OUT_C", CellDataType.Decimal),          // не Calculated
        ]);

    private const string Text =
        "rows from T1;\nvar h = row.Hours;\noutput OUT_A : t = h;\noutput OUT_B : kg = h;\noutput OUT_C : m3 = h;";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Автоприв_язка_додає_лише_відсутні_колонка_дорівнює_виходу_або_вихід_M_mm()
    {
        var (script, environment) = Prepare(Text, Table());

        var result = await Materializer().MaterializeAsync(
            VersionId, MethodologyId, script, environment, new ScriptMaterializeOptions(Publish: true), default);

        Assert.Equal(3, result.AddedBindings);
        Assert.Equal(
            [(10, "OUT_A"), (11, "OUT_B"), (12, "OUT_B")],
            _added.Select(b => (b.ColumnDefId, b.OutputCode)).OrderBy(x => x.Item1));
        Assert.All(_added, b =>
        {
            Assert.Equal("{}", b.MatchJson);
            Assert.True(b.IsActive);
            Assert.Equal(MethodologyId, b.MethodologyId);
            Assert.Equal(100, b.TableDefId);
        });

        // OUT_C: колонка не Calculated — прив'язки немає, а значить вихід «ні в що не пишеться».
        Assert.Equal(["OUT_A", "OUT_B"], result.BoundOutputCodes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Другий_прогін_нічого_не_додає_і_наявні_прив_язки_не_змінюються()
    {
        var (script, environment) = Prepare(Text, Table());

        // Усе вже прив'язано; одна з прив'язок вимкнена вручну й так і лишається.
        _existing =
        [
            new CalculationBinding(100, 10, MethodologyId, "OUT_A", "{\"x\":1}"),
            new CalculationBinding(100, 11, MethodologyId, "out_b", "{}"),
            new CalculationBinding(100, 12, MethodologyId, "OUT_B", "{}"),
        ];
        _existing[2].Update("{}", isActive: false);

        var result = await Materializer().MaterializeAsync(
            VersionId, MethodologyId, script, environment, new ScriptMaterializeOptions(Publish: true), default);

        Assert.Equal(0, result.AddedBindings);
        Assert.Empty(_added);
        Assert.Equal("{\"x\":1}", _existing[0].MatchJson);
        Assert.False(_existing[2].IsActive);
        Assert.Equal(["OUT_A", "OUT_B"], result.BoundOutputCodes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Збереження_чернетки_матеріалізує_виходи_але_не_чіпає_прив_язки_й_залежності()
    {
        var (script, environment) = Prepare(Text, Table());

        var result = await Materializer().MaterializeAsync(
            VersionId, MethodologyId, script, environment, new ScriptMaterializeOptions(Publish: false), default);

        Assert.Equal(0, result.AddedBindings);
        Assert.Empty(_added);
        await _store.Received(1).ReplaceScriptOutputsAsync(
            VersionId,
            Arg.Is<IReadOnlyList<ScriptOutputRow>>(rows =>
                rows.Count == 3
                && rows[0].Code == "OUT_A" && rows[0].UnitId == ScriptTestKit.Tonne && rows[0].Ordinal == 1 && !rows[0].IsPerSubstance
                && rows[1].Code == "OUT_B" && rows[1].UnitId == ScriptTestKit.Kg && rows[1].Ordinal == 2),
            Arg.Any<CancellationToken>());
        await _store.Received(1).ReplaceScriptReadTablesAsync(VersionId, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().ReplaceDependenciesAsync(Arg.Any<int>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Речовини_імпорти_таблиці_читання_і_залежності_записуються()
    {
        var table = ScriptTestKit.Table("T1", [ScriptTestKit.Column(1, "Hours"), ScriptTestKit.Column(2, "Amount")]);

        var library = ScriptTestKit.NewMethodology("COMMON", 9, MethodologyKind.Library);
        _drafts.FindByCodeAsync("COMMON", Arg.Any<CancellationToken>()).Returns(library);
        _store.FindSubstanceCandidatesAsync(Arg.Any<IReadOnlyCollection<string>>(), MethodologyId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SubstanceCandidate>>(
                [new SubstanceCandidate("301", 301, "E301", "SUBST", false), new SubstanceCandidate("304", 304, "E304", "SUBST", false)]));

        var libraryCompiled = ScriptTestKit.Compile("const k = 1;", isLibrary: true).Script!;
        var text = "import COMMON;\nrows from T1;\ntable Src = T2;\nvar total = Sum(Src.Amount);\n"
                   + "foreach substance S in (\"304\", \"301\") {\n output OUT_A per S : t = total * COMMON.k;\n}";
        var compilation = ScriptCompiler.Compile(
            text, new ScriptCompileOptions(NumericMode.Strict, false, name => name == "COMMON" ? libraryCompiled : null));
        Assert.Empty(compilation.Diagnostics);

        // Джерело результатів 77 — із перевірки оточення.
        var environment = new ScriptEnvironment([], [table], [77]);
        var result = await Materializer().MaterializeAsync(
            VersionId, MethodologyId, compilation.Script!, environment, new ScriptMaterializeOptions(Publish: true), default);

        await _store.Received(1).ReplaceScriptSubstancesAsync(
            VersionId, Arg.Is<IReadOnlyList<long>>(ids => ids.SequenceEqual(SubstanceIds)), Arg.Any<CancellationToken>());
        await _store.Received(1).ReplaceScriptImportsAsync(
            VersionId, Arg.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(ImportIds)), Arg.Any<CancellationToken>());
        await _store.Received(1).ReplaceScriptReadTablesAsync(
            VersionId, Arg.Is<IReadOnlyList<string>>(codes => codes.SequenceEqual(ReadTables)), Arg.Any<CancellationToken>());
        await _store.Received(1).ReplaceDependenciesAsync(
            MethodologyId, Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(Dependencies)), Arg.Any<CancellationToken>());
        Assert.Equal([9, 77], result.DependencyMethodologyIds);
    }

    /// <summary>
    /// c11-10: код речовини («333») є в КІЛЬКОХ довідниках речовин (LAND_HSE400_SUBSTANCE C333 і LAND_AIR_SUBSTANCE E333), і
    /// глобальний пошук бере запис із меншим Id. Клон мігрованої версії вже має речовини свого довідника (E333…), тож збереження
    /// його тексту не може пересадити їх на чужий довідник: виходи писалися б з іншими SubstanceEntryId (золотий набір V5
    /// ECW_C11_10 на копії live — «actual null» на кожній речовині, звіти за речовиною — порожні).
    /// </summary>
    /// <remarks>Мутація: брати лише глобальний пошук (прибрати перевагу наявних речовин) — червоніє.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Збереження_тексту_версії_лишає_речовини_її_довідника_а_не_перший_збіг_коду()
    {
        _store.GetSubstanceCodesAsync(VersionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<long, string>>(
                new Dictionary<long, string> { [6044] = "333", [6051] = "415" }));
        _store.FindSubstanceCandidatesAsync(Arg.Any<IReadOnlyCollection<string>>(), MethodologyId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SubstanceCandidate>>(
            [
                new SubstanceCandidate("333", 642, "C333", "LAND_HSE400_SUBSTANCE", false),
                new SubstanceCandidate("333", 6044, "E333", "LAND_AIR_SUBSTANCE", false),
                new SubstanceCandidate("415", 651, "C415", "LAND_HSE400_SUBSTANCE", false),
                new SubstanceCandidate("415", 6051, "E415", "LAND_AIR_SUBSTANCE", false),
                new SubstanceCandidate("416", 652, "C416", "LAND_HSE400_SUBSTANCE", false),
            ]));
        var (script, environment) = Prepare(
            "rows from T1;\nforeach substance S in (\"333\", \"415\", \"416\") {\n output OUT_A per S : t = row.Hours;\n}", Table());

        await Materializer().MaterializeAsync(
            VersionId, MethodologyId, script, environment, new ScriptMaterializeOptions(Publish: false), default);

        // Наявні коди — з довідника версії; новий код (416) — глобальним пошуком, як і раніше.
        await _store.Received(1).ReplaceScriptSubstancesAsync(
            VersionId, Arg.Is<IReadOnlyList<long>>(ids => ids.SequenceEqual(KeptSubstanceIds)), Arg.Any<CancellationToken>());
    }

    private static readonly long[] KeptSubstanceIds = [6044, 6051, 652];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Власна_методологія_не_потрапляє_у_власні_залежності_ні_при_публікації_ні_при_перемиканні()
    {
        var (script, _) = Prepare(Text, Table());

        // Власний id серед джерел результатів (дубль 77 теж) — ребра «сама на себе» бути не може: це цикл.
        var environment = new ScriptEnvironment([], [Table()], [MethodologyId, 77, 77]);

        var published = await Materializer().MaterializeAsync(
            VersionId, MethodologyId, script, environment, new ScriptMaterializeOptions(Publish: true), default);
        await Materializer().WriteForSwitchAsync(VersionId, MethodologyId, script, environment, default);

        Assert.Equal([77], published.DependencyMethodologyIds);
        await _store.Received(2).ReplaceDependenciesAsync(
            MethodologyId, Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(OnlyForeign)), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-13.9")]
    public async Task Ребра_довідників_пишуться_при_публікації_і_при_перемиканні_але_не_при_збереженні_чернетки()
    {
        var registries = Substitute.For<IRegistryStore>();
        var uses = Substitute.For<IRegistryUseStore>();
        var definition = ScriptTestKit.WithId(
            new Ecr.Domain.Entities.Configuration.RegistryDef(
                Ecr.Domain.ValueObjects.EcrCode.Create("REG"),
                new Ecr.Domain.ValueObjects.LocalizedText(new Dictionary<string, string> { ["en"] = "reg" }),
                isTemporal: false),
            33);
        registries.FindDefinitionAsync("REG", Arg.Any<CancellationToken>()).Returns(definition);
        var materializer = new ScriptMaterializer(_store, _drafts, ScriptTestKit.Catalog(), _bindings, registries, uses);

        var compilation = ScriptTestKit.Compile(
            "rows from T1\n    where row.Hours == entry(\"REG\", \"X\");\noutput OUT_A : t = row.Hours;");
        Assert.Empty(compilation.Diagnostics);
        var script = compilation.Script!;
        var environment = new ScriptEnvironment([], [Table()], []);

        await materializer.MaterializeAsync(
            VersionId, MethodologyId, script, environment, new ScriptMaterializeOptions(Publish: false), default);
        await uses.DidNotReceiveWithAnyArgs().ReplaceMethodologyUsesAsync(default, default!, default);

        await materializer.MaterializeAsync(
            VersionId, MethodologyId, script, environment, new ScriptMaterializeOptions(Publish: true), default);
        await materializer.WriteForSwitchAsync(VersionId, MethodologyId, script, environment, default);

        await uses.Received(2).ReplaceMethodologyUsesAsync(
            VersionId,
            Arg.Is<IReadOnlyCollection<Ecr.Domain.Entities.Configuration.RegistryUse>>(u =>
                u.Count == 1
                && u.Single().RegistryDefId == 33
                && u.Single().SourceId == VersionId
                && u.Single().FormulaCode == ScriptMaterializer.RegistryUseSource),
            Arg.Any<CancellationToken>());
    }
}
