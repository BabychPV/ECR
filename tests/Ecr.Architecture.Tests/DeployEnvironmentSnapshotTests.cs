using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// R5-U1/U1-05 (аудит 2026-10-09): оновлення MSI стирає Environment служб, тож <c>deploy-ecr.ps1</c>
/// знімає його до <c>msiexec</c> і повертає все, чого MSI не лишив, ДО кроків 4–5 (явні параметри
/// перекривають); змінений відбиток Data Protection попередньої установки стає «попереднім».
/// </summary>
/// <remarks>
/// Предмет — справжні функції скрипта (<see cref="DeployScriptHarness"/>); реєстру тут немає.
/// Тести/мутація — CI, локально не запускались.
/// </remarks>
public sealed class DeployEnvironmentSnapshotTests
{
    private const string Body = """
        function Out-Restore([string] $key, [string[]] $snapshot, [string[]] $current) {
            $r = Get-EnvironmentEntriesToRestore -Snapshot $snapshot -Current $current
            "restore.$key=$((@($r.Keys) | ForEach-Object { "$_=$($r[$_])" }) -join ';')"
        }
        Out-Restore 'wiped'    @('ECR_Secrets__Pi=x=y', 'ECR_Jobs__Workers__Count=4') @()
        Out-Restore 'explicit' @('ASPNETCORE_URLS=old', 'ECR_Secrets__Pi=x') @('ASPNETCORE_URLS=new')
        Out-Restore 'case'     @('ecr_secrets__pi=x') @('ECR_Secrets__Pi=y')
        Out-Restore 'dup'      @('A=1', 'A=2', 'B=') @()
        Out-Restore 'junk'     @('', 'noequals', '=v') $null
        Out-Restore 'empty'    $null @('A=1')

        function Out-Prev([string] $key, [string] $explicit, [string] $prev, [string] $cur, [string] $new) {
            $p = Resolve-PreviousDataProtectionThumbprints -Explicit $explicit -SnapshotPrevious $prev -SnapshotCurrent $cur -NewCurrent $new
            "prev.$key=$(if ($null -eq $p) { '<null>' } else { $p })"
        }
        Out-Prev 'none'     ''          ''          ''        'aa11'
        Out-Prev 'same'     ''          ''          'AA 11'   'aa11'
        Out-Prev 'rotated'  ''          ''          'bb22'    'aa11'
        Out-Prev 'kept'     ''          'cc33;dd44' 'aa11'    'aa11'
        Out-Prev 'union'    'ee55, cc33' 'cc33'     'bb22'    'aa11'
        Out-Prev 'noNew'    ''          'aa11;cc33' 'bb22'    'AA11'
        """;

    private static readonly Lazy<Dictionary<string, string>> Results =
        new(() => DeployScriptHarness.Run(
            ["ConvertTo-NormalizedThumbprint", "Get-EnvironmentEntriesToRestore", "Resolve-PreviousDataProtectionThumbprints"], Body));

    /// <remarks>
    /// Мутації (CI): повертати весь знімок → червоний <c>explicit</c>/<c>case</c> (старе перекрило б нове);
    /// нічого не повертати → червоний <c>wiped</c>; різати значення по другому <c>=</c> → червоний <c>wiped</c>.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("wiped", "ECR_Secrets__Pi=x=y;ECR_Jobs__Workers__Count=4")]
    [InlineData("explicit", "ECR_Secrets__Pi=x")]
    [InlineData("case", "")]
    [InlineData("dup", "A=1;B=")]
    [InlineData("junk", "")]
    [InlineData("empty", "")]
    public void Після_MSI_повертається_лише_те_чого_немає(string key, string expected)
    {
        Assert.Equal(expected, DeployScriptHarness.Value(Results.Value, $"restore.{key}"));
    }

    /// <remarks>
    /// Мутації (CI): не додавати змінений поточний відбиток → червоний <c>rotated</c>/<c>union</c>; пускати
    /// новий поточний у «попередні» → червоний <c>same</c>/<c>kept</c>/<c>noNew</c>.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("none", "<null>")]
    [InlineData("same", "<null>")]
    [InlineData("rotated", "BB22")]
    [InlineData("kept", "CC33;DD44")]
    [InlineData("union", "EE55;CC33;BB22")]
    [InlineData("noNew", "CC33;BB22")]
    public void Змінений_відбиток_DP_стає_попереднім(string key, string expected)
    {
        Assert.Equal(expected, DeployScriptHarness.Value(Results.Value, $"prev.{key}"));
    }

    /// <remarks>
    /// ⛔ Порядок у тексті скрипта: знімок — до <c>msiexec</c>; повернення — після нього й до кроку 4
    /// (інакше старе значення перекривало б явний параметр). Мутація: перенести знімок після msiexec → червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Знімок_до_MSI_повернення_до_кроку_4()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));

        var snapshot = script.IndexOf("$envSnapshot[$service] = Get-ServiceEnvironmentEntries", StringComparison.Ordinal);
        var msi = script.IndexOf("Start-Process msiexec", StringComparison.Ordinal);
        var restore = script.IndexOf("Get-EnvironmentEntriesToRestore -Snapshot $envSnapshot", StringComparison.Ordinal);
        var step4 = script.IndexOf("Write-Step \"Крок 4/7", StringComparison.Ordinal);
        var previous = script.IndexOf("-Value $previousDataProtection", StringComparison.Ordinal);

        Assert.True(snapshot > 0 && msi > snapshot, "знімка Environment до msiexec немає");
        Assert.True(restore > msi && step4 > restore, "Environment не повертається між msiexec і кроком 4");
        Assert.True(previous > step4, "попередні відбитки DP пишуться не з обчисленого переліку");
    }
}
