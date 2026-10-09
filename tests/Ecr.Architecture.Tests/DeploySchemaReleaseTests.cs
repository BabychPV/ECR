using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// R6-X4/X4-03: міграції EF не бачать змін лише в <c>Sql/*.sql</c> (TVP, процедури, тригери). Тому крок 2
/// <c>deploy-ecr.ps1</c> зі схемою в кінці пише штамп релізу схеми (<c>ECR.SchemaRelease</c> = версія пакета),
/// а <c>-SkipSchema</c> звіряє його з пакетом ДО <c>msiexec</c>.
/// </summary>
/// <remarks>
/// Предмет — справжня функція скрипта (<see cref="DeployScriptHarness"/>); SQL тут немає.
/// Тести/мутація — CI, локально не запускались.
/// </remarks>
public sealed class DeploySchemaReleaseTests
{
    private const string Body = """
        function Out-Release([string] $key, [string] $stamp, [string] $package) {
            $p = Get-SchemaReleaseProblem -Stamp $stamp -Package $package -Database 'EcrDb'
            "release.$key.ok=$($null -eq $p)"
            "release.$key.both=$([bool] ($p -and $p.Contains($stamp.Trim()) -and $package -and $p.Contains($package)))"
            "release.$key.fix=$([bool] ($p -and $p.Contains('sp_updateextendedproperty') -and $p.Contains('ECR.SchemaRelease')))"
        }
        Out-Release 'none'    'none'    '0.0.600'
        Out-Release 'empty'   ''        '0.0.600'
        Out-Release 'same'    '0.0.600' '0.0.600'
        Out-Release 'pad'     ' 0.0.600 ' '0.0.600'
        Out-Release 'older'   '0.0.599' '0.0.600'
        Out-Release 'newer'   '0.0.601' '0.0.600'
        Out-Release 'nopkg'   '0.0.600' ''
        """;

    private static readonly Lazy<Dictionary<string, string>> Results =
        new(() => DeployScriptHarness.Run(["Get-SchemaReleaseProblem"], Body));

    /// <remarks>
    /// Мутації (CI): повертати <c>$null</c> завжди → червоні <c>older</c>/<c>newer</c>/<c>nopkg</c>; відмова на
    /// <c>none</c> (база до X4-03) → червоний <c>none</c>; без <c>Trim</c> → червоний <c>pad</c>.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("none", "True", "False", "False")]
    [InlineData("empty", "True", "False", "False")]
    [InlineData("same", "True", "False", "False")]
    [InlineData("pad", "True", "False", "False")]
    [InlineData("older", "False", "True", "True")]
    [InlineData("newer", "False", "True", "True")]
    [InlineData("nopkg", "False", "False", "False")]
    public void SkipSchema_відмовляє_на_схемі_іншого_релізу(string key, string ok, string both, string fix)
    {
        Assert.Equal(ok, DeployScriptHarness.Value(Results.Value, $"release.{key}.ok"));
        Assert.Equal(both, DeployScriptHarness.Value(Results.Value, $"release.{key}.both"));
        Assert.Equal(fix, DeployScriptHarness.Value(Results.Value, $"release.{key}.fix"));
    }

    /// <remarks>
    /// ⛔ Порядок у тексті скрипта: з <c>-SkipSchema</c> — читання штампа й вердикт ДО кроку 3 (msiexec);
    /// зі схемою — запис штампа ПІСЛЯ циклу скриптів (упалий посередині крок 2 штампа не оновлює).
    /// Мутації: перенести запис штампа перед циклом або прибрати звірку з гілки <c>-SkipSchema</c> → червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Штамп_пишеться_після_скриптів_і_звіряється_до_MSI()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));

        var skipped = script.IndexOf("Write-Step \"Крок 2/7: схема — ПРОПУЩЕНО", StringComparison.Ordinal);
        var read = script.IndexOf("WHERE class = 0 AND name = N'ECR.SchemaRelease'), N'none')", StringComparison.Ordinal);
        var verdict = script.IndexOf("Get-SchemaReleaseProblem -Stamp $stamp", StringComparison.Ordinal);
        var step2 = script.IndexOf("Write-Step \"Крок 2/7: схема ($Database", StringComparison.Ordinal);
        var loop = script.IndexOf("foreach ($name in $scripts) {", StringComparison.Ordinal);
        var write = script.IndexOf("EXEC sys.sp_addextendedproperty @name = N'ECR.SchemaRelease'", StringComparison.Ordinal);
        var msi = script.IndexOf("Write-Step \"Крок 3/7: MSI\"", StringComparison.Ordinal);

        Assert.True(skipped > 0 && read > skipped && verdict > read && step2 > verdict, "звірки штампа в гілці -SkipSchema немає");
        Assert.True(loop > step2 && write > loop && msi > write, "штамп не пишеться після циклу скриптів кроку 2");
        Assert.Contains("$schemaRelease = if ($Version) { $Version } elseif ($MsiPath) { Get-MsiProductVersion", script, StringComparison.Ordinal);
    }
}
