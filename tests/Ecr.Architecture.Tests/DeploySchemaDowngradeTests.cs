using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// R5-U1/U1-06 (аудит 2026-10-09): старіший пакет на новішій базі — <c>deploy-ecr.ps1</c> відмовляє до
/// зупинки служб і до першого <c>sqlcmd -i</c>, а не після того, як <c>CREATE OR ALTER</c> старих скриптів
/// повернув старі процедури й тригери, а MSI відмовив у пониженні версії.
/// </summary>
/// <remarks>
/// Предмет — справжня функція скрипта (<see cref="DeployScriptHarness"/>); SQL тут немає.
/// Тести/мутація — CI, локально не запускались.
/// </remarks>
public sealed class DeploySchemaDowngradeTests
{
    private const string Body = """
        $pkg = @('20260904182730_InitialCreate', '20261001000000_B', '20261005000000_C')
        function Out-Down([string] $key, [string[]] $db, [string[]] $package) {
            $p = Get-SchemaDowngradeProblem -DatabaseMigrations $db -PackageMigrations $package -Database 'EcrDb'
            "down.$key.ok=$($null -eq $p)"
            "down.$key.ids=$(if ($p) { ([regex]::Matches($p, '\d{14}_[A-Za-z]+') | ForEach-Object { $_.Value }) -join ',' } else { '' })"
        }
        Out-Down 'empty'   @() $pkg
        Out-Down 'null'    $null $pkg
        Out-Down 'older'   @('20260904182730_InitialCreate') $pkg
        Out-Down 'same'    @('20260904182730_InitialCreate', '20261001000000_B ', '20261005000000_C') $pkg
        Out-Down 'newer'   @('20260904182730_InitialCreate', '20261005000000_C', '20261009000000_D') $pkg
        Out-Down 'case'    @('20261001000000_b') $pkg
        Out-Down 'noPkg'   @('20260904182730_InitialCreate') @()
        """;

    private static readonly Lazy<Dictionary<string, string>> Results =
        new(() => DeployScriptHarness.Run(["Get-SchemaDowngradeProblem"], Body));

    /// <remarks>
    /// Мутації (CI): повертати <c>$null</c> завжди → червоний <c>newer</c>; порівнювати лише кількість →
    /// червоний <c>newer</c>; приймати порожній пакет → червоний <c>noPkg</c>.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("empty", "True", "")]
    [InlineData("null", "True", "")]
    [InlineData("older", "True", "")]
    [InlineData("same", "True", "")]
    [InlineData("newer", "False", "20261009000000_D")]
    [InlineData("case", "True", "")]
    [InlineData("noPkg", "False", "")]
    public void Старіший_пакет_на_новішу_базу_не_ставиться(string key, string ok, string ids)
    {
        Assert.Equal(ok, DeployScriptHarness.Value(Results.Value, $"down.{key}.ok"));
        Assert.Equal(ids, DeployScriptHarness.Value(Results.Value, $"down.{key}.ids"));
    }

    /// <remarks>
    /// ⛔ Порядок у тексті скрипта: міграції пакета читаються з <c>migration.sql</c> (після його генерації),
    /// вердикт — до зупинки служб і першого <c>sqlcmd -i</c>. Мутація: перенести перевірку після
    /// <c>Stop-EcrServicesForSchema</c> → червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Перевірка_версії_бази_стоїть_до_зміни_схеми()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));

        var generate = script.IndexOf("--output $migration", StringComparison.Ordinal);
        var parse = script.IndexOf("Get-Content -LiteralPath $migration -Raw", StringComparison.Ordinal);
        var verdict = script.IndexOf("Get-SchemaDowngradeProblem -DatabaseMigrations", StringComparison.Ordinal);
        var stop = script.IndexOf("$stoppedForSchema = @(Stop-EcrServicesForSchema)", StringComparison.Ordinal);
        var firstFile = script.IndexOf("Invoke-DeploySql -TargetDb $Database -File", StringComparison.Ordinal);

        Assert.True(generate > 0 && parse > generate, "міграції пакета читаються до генерації migration.sql");
        Assert.True(verdict > parse, "вердикту про версію бази немає");
        Assert.True(stop > verdict && firstFile > verdict, "схема змінюється раніше, ніж перевірено версію бази");
    }
}
