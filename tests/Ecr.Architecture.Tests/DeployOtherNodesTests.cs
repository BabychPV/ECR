using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// R5-U1/U1-02 (аудит 2026-10-09): <c>deploy-ecr.ps1</c> зупиняє служби перед зміною схеми лише на ЦІЙ
/// машині, тож за D-32 (≥2 вузли) крок 2 спершу шукає сеанси застосунку з інших хостів до бази й
/// відмовляє ДО зупинки локальних служб і до першого <c>sqlcmd</c> зі зміною схеми.
/// </summary>
/// <remarks>
/// Предмет — справжня функція скрипта (<see cref="DeployScriptHarness"/>); SQL тут немає.
/// Тести/мутація — CI, локально не запускались.
/// </remarks>
public sealed class DeployOtherNodesTests
{
    private const string Body = """
        function Out-Foreign([string] $key, [string[]] $rows) {
            $p = Get-ForeignEcrSessionProblem -Rows $rows -Database 'EcrDb'
            "foreign.$key.ok=$($null -eq $p)"
            "foreign.$key.hosts=$(if ($p) { ([regex]::Matches($p, 'NODE-[A-Z]') | ForEach-Object { $_.Value }) -join ',' } else { '' })"
            "foreign.$key.hint=$([bool] ($p -and $p.Contains('-SkipSchema') -and $p.Contains('-SkipOtherNodesCheck')))"
        }
        Out-Foreign 'none'   @()
        Out-Foreign 'null'   $null
        Out-Foreign 'blank'  @('', '   ')
        Out-Foreign 'one'    @('NODE-B')
        Out-Foreign 'two'    @('NODE-C', 'NODE-B', 'NODE-B ')
        """;

    private static readonly Lazy<Dictionary<string, string>> Results =
        new(() => DeployScriptHarness.Run(["Get-ForeignEcrSessionProblem"], Body));

    /// <remarks>
    /// Мутації (CI): повертати <c>$null</c> завжди → червоні <c>one</c>/<c>two</c>; не прибирати порожні
    /// рядки → червоний <c>blank</c>; прибрати підказку <c>-SkipSchema</c> → червоні <c>hint</c>.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("none", "True", "", "False")]
    [InlineData("null", "True", "", "False")]
    [InlineData("blank", "True", "", "False")]
    [InlineData("one", "False", "NODE-B", "True")]
    [InlineData("two", "False", "NODE-B,NODE-C", "True")]
    public void Сеанси_застосунку_з_інших_вузлів_зупиняють_зміну_схеми(string key, string ok, string hosts, string hint)
    {
        Assert.Equal(ok, DeployScriptHarness.Value(Results.Value, $"foreign.{key}.ok"));
        Assert.Equal(hosts, DeployScriptHarness.Value(Results.Value, $"foreign.{key}.hosts"));
        Assert.Equal(hint, DeployScriptHarness.Value(Results.Value, $"foreign.{key}.hint"));
    }

    /// <remarks>
    /// ⛔ Порядок у тексті скрипта: запит до <c>sys.dm_exec_sessions</c> (без власного хоста) і вердикт —
    /// після перевірки копії й ДО зупинки локальних служб; обхід — лише явний <c>-SkipOtherNodesCheck</c>.
    /// Мутація: перенести перевірку після <c>Stop-EcrServicesForSchema</c> → червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Перевірка_інших_вузлів_стоїть_до_зупинки_служб()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));

        var backup = script.IndexOf("Get-SchemaBackupProblem -AgeMinutes", StringComparison.Ordinal);
        var bypass = script.IndexOf("if ($SkipOtherNodesCheck)", StringComparison.Ordinal);
        var query = script.IndexOf("FROM sys.dm_exec_sessions AS s", StringComparison.Ordinal);
        var verdict = script.IndexOf("Get-ForeignEcrSessionProblem -Rows", StringComparison.Ordinal);
        var stop = script.IndexOf("$stoppedForSchema = @(Stop-EcrServicesForSchema)", StringComparison.Ordinal);

        Assert.True(backup > 0 && bypass > backup, "перевірки інших вузлів немає після перевірки копії");
        Assert.True(query > bypass && verdict > query, "запиту до sys.dm_exec_sessions і вердикту немає");
        Assert.True(stop > verdict, "локальні служби зупиняються раніше, ніж перевірено інші вузли");

        var queryText = script.Substring(query, Math.Min(600, script.Length - query));
        Assert.Contains("HOST_NAME()", queryText, StringComparison.Ordinal);
    }
}
