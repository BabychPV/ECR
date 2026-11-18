using Ecr.Application.Calculations.Scripting;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Scripting;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations.Scripting;

/// <summary>
/// Мемоїзація бібліотек у межах однієї компіляції: ромбоподібні імпорти (кожна бібліотека шару імпортує всі бібліотеки
/// наступного) раніше компілювалися до F^глибина разів. Лічильники стоять у тестовому джерелі, а не в часі.
/// </summary>
public sealed class ScriptSourceCompilerMemoTests
{
    private static readonly DateOnly From = new(2099, 6, 1);

    private readonly Dictionary<string, int> _lookups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _scriptReads = new(StringComparer.Ordinal);

    private ScriptSourceCompiler Build(params (string Code, string Text)[] libs)
    {
        var drafts = Substitute.For<IMethodologyDraftStore>();
        var store = ScriptTestKit.Store();
        var nextId = 100;
        foreach (var (code, text) in libs)
        {
            var versionId = 5000 + nextId;
            var methodology = ScriptTestKit.NewMethodology(code, nextId++, MethodologyKind.Library);
            var version = ScriptTestKit.NewVersion(methodology, versionId);
            version.SetScript(text, ScriptCompiler.Hash(text));
            methodology.PublishVersion(version, 2, "arrange", new DateOnly(2099, 1, 1), true, new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var libraryCode = code;
            drafts.FindByCodeAsync(libraryCode, Arg.Any<CancellationToken>()).Returns(_ =>
            {
                _lookups[libraryCode] = _lookups.GetValueOrDefault(libraryCode) + 1;
                return methodology;
            });
            store.GetScriptAsync(versionId, Arg.Any<CancellationToken>()).Returns(_ =>
            {
                _scriptReads[libraryCode] = _scriptReads.GetValueOrDefault(libraryCode) + 1;
                return new MethodologyScript(text, null, MethodologyEngine.Script, true, NumericMode.Strict);
            });
        }

        return new ScriptSourceCompiler(drafts, store);
    }

    /// <summary>Шари <c>D1..Dlayers</c> по <paramref name="width"/> бібліотек; кожна імпортує всі бібліотеки наступного шару.</summary>
    private static (string Code, string Text)[] Diamond(int layers, int width, string? backEdgeTo = null)
    {
        var libs = new List<(string, string)>();
        for (var layer = 1; layer <= layers; layer++)
        {
            for (var k = 0; k < width; k++)
            {
                var imports = layer < layers
                    ? string.Concat(Enumerable.Range(0, width).Select(j => $"import D{layer + 1}_{j};\n"))
                    : backEdgeTo is null ? string.Empty : $"import {backEdgeTo};\n";
                libs.Add(($"D{layer}_{k}", imports + "const c = 1;"));
            }
        }

        return [.. libs];
    }

    private static string OwnerImportingTopLayer(int width)
        => string.Concat(Enumerable.Range(0, width).Select(j => $"import D1_{j};\n")) + "rows from T1;\noutput O : t = 1;";

    private void AssertEachLoadedOnce((string Code, string Text)[] libs)
    {
        foreach (var (code, _) in libs)
        {
            Assert.Equal(1, _lookups.GetValueOrDefault(code));
            Assert.Equal(1, _scriptReads.GetValueOrDefault(code));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Ромб_глибини_8_компілює_кожну_бібліотеку_рівно_один_раз()
    {
        // Власник + 7 шарів = 8 рівнів, по 3 бібліотеки; без мемоїзації це 3^7 = 2187 завантажень нижнього шару.
        var libs = Diamond(7, 3);
        var compiler = Build(libs);

        var result = await compiler.CompileAsync(OwnerImportingTopLayer(3), "OWNER", NumericMode.Strict, false, From, default);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(["D1_0", "D1_1", "D1_2"], result.Script!.Imports);
        AssertEachLoadedOnce(libs);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Ромб_із_зворотним_імпортом_дає_importCycle_і_теж_читається_по_одному_разу()
    {
        var libs = Diamond(7, 3, backEdgeTo: "D1_0");
        var compiler = Build(libs);

        var result = await compiler.CompileAsync(OwnerImportingTopLayer(3), "OWNER", NumericMode.Strict, false, From, default);

        Assert.Contains(result.Diagnostics, d => d.Code == ScriptDiagnosticCodes.ImportCycle && d.Params["name"] == "D1_0");
        Assert.All(result.Diagnostics, d => Assert.Equal(ScriptDiagnosticCodes.ImportCycle, d.Code));
        Assert.Equal(3, result.Diagnostics.Count);
        AssertEachLoadedOnce(libs);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Ромб_глибини_9_дає_importCycle_із_межею_і_читається_по_одному_разу()
    {
        var libs = Diamond(8, 3);
        var compiler = Build(libs);

        var result = await compiler.CompileAsync(OwnerImportingTopLayer(3), "OWNER", NumericMode.Strict, false, From, default);

        Assert.Equal(3, result.Diagnostics.Count);
        Assert.All(result.Diagnostics, d => Assert.Equal(ScriptDiagnosticCodes.ImportCycle, d.Code));
        Assert.All(result.Diagnostics, d => Assert.Equal(ScriptSourceCompiler.MaxImportDepth + 1, d.Params["path"].Split(" -> ").Length));
        AssertEachLoadedOnce(libs);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Кеш_не_пом_якшує_межу_глибини_для_бібліотеки_яку_імпортують_і_неглибоко_і_глибоко()
    {
        // X → Y → Z: висота 2. Власник імпортує X на глибині 1 (можна) і ланцюг A1…A5, у кінці якого X на глибині 6
        // (6 + 2 ≥ 8: не можна). Запис X з кешу мусить дозволяти перше й забороняти друге.
        (string, string)[] libs =
        [
            ("X", "import Y;\nconst c = 1;"),
            ("Y", "import Z;\nconst c = 1;"),
            ("Z", "const c = 1;"),
            ("A1", "import A2;\nconst c = 1;"),
            ("A2", "import A3;\nconst c = 1;"),
            ("A3", "import A4;\nconst c = 1;"),
            ("A4", "import A5;\nconst c = 1;"),
            ("A5", "import X;\nconst c = 1;"),
        ];
        var compiler = Build(libs);

        var result = await compiler.CompileAsync(
            "import X;\nimport A1;\nrows from T1;\noutput O : t = 1;", "OWNER", NumericMode.Strict, false, From, default);

        var d = Assert.Single(result.Diagnostics);
        Assert.Equal(ScriptDiagnosticCodes.ImportCycle, d.Code);
        Assert.Equal(2, d.Line);
        Assert.Equal("A1", d.Params["name"]);
        Assert.Equal(1, _lookups["X"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.1")]
    public async Task Окремі_компіляції_не_ділять_кеш()
    {
        var libs = Diamond(2, 2);
        var compiler = Build(libs);

        await compiler.CompileAsync(OwnerImportingTopLayer(2), "OWNER", NumericMode.Strict, false, From, default);
        await compiler.CompileAsync(OwnerImportingTopLayer(2), "OWNER", NumericMode.Strict, false, From, default);

        Assert.All(libs, l => Assert.Equal(2, _lookups[l.Code]));
    }
}
