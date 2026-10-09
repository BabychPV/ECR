using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// S2-01 (аудит 2026-10-09b): <c>deploy-ecr.ps1</c> закриває ключі служб <c>EcrApi</c>/<c>EcrWorker</c>
/// від <c>BUILTIN\Users</c> щоразу, коли їхній Environment несе секрет (рядок підключення чи
/// <c>ECR_Secrets__*</c>), а не лише коли в рядку підключення є пароль SQL — під gMSA секрети PI/SMTP
/// інакше читав кожен локальний користувач.
/// </summary>
/// <remarks>
/// Предмет — справжня функція скрипта (вирізається парсером, <see cref="DeployScriptHarness"/>) і
/// розташування виклику в тексті скрипта. Запис ACL у справжній реєстр перевіряє Windows-раннер
/// (<c>tools/ci-msi-install.ps1</c>, D5b/D5d). Тести/мутація — CI, локально не запускались.
/// </remarks>
public sealed class DeployServiceKeySecretsTests
{
    private const string Body = """
        function Out-Secret([string] $key, [string[]] $entries) { "$key=$(Test-ServiceEnvironmentHasSecret -Entries $entries)" }
        Out-Secret 'empty'          @()
        Out-Secret 'nonSecret'      @('ASPNETCORE_URLS=http://+:5000', 'ECR_Database__EditionMode=Standard')
        Out-Secret 'gmsaConnection' @('ASPNETCORE_URLS=http://+:5000', 'ECR_ConnectionStrings__Ecr=Server=.;Database=Ecr;Integrated Security=True')
        Out-Secret 'piSecret'       @('ECR_Secrets__PiAf.Primary=p@ss')
        Out-Secret 'smtpLowerCase'  @('ecr_secrets__SmtpPassword=x')
        Out-Secret 'emptyValue'     @('ECR_Secrets__PiAf.Primary=')
        Out-Secret 'noName'         @('ECR_Secrets__=x')
        Out-Secret 'notPrefix'      @('XECR_Secrets__A=x')
        """;

    private static readonly Lazy<Dictionary<string, string>> Results =
        new(() => DeployScriptHarness.Run(["Test-ServiceEnvironmentHasSecret"], Body));

    /// <remarks>
    /// Мутації (CI): шукати лише <c>Password</c>/<c>ConnectionStrings</c> → <c>piSecret</c>/<c>smtpLowerCase</c>
    /// червоні; шукати лише <c>Secrets</c> → <c>gmsaConnection</c> червоний; прийняти порожнє значення →
    /// <c>emptyValue</c> червоний.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("empty", "False")]
    [InlineData("nonSecret", "False")]
    [InlineData("gmsaConnection", "True")]
    [InlineData("piSecret", "True")]
    [InlineData("smtpLowerCase", "True")]
    [InlineData("emptyValue", "False")]
    [InlineData("noName", "False")]
    [InlineData("notPrefix", "False")]
    public void Секретом_вважаються_рядок_підключення_і_ECR_Secrets(string key, string expected)
        => Assert.Equal(expected, DeployScriptHarness.Value(Results.Value, key));

    /// <remarks>
    /// ⛔ Ключ закривається після ВСІХ записів Environment (кінець кроку 5), до старту служб (крок 6), і
    /// умова — секрети в Environment або <c>-ConnectionString</c>, а не <c>Test-ConnectionStringHasPassword</c>.
    /// Мутація: повернути виклик у гілку пароля SQL → червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Ключ_служби_закривається_за_секретами_в_Environment_а_не_за_паролем_SQL()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));

        var step5 = script.IndexOf("Write-Step \"Крок 5/7", StringComparison.Ordinal);
        var step6 = script.IndexOf("Write-Step \"Крок 6/7", StringComparison.Ordinal);
        var call = script.IndexOf("Protect-ServiceRegistryKey -ServiceName $service", StringComparison.Ordinal);
        Assert.True(step5 > 0 && step6 > step5, "кроків 5/6 не знайдено");
        Assert.True(call > step5 && call < step6, "ключ служби закривається не між останнім записом Environment і стартом служб");
        Assert.Equal(-1, script.IndexOf("Protect-ServiceRegistryKey -ServiceName", call + 1, StringComparison.Ordinal));

        var loop = script.LastIndexOf("foreach ($service in @('EcrApi') + @(if ($workerEnabled) { 'EcrWorker' }))", call, StringComparison.Ordinal);
        Assert.True(loop > step5, "виклик не в циклі по EcrApi/EcrWorker");
        var guard = script[loop..call];
        Assert.Contains("Test-ServiceEnvironmentHasSecret -Entries $envEntries", guard, StringComparison.Ordinal);
        Assert.Contains("$ConnectionString -or", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("Test-ConnectionStringHasPassword", guard, StringComparison.Ordinal);
    }
}
