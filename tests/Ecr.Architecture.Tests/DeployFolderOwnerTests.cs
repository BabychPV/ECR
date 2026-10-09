using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// N5-02 (аудит 2026-10-09): <c>deploy-ecr.ps1</c> відмовляється ставити MSI, якщо власник
/// <c>%ProgramData%\ECR</c> (чи <c>config</c>/<c>logs</c>) — не SYSTEM і не Administrators.
/// </summary>
/// <remarks>
/// Предмет — справжні функції скрипта (вирізаються парсером, <see cref="DeployScriptHarness"/>).
/// Саму читку власника теки (<c>Get-Acl</c>) перевіряє лише Windows-раннер: <c>tools/verify-msi.ps1</c>
/// (<c>Assert-TrustedOwner</c>). Тести/мутація — CI, локально не запускались.
/// </remarks>
public sealed class DeployFolderOwnerTests
{
    private const string Body = """
        function Out-Trusted([string] $key, $sid) { "$key=$(Test-TrustedOwnerSid $sid)" }
        Out-Trusted 'system'        'S-1-5-18'
        Out-Trusted 'administrators' 'S-1-5-32-544'
        Out-Trusted 'users'         'S-1-5-32-545'
        Out-Trusted 'domainUser'    'S-1-5-21-1111-2222-3333-1001'
        Out-Trusted 'localService'  'S-1-5-19'
        Out-Trusted 'empty'         ''
        """;

    private static readonly Lazy<Dictionary<string, string>> Results =
        new(() => DeployScriptHarness.Run(["Test-TrustedOwnerSid"], Body));

    /// <remarks>
    /// Мутація (CI): у <c>Test-TrustedOwnerSid</c> додати <c>S-1-5-32-545</c> (Users) → <c>users</c> червоний.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("system", "True")]
    [InlineData("administrators", "True")]
    [InlineData("users", "False")]
    [InlineData("domainUser", "False")]
    [InlineData("localService", "False")]
    [InlineData("empty", "False")]
    public void Власником_теки_ECR_довіряються_лише_SYSTEM_і_Administrators(string key, string expected)
        => Assert.Equal(expected, DeployScriptHarness.Value(Results.Value, key));

    /// <remarks>
    /// ⛔ Перевірка власника стоїть ДО збірки/установки MSI (кроку 3) і накриває всі три теки;
    /// порожній індекс (функцію перейменовано) не дає зеленого.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Власник_тек_перевіряється_до_msiexec_для_ECR_config_і_logs()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));

        var call = script.IndexOf("Assert-EcrFolderOwner -Path", StringComparison.Ordinal);
        var build = script.IndexOf("& $buildMsi", StringComparison.Ordinal);
        var msi = script.IndexOf("Start-Process msiexec", StringComparison.Ordinal);

        Assert.True(call > 0, "виклику Assert-EcrFolderOwner немає");
        Assert.True(build > call, "власника перевірено після збірки MSI");
        Assert.True(msi > call, "власника перевірено після msiexec");

        var line = script[call..script.IndexOf('\n', call)];
        Assert.Contains("'config'", line, StringComparison.Ordinal);
        Assert.Contains("'logs'", line, StringComparison.Ordinal);
        Assert.Contains("$programDataEcr", line, StringComparison.Ordinal);
    }
}
