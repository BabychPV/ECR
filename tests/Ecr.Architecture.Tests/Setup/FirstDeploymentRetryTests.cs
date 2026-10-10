using System.Security;
using Ecr.Setup;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests.Setup;

/// <summary>
/// R9-F5/F5-01 (аудит 2026-10-10): перше розгортання, що впало ПІСЛЯ кроку 2 скрипта (схему вже накочено,
/// файл пароля bootstrap — ні), повторюється лише як <c>-FirstDeployment -SkipSchema -BootstrapPassword</c>.
/// Раніше майстер дозволяв пропуск схеми лише в «Update», який пароля не передає, а підказка скрипта вела саме
/// туди — повтор закінчувався «Done» і системою без жодного адміністратора.
/// </summary>
/// <remarks>Тести/мутація — CI, локально не запускались.</remarks>
public sealed class FirstDeploymentRetryTests
{
    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine([SourceTree.Root, .. parts]));

    /// <remarks>Мутація: прибрати пропуск схеми з першого розгортання (підпис/перемикач) → червоний.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Повтор_першого_розгортання_передає_пароль_разом_із_пропуском_схеми()
    {
        using var bootstrap = new SecureString();
        foreach (var c in "Bootstrap-1!")
        {
            bootstrap.AppendChar(c);
        }

        var state = new WizardState
        {
            Mode = WizardMode.FirstDeployment,
            SkipSchema = true,
            BootstrapPassword = bootstrap,
            DataProtectionThumbprint = "AA11BB22CC33DD44EE55FF6600112233445566AA",
            HttpsThumbprint = "0123456789ABCDEF0123456789ABCDEF01234567",
        };

        var names = DeployArguments.Build(state).Select(a => a.Key).ToList();

        Assert.Contains("FirstDeployment", names);
        Assert.Contains("SkipSchema", names);
        Assert.Contains("BootstrapPassword", names);
        Assert.False(state.SkipBackupCheck);
        Assert.Contains("previous attempt", WizardState.SkipSchemaLabel(WizardMode.FirstDeployment), StringComparison.Ordinal);
        Assert.Equal("Schema already applied separately (skip)", WizardState.SkipSchemaLabel(WizardMode.Update));
    }

    /// <remarks>
    /// Крок бази (WinForms, не підключений до тестів) — за текстом: прапорець видно в обох режимах, а
    /// <c>Apply</c> не скидає його поза «Update». Мутація: повернути <c>Mode == WizardMode.Update &amp;&amp;</c> → червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Крок_бази_дозволяє_пропуск_схеми_в_першому_розгортанні()
    {
        var step = Read("tools", "Ecr.Setup", "Steps", "DatabaseStep.cs");

        Assert.Contains("state.SkipSchema = _skipSchemaCheckBox!.Checked;", step, StringComparison.Ordinal);
        Assert.Contains("_skipSchemaCheckBox.Text = WizardState.SkipSchemaLabel(state.Mode);", step, StringComparison.Ordinal);
        Assert.DoesNotContain("_skipSchemaCheckBox!.Visible = state.Mode == WizardMode.Update", step, StringComparison.Ordinal);
    }

    /// <remarks>Мутація: повернути підказку «у майстрі — Update … з -SkipSchema» без пароля → червоний.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Підказка_відмови_і_runbook_називають_повну_комбінацію()
    {
        var results = DeployScriptHarness.Run(
            ["Get-FirstDeploymentProblem"],
            """
            $p = Get-FirstDeploymentProblem -AppliedCount '12' -Database 'EcrDb'
            "retry.full=$([bool] ($p -and $p.Contains('-FirstDeployment -SkipSchema -BootstrapPassword')))"
            """);
        Assert.Equal("True", DeployScriptHarness.Value(results, "retry.full"));

        var runbook = Read("docs", "admin", "operations-runbook.md");
        var start = runbook.IndexOf("**Запису `bootstrap` немає зовсім**", StringComparison.Ordinal);
        Assert.True(start > 0, "runbook §6.3 не описує випадок «запису bootstrap немає зовсім»");
        var block = runbook[start..runbook.IndexOf("### 6.4.", start, StringComparison.Ordinal)];
        Assert.Contains("deploy-ecr.ps1 -FirstDeployment -SkipSchema -BootstrapPassword", block, StringComparison.Ordinal);
        Assert.DoesNotContain("-File", block, StringComparison.Ordinal);
    }
}
