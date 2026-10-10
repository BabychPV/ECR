using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Y3-03 (аудит 2026-10-10): U1-06 (<see cref="DeploySchemaDowngradeTests"/>) бачить лише міграції EF, а реліз,
/// що змінює тільки <c>Sql/*.sql</c>, має ті самі міграції. Крок 2 <c>deploy-ecr.ps1</c> зі схемою тепер
/// відмовляє й за штампом <c>ECR.SchemaRelease</c> новішим за пакет — до зупинки служб і до першого <c>sqlcmd</c>.
/// </summary>
/// <remarks>
/// Предмет — справжня функція скрипта (<see cref="DeployScriptHarness"/>); SQL тут немає.
/// Тести/мутація — CI, локально не запускались.
/// </remarks>
public sealed class DeploySchemaStampDowngradeTests
{
    private const string Body = """
        function Out-Stamp([string] $key, [string] $stamp, [string] $package) {
            $p = Get-SchemaStampDowngradeProblem -Stamp $stamp -Package $package -Database 'EcrDb'
            "stamp.$key.ok=$($null -eq $p)"
            "stamp.$key.both=$([bool] ($p -and $p.Contains('1.6.1') -and $p.Contains('1.6.0')))"
        }
        Out-Stamp 'newer'            '1.6.1'            '1.6.0'
        Out-Stamp 'newerNumeric'     '1.10.0'           '1.9.0'
        Out-Stamp 'newerPad'         ' 1.6.1 '          '1.6.0'
        Out-Stamp 'incompleteNewer'  'incomplete:1.6.1' '1.6.0'
        Out-Stamp 'same'             '1.6.0'            '1.6.0'
        Out-Stamp 'older'            '1.5.9'            '1.6.0'
        Out-Stamp 'incompleteSame'   'incomplete:1.6.0' '1.6.0'
        Out-Stamp 'incompleteOlder'  'incomplete:1.5.0' '1.6.0'
        Out-Stamp 'none'             'none'             '1.6.0'
        Out-Stamp 'empty'            ''                 '1.6.0'
        Out-Stamp 'unknownStamp'     'incomplete:unknown' '1.6.0'
        Out-Stamp 'nopkg'            '1.6.1'            ''
        """;

    private static readonly Lazy<Dictionary<string, string>> Results =
        new(() => DeployScriptHarness.Run(["Get-SchemaStampDowngradeProblem"], Body));

    /// <remarks>
    /// Мутації (CI): повертати <c>$null</c> завжди → червоні <c>newer*</c>/<c>incompleteNewer</c>; порівнювати
    /// рядки замість <c>[version]</c> → червоний <c>newerNumeric</c> (1.10.0 проти 1.9.0); відмова на
    /// <c>-ge</c> замість <c>-gt</c> → червоні <c>same</c>/<c>incompleteSame</c> (повтор після збою).
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("newer", "False")]
    [InlineData("newerNumeric", "False")]
    [InlineData("newerPad", "False")]
    [InlineData("incompleteNewer", "False")]
    [InlineData("same", "True")]
    [InlineData("older", "True")]
    [InlineData("incompleteSame", "True")]
    [InlineData("incompleteOlder", "True")]
    [InlineData("none", "True")]
    [InlineData("empty", "True")]
    [InlineData("unknownStamp", "True")]
    [InlineData("nopkg", "True")]
    public void Пакет_старший_за_штамп_бази_не_ставиться_зі_схемою(string key, string ok)
    {
        Assert.Equal(ok, DeployScriptHarness.Value(Results.Value, $"stamp.{key}.ok"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Відмова_називає_обидві_версії()
    {
        Assert.Equal("True", DeployScriptHarness.Value(Results.Value, "stamp.newer.both"));
        Assert.Equal("True", DeployScriptHarness.Value(Results.Value, "stamp.incompleteNewer.both"));
    }

    /// <remarks>
    /// ⛔ Порядок у тексті скрипта: вердикт за штампом — у кроці 2 ДО зупинки служб і до першого <c>sqlcmd</c> зі
    /// зміною схеми. Мутація: перенести перевірку після <c>Stop-EcrServicesForSchema</c> або прибрати → червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Перевірка_штампа_стоїть_до_зміни_схеми()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));

        var step2 = script.IndexOf("Write-Step \"Крок 2/7: схема ($Database", StringComparison.Ordinal);
        var verdict = script.IndexOf("Get-SchemaStampDowngradeProblem -Stamp", StringComparison.Ordinal);
        var stop = script.IndexOf("$stoppedForSchema = @(Stop-EcrServicesForSchema)", StringComparison.Ordinal);
        var firstFile = script.IndexOf("Invoke-DeploySql -TargetDb $Database -File", StringComparison.Ordinal);

        Assert.True(step2 > 0 && verdict > step2, "відмови за штампом у кроці 2 немає");
        Assert.True(stop > verdict && firstFile > verdict, "схема змінюється раніше, ніж звірено штамп");
    }
}
