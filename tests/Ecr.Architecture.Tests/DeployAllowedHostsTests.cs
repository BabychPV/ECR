using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// PSS-28: <c>deploy-ecr.ps1 -AllowedHosts</c> — перевірка значення, додавання <c>localhost</c> для проб кроку 7 і
/// збереження <c>ECR_AllowedHosts</c> через MSI-оновлення (знімок Environment).
/// </summary>
/// <remarks>
/// Предмет — справжні функції скрипта (<see cref="DeployScriptHarness"/>); реєстру тут немає. Мутації: прибрати
/// додавання <c>localhost</c> — червоніють <c>names</c>/<c>port</c>; дозволити схему/шлях — червоніють <c>scheme</c>/<c>path</c>;
/// дозволити <c>*</c> поряд з іменами — червоніє <c>mixed</c>.
/// </remarks>
public sealed class DeployAllowedHostsTests
{
    private const string Body = """
        function Out-Hosts([string] $key, [string] $value) {
            $r = Resolve-AllowedHosts -Value $value
            if ($r.Problem) { "hosts.$key=PROBLEM" } else { "hosts.$key=$($r.Value)" }
        }
        Out-Hosts 'star'     '*'
        Out-Hosts 'starsp'   ' * '
        Out-Hosts 'names'    'ecr.example.kz;ecr'
        Out-Hosts 'haslocal' 'ecr.example.kz;LocalHost'
        Out-Hosts 'port'     'ecr.example.kz:8443'
        Out-Hosts 'localport' 'localhost:5000;ecr'
        Out-Hosts 'wild'     '*.example.kz'
        Out-Hosts 'ipv6'     '[::1]:8443'
        Out-Hosts 'dup'      'ecr;ECR;ecr'
        Out-Hosts 'trail'    'ecr;;'
        Out-Hosts 'empty'    ''
        Out-Hosts 'blank'    '  '
        Out-Hosts 'onlysep'  ';;'
        Out-Hosts 'scheme'   'https://ecr.example.kz'
        Out-Hosts 'path'     'ecr.example.kz/app'
        Out-Hosts 'mixed'    'ecr;*'
        Out-Hosts 'space'    'ecr example'
        Out-Hosts 'badport'  'ecr:port'
        Out-Hosts 'underscore' 'ec_r'
        """;

    private static readonly Lazy<Dictionary<string, string>> Results =
        new(() => DeployScriptHarness.Run(["Resolve-AllowedHosts"], Body));

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("star", "*")]
    [InlineData("starsp", "*")]
    [InlineData("names", "ecr.example.kz;ecr;localhost")]
    [InlineData("haslocal", "ecr.example.kz;LocalHost")]
    [InlineData("port", "ecr.example.kz:8443;localhost")]
    [InlineData("localport", "localhost:5000;ecr")]
    [InlineData("wild", "*.example.kz;localhost")]
    [InlineData("ipv6", "[::1]:8443;localhost")]
    [InlineData("dup", "ecr;localhost")]
    [InlineData("trail", "ecr;localhost")]
    [InlineData("empty", "PROBLEM")]
    [InlineData("blank", "PROBLEM")]
    [InlineData("onlysep", "PROBLEM")]
    [InlineData("scheme", "PROBLEM")]
    [InlineData("path", "PROBLEM")]
    [InlineData("mixed", "PROBLEM")]
    [InlineData("space", "PROBLEM")]
    [InlineData("badport", "PROBLEM")]
    [InlineData("underscore", "PROBLEM")]
    public void Значення_AllowedHosts_перевіряється_і_отримує_localhost(string key, string expected)
        => Assert.Equal(expected, DeployScriptHarness.Value(Results.Value, $"hosts.{key}"));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void ECR_AllowedHosts_після_MSI_повертається_зі_знімка_а_явний_параметр_пишеться_після_цього()
    {
        // Загальний механізм знімка (R5-U1/U1-05) повертає будь-який запис, якого MSI не лишив, — зокрема цей.
        var restored = DeployScriptHarness.Run(
            ["Get-EnvironmentEntriesToRestore"],
            """
            $r = Get-EnvironmentEntriesToRestore -Snapshot @('ECR_AllowedHosts=ecr;localhost', 'ECR_X=1') -Current @('ECR_X=2')
            "restore=$((@($r.Keys) | ForEach-Object { "$_=$($r[$_])" }) -join '|')"
            """);
        Assert.Equal("ECR_AllowedHosts=ecr;localhost", DeployScriptHarness.Value(restored, "restore"));

        // Запис після відновлення (явний параметр перекриває), лише коли параметр заданий.
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));
        Assert.Contains("[string] $AllowedHosts,", script, StringComparison.Ordinal);
        Assert.Contains("$PSBoundParameters.ContainsKey('AllowedHosts')", script, StringComparison.Ordinal);
        Assert.Contains("-Name 'ECR_AllowedHosts' -Value $allowedHostsValue", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("Get-EnvironmentEntriesToRestore -Snapshot", StringComparison.Ordinal)
            < script.IndexOf("-Name 'ECR_AllowedHosts' -Value", StringComparison.Ordinal),
            "запис ECR_AllowedHosts має йти ПІСЛЯ відновлення знімка");
    }
}
