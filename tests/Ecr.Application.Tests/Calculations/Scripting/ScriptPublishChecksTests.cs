using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Scripting;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Scripting;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations.Scripting;

/// <summary>Перевірки публікації версії-скрипта й компіляція з імпортами (MS T3.1).</summary>
public sealed class ScriptPublishChecksTests
{
    private const int MethodologyId = 5;
    private const int VersionId = 50;
    private static readonly DateOnly From = new(2099, 6, 1);

    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly ICalculationBindingStore _bindings = Substitute.For<ICalculationBindingStore>();
    private readonly List<CalculationBinding> _added = [];

    public ScriptPublishChecksTests()
    {
        _bindings.ListAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CalculationBinding>>([]));
        _bindings.When(b => b.Add(Arg.Any<CalculationBinding>())).Do(call => _added.Add(call.Arg<CalculationBinding>()));
    }

    private (ScriptPublishChecks Checks, IMethodologyStore Store) Build(
        string text, MethodologyKind kind, params LiveTable[] tables)
    {
        var store = ScriptTestKit.Store(tables);
        store.GetScriptAsync(VersionId, Arg.Any<CancellationToken>())
            .Returns(new MethodologyScript(text, null, MethodologyEngine.Script, kind == MethodologyKind.Library, NumericMode.Strict));
        var compiler = new ScriptSourceCompiler(_drafts, store);
        var checker = ScriptTestKit.Checker(store, _drafts);
        var materializer = new ScriptMaterializer(store, _drafts, ScriptTestKit.Catalog(), _bindings);
        return (new ScriptPublishChecks(compiler, checker, materializer, store), store);
    }

    private static (Domain.Entities.Calculations.Methodology Methodology, Domain.Entities.Calculations.MethodologyVersion Version) Subject(
        MethodologyKind kind = MethodologyKind.DataDriven)
    {
        var methodology = ScriptTestKit.NewMethodology("OWNER", MethodologyId, kind);
        return (methodology, ScriptTestKit.NewVersion(methodology, VersionId));
    }

    private static async Task<BusinessRuleException> RejectedAsync(Task task)
        => await Assert.ThrowsAsync<BusinessRuleException>(() => task);

    private static string[] Keys(BusinessRuleException ex)
        => [.. ((IReadOnlyList<PublishProblem>)ex.Details!["problems"]!).Select(p => p.MessageKey)];

    private static LiveTable Table() => ScriptTestKit.Table(
        "T1", [ScriptTestKit.Column(1, "Hours"), ScriptTestKit.Column(2, "OUT_A", CellDataType.Calculated)]);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Чистий_скрипт_матеріалізується_і_прив_язується()
    {
        var (checks, store) = Build("rows from T1;\nvar h = row.Hours;\noutput OUT_A : t = h;", MethodologyKind.DataDriven, Table());
        var (methodology, version) = Subject();

        var result = await checks.RunAsync(methodology, version, From, default);

        Assert.Equal(["OUT_A"], result.BoundOutputCodes);
        Assert.Equal((2, "OUT_A"), (_added.Single().ColumnDefId, _added.Single().OutputCode));
        await store.Received(1).ReplaceScriptOutputsAsync(VersionId, Arg.Any<IReadOnlyList<ScriptOutputRow>>(), Arg.Any<CancellationToken>());
        await store.Received(1).ReplaceDependenciesAsync(MethodologyId, Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Вихід_без_колонки_Calculated_дає_noBoundOutput()
    {
        var table = ScriptTestKit.Table("T1", [ScriptTestKit.Column(1, "Hours")]);
        var (checks, _) = Build("rows from T1;\nvar h = row.Hours;\noutput OUT_A : t = h;", MethodologyKind.DataDriven, table);
        var (methodology, version) = Subject();

        var ex = await RejectedAsync(checks.RunAsync(methodology, version, From, default));

        Assert.Equal("ECR-CALC-0422", ex.ErrorCode);
        Assert.Equal("err.ECR-CALC-0422.publishChecksFailed", ex.Details!["messageKey"]);
        Assert.Equal(["script.noBoundOutput"], Keys(ex));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Невідомі_таблиця_колонка_одиниця_речовина_і_запис_блокують_публікацію_разом()
    {
        var table = ScriptTestKit.Table("T1", [ScriptTestKit.Column(1, "Hours")]);
        var (checks, store) = Build(
            "rows from T1;\nvar a = row.Nope;\nforeach substance S in (\"X1\") {\n output OUT_A per S : bad_unit = 1;\n}\n",
            MethodologyKind.DataDriven, table);
        var (methodology, version) = Subject();

        var ex = await RejectedAsync(checks.RunAsync(methodology, version, From, default));

        Assert.Equal(
            ["script.unknownColumn", "script.unknownSubstance", "script.unknownUnit"],
            Keys(ex).OrderBy(k => k, StringComparer.Ordinal));
        var problem = ((IReadOnlyList<PublishProblem>)ex.Details!["problems"]!).First(p => p.MessageKey == "script.unknownColumn");
        Assert.Equal("2", problem.Args["line"]);
        Assert.Equal("Nope", problem.Args["column"]);

        // Нічого не матеріалізовано: перевірки йдуть ДО запису.
        await store.DidNotReceive().ReplaceScriptOutputsAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<ScriptOutputRow>>(), Arg.Any<CancellationToken>());
        Assert.Empty(_added);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Помилка_компіляції_блокує_публікацію_з_позицією()
    {
        var (checks, _) = Build("rows from T1;\nvar a = ;", MethodologyKind.DataDriven, Table());
        var (methodology, version) = Subject();

        var ex = await RejectedAsync(checks.RunAsync(methodology, version, From, default));

        var problem = Assert.Single((IReadOnlyList<PublishProblem>)ex.Details!["problems"]!);
        Assert.StartsWith("script.", problem.MessageKey, StringComparison.Ordinal);
        Assert.Equal("2", problem.Args["line"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Цикл_результатів_блокує_публікацію()
    {
        var table = ScriptTestKit.Table(
            "T1", [ScriptTestKit.Column(1, "Hours"), ScriptTestKit.Column(2, "Foreign", CellDataType.Calculated)]);
        var (checks, store) = Build(
            "rows from T1;\nvar r = row.Foreign;\noutput OUT_A : t = r;", MethodologyKind.DataDriven, table);
        store.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ColumnResultBinding>>(
                [new ColumnResultBinding(100, 2, 7, "B_OUT", "{}", [70])]));
        store.GetMethodologyDependenciesAsync(
                Arg.Is<IReadOnlyCollection<int>>(x => x.Contains(7)), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>(new Dictionary<int, IReadOnlyList<int>> { [7] = [MethodologyId] }));
        var (methodology, version) = Subject();

        var ex = await RejectedAsync(checks.RunAsync(methodology, version, From, default));

        Assert.Contains("script.resultCycle", Keys(ex));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Бібліотека_не_потребує_прив_язаного_виходу()
    {
        var (checks, _) = Build("const k : kg = 1;\nfunction twice(x) = x * 2;", MethodologyKind.Library);
        var (methodology, version) = Subject(MethodologyKind.Library);

        var result = await checks.RunAsync(methodology, version, From, default);

        Assert.Empty(result.BoundOutputCodes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Відсутній_текст_скрипта_це_scriptEmpty()
    {
        var store = ScriptTestKit.Store();
        store.GetScriptAsync(VersionId, Arg.Any<CancellationToken>()).Returns(Task.FromResult<MethodologyScript?>(null));
        var checks = new ScriptPublishChecks(
            new ScriptSourceCompiler(_drafts, store),
            ScriptTestKit.Checker(store),
            new ScriptMaterializer(store, _drafts, ScriptTestKit.Catalog(), _bindings),
            store);
        var (methodology, version) = Subject();

        var ex = await RejectedAsync(checks.RunAsync(methodology, version, From, default));

        Assert.Equal("err.ECR-CALC-0422.scriptEmpty", ex.Details!["messageKey"]);
    }

    // --- імпорти: бібліотека чинної на дату версії, цикл, глибина, не-скрипт ---

    private ScriptSourceCompiler CompilerWithLibraries(params (string Code, int VersionId, string Text, MethodologyEngine Engine)[] libs)
    {
        var store = ScriptTestKit.Store();
        var nextId = 100;
        foreach (var (code, versionId, text, engine) in libs)
        {
            var methodology = ScriptTestKit.NewMethodology(code, nextId++, MethodologyKind.Library);
            var version = ScriptTestKit.NewVersion(methodology, versionId, engine);
            version.SetScript(text, ScriptCompiler.Hash(text));
            methodology.PublishVersion(version, 2, "arrange", new DateOnly(2099, 1, 1), true, new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            _drafts.FindByCodeAsync(code, Arg.Any<CancellationToken>()).Returns(methodology);
            store.GetScriptAsync(versionId, Arg.Any<CancellationToken>())
                .Returns(new MethodologyScript(text, null, engine, true, NumericMode.Strict));
        }

        return new ScriptSourceCompiler(_drafts, store);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Імпорт_розв_язується_у_скомпільовану_бібліотеку_чинної_версії()
    {
        var compiler = CompilerWithLibraries(("COMMON", 900, "const k = 2;\nfunction twice(x) = x * k;", MethodologyEngine.Script));

        var result = await compiler.CompileAsync(
            "import COMMON;\nrows from T1;\noutput O : t = COMMON.twice(3);", "OWNER", NumericMode.Strict, false, From, default);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(["COMMON"], result.Script!.Imports);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Цикл_імпортів_дає_importCycle_із_шляхом()
    {
        var compiler = CompilerWithLibraries(
            ("LIB_A", 901, "import LIB_B;\nconst a = 1;", MethodologyEngine.Script),
            ("LIB_B", 902, "import LIB_A;\nconst b = 1;", MethodologyEngine.Script));

        var result = await compiler.CompileAsync(
            "import LIB_A;\nrows from T1;\noutput O : t = 1;", "OWNER", NumericMode.Strict, false, From, default);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ScriptDiagnosticCodes.ImportCycle, diagnostic.Code);
        Assert.Equal(1, diagnostic.Line);
        Assert.Equal("OWNER -> LIB_A -> LIB_B -> LIB_A", diagnostic.Params["path"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Імпорт_самої_себе_дає_importCycle()
    {
        var compiler = CompilerWithLibraries(("OWNER", 903, "const a = 1;", MethodologyEngine.Script));

        var result = await compiler.CompileAsync(
            "import OWNER;\nrows from T1;\noutput O : t = 1;", "OWNER", NumericMode.Strict, false, From, default);

        Assert.Equal([ScriptDiagnosticCodes.ImportCycle], result.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Занадто_глибокий_ланцюг_імпортів_дає_importCycle()
    {
        // L1 → L2 → … → L9: разом із власником глибина перевищує межу 8.
        var libs = Enumerable.Range(1, 9)
            .Select(i => ($"L{i}", 920 + i, i < 9 ? $"import L{i + 1};\nconst c = 1;" : "const c = 1;", MethodologyEngine.Script))
            .ToArray();
        var compiler = CompilerWithLibraries(libs);

        var result = await compiler.CompileAsync(
            "import L1;\nrows from T1;\noutput O : t = 1;", "OWNER", NumericMode.Strict, false, From, default);

        Assert.Equal([ScriptDiagnosticCodes.ImportCycle], result.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Бібліотека_без_версії_зі_скриптом_дає_libraryNotScript_а_неіснуюча_unknownImport()
    {
        var compiler = CompilerWithLibraries(("OLD_LIB", 930, "const a = 1;", MethodologyEngine.Formulas));

        var result = await compiler.CompileAsync(
            "import OLD_LIB;\nimport NO_SUCH;\nrows from T1;\noutput O : t = 1;", "OWNER", NumericMode.Strict, false, From, default);

        Assert.Equal(
            [ScriptDiagnosticCodes.LibraryNotScript, ScriptDiagnosticCodes.UnknownImport],
            result.Diagnostics.Select(d => d.Code));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    [InlineData(7, false)]
    [InlineData(8, true)]
    public async Task Межа_глибини_імпортів_власник_і_сім_бібліотек_ще_можна_а_вісім_уже_importCycle(int count, bool cycle)
    {
        var libs = Enumerable.Range(1, count)
            .Select(i => ($"L{i}", 950 + i, i < count ? $"import L{i + 1};\nconst c = 1;" : "const c = 1;", MethodologyEngine.Script))
            .ToArray();
        var compiler = CompilerWithLibraries(libs);

        var result = await compiler.CompileAsync(
            "import L1;\nrows from T1;\noutput O : t = 1;", "OWNER", NumericMode.Strict, false, From, default);

        Assert.Equal(
            cycle ? [ScriptDiagnosticCodes.ImportCycle] : Array.Empty<string>(),
            result.Diagnostics.Select(d => d.Code));
    }
}
