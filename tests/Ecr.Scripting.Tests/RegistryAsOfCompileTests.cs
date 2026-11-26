using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Scripting;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scripting.Tests;

/// <summary>
/// ✎ SGF (DESIGN-sg-flaring §5.2a, E1/E2): речення <c>registry as of &lt;вираз&gt;;</c> — розбір, зв'язування, діагностики,
/// <c>asof.*</c>, обсяг довідників для графа залежностей (D1).
/// </summary>
public sealed class RegistryAsOfCompileTests
{
    private const string Read = "output X : t = REGFIELD(REGFIND(\"GAS\", \"H2S\"), \"MOL\");\n";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "SGF-AC-1")]
    public void Речення_розбирається_і_несе_вираз_дати()
    {
        var c = T.WithRows("registry as of row.EVENT_START;\n" + Read);

        Assert.True(c.IsSuccess, T.Describe(c));
        var script = c.Script!;
        Assert.IsType<SymbolReferenceNode>(script.RegistryAsOf);
        Assert.Equal("row.EVENT_START", script.RegistryAsOfText);
        Assert.Contains(script.RowFields, f => f.Name == "EVENT_START" && !f.IsOptional);
    }

    /// <summary>AC-3: <c>RegistryCodes</c> (звідси <c>cfg.RegistryUse</c>) реченням не змінюється.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "SGF-AC-3")]
    public void RegistryCodes_ті_самі_з_реченням_і_без()
    {
        var without = T.WithRows(Read).Script!;
        var with = T.WithRows("registry as of row.EVENT_START;\n" + Read).Script!;

        Assert.Equal(without.RegistryCodes, with.RegistryCodes);
        Assert.Equal(["GAS"], with.RegistryCodes);
        Assert.Null(without.RegistryAsOf);
    }

    /// <summary>AC-3 / D1: під реченням обсяг довідника — рік (місяць розширюється), «усе» лишається «усе».</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "SGF-AC-3")]
    public void Обсяг_довідника_під_реченням__рік()
    {
        const string monthly = "output X : t = REGFIELD(REGFIND(\"GAS\", period.Year, period.Month), \"MOL\");\n";

        var plain = ScriptRegistryScopes.Collect(T.WithRows(monthly).Script!);
        var asOf = ScriptRegistryScopes.Collect(T.WithRows("registry as of row.D;\n" + monthly).Script!);
        var all = ScriptRegistryScopes.Collect(T.WithRows("registry as of row.D;\n" + Read).Script!);

        Assert.Equal(PeriodScope.Month, plain["GAS"]);
        Assert.Equal(PeriodScope.Year, asOf["GAS"]);
        Assert.Equal(PeriodScope.All, all["GAS"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "SGF-AC-1")]
    public void asof_властивості_опускаються_в_хост()
    {
        var c = T.WithRows("registry as of row.D;\nvar m = asof.Month;\nvar y = asof.Year;\nvar d = asof.Date;\n" + Read);

        Assert.True(c.IsSuccess, T.Describe(c));
        var vars = c.Script!.Statements.OfType<VarStatement>().ToList();
        Assert.All(vars, v => Assert.Equal("$asof", Assert.IsType<FunctionNode>(v.Expr).Name));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "SGF-AC-1")]
    public void asof_без_речення__невідоме_імя()
        => Assert.Contains(ScriptDiagnosticCodes.UnknownName, T.Codes(T.WithRows("var m = asof.Month;\n" + Read)));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "SGF-AC-1")]
    public void duplicateRegistryAsOf()
        => Assert.Single(
            T.WithRows("registry as of row.D;\nregistry as of row.E;\n" + Read).Diagnostics,
            d => d.Code == ScriptDiagnosticCodes.DuplicateRegistryAsOf && d.MessageKey == "script.duplicateRegistryAsOf");

    [Theory]
    [InlineData("1")]
    [InlineData("\"2026-03-01\"")]
    [InlineData("true")]
    [InlineData("null")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "SGF-AC-2")]
    public void registryAsOfNotDate(string literal)
        => Assert.Contains(ScriptDiagnosticCodes.RegistryAsOfNotDate, T.Codes(T.WithRows($"registry as of {literal};\n" + Read)));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "SGF-AC-1")]
    public void registryAsOfWithoutRegistries()
        => Assert.Contains(
            ScriptDiagnosticCodes.RegistryAsOfWithoutRegistries,
            T.Codes(T.WithRows("registry as of row.D;\noutput X : t = row.A * 2;\n")));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "SGF-AC-1")]
    public void Речення_до_rows__помилка_порядку()
        => Assert.Contains(
            ScriptDiagnosticCodes.UnexpectedToken,
            T.Codes(T.Compile("registry as of row.D;\nrows from T1;\n" + Read)));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "SGF-AC-1")]
    public void Речення_в_бібліотеці__заборонено()
        => Assert.Contains(
            ScriptDiagnosticCodes.RowsInLibrary,
            T.Codes(T.Compile("registry as of row.D;\nvar x = REGFIELD(REGFIND(\"GAS\", \"H2S\"), \"MOL\");\n", library: true)));

    /// <summary>Вираз бачить порожню область: змінні ще не пораховані (рядок обчислює дату ДО операторів).</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "SGF-AC-1")]
    public void Вираз_не_бачить_змінних()
        => Assert.NotEmpty(T.WithRows("var d = row.D;\nregistry as of d;\n" + Read).Diagnostics);

    /// <summary>Контекстні слова: ідентифікатор <c>registry</c> поза реченням лишається звичайним іменем.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "SGF-AC-1")]
    public void registry_як_імя_змінної_працює()
    {
        var c = T.WithRows("var registry = 2;\noutput X : t = registry * 3;\n");
        Assert.True(c.IsSuccess, T.Describe(c));
    }
}
