using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Аудит R9-F5, R8-Z7, R7-Y3 (low), раунд 11: точність прикладів і рецептів у документації адміністратора.
/// Кожен тест — одна знахідка; предмет — текст документів, а де потрібно — і <c>tools/deploy-ecr.ps1</c>.
/// </summary>
/// <remarks>Тести/мутація — CI, локально не запускались.</remarks>
public sealed partial class AdminDeployDocsPrecisionTests
{
    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine([SourceTree.Root, .. parts])).Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>Текст від першого входження <paramref name="start"/> до першого наступного <paramref name="end"/>.</summary>
    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"немає розділу «{start}»");
        var to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"після «{start}» немає кінця розділу «{end}»");
        return text[from..to];
    }

    private static string Flat(string text) => text.Replace('\n', ' ');

    // cmdlet або параметр шляху з аргументом у лапках, що починається з %ЗМІННОЇ% (cmd-синтаксис).
    [GeneratedRegex(@"(Test-Path|Set-Content|Add-Content|Get-Content|Get-ChildItem|Remove-Item|Get-Acl|Set-Acl|Copy-Item|-Path|-LiteralPath)\s+\(?['""]%[A-Za-z]+%")]
    private static partial Regex CmdPercentPath();

    /// <summary>
    /// F5-05: у PowerShell <c>%ProgramData%</c> не розкривається. Команда <c>Test-Path '%ProgramData%\…'</c> завжди
    /// давала <c>False</c> (секрет, що лишився на диску, не помічався), а <c>Set-Content -Path '%ProgramData%\…'</c>
    /// писала не туди. Мутація: повернути <c>'%ProgramData%\…'</c> у команду → червоний.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("docs/build/11-install-guide.md")]
    [InlineData("docs/admin/operations-runbook.md")]
    [InlineData("docs/admin/admin-guide.md")]
    [InlineData("docs/admin/https-certificate.md")]
    public void Команди_PowerShell_у_документах_не_використовують_cmd_змінні_у_шляхах(string path)
    {
        var text = Read(path.Split('/'));

        Assert.DoesNotMatch(CmdPercentPath(), text);
    }

    /// <summary>
    /// F5-05: ручне задання bootstrap-пароля (§9) і перевірка зникнення файлу (§7) — через <c>$env:ProgramData</c>;
    /// §9 попереджає про власника файлу (застосунок приймає лише Administrators/SYSTEM).
    /// Мутації: прибрати <c>$env:ProgramData</c> з §7/§9 чи попередження про власника → червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Bootstrap_секрет_у_install_guide_береться_з_env_ProgramData_і_має_власника()
    {
        var guide = Read("docs", "build", "11-install-guide.md");

        Assert.Contains("Test-Path (Join-Path $env:ProgramData 'ECR\\config\\bootstrap.secret')", guide, StringComparison.Ordinal);
        var manual = Between(guide, "Якщо потрібно задати bootstrap-", "Рядок підключення живе у");
        Assert.Contains("Join-Path $env:ProgramData 'ECR\\config\\bootstrap.secret'", manual, StringComparison.Ordinal);
        Assert.Contains("BUILTIN\\Administrators", manual, StringComparison.Ordinal);

        // Застосунок справді відхиляє файл не від Administrators/SYSTEM — підстава попередження.
        var app = Read("src", "Ecr.Api", "Startup", "BootstrapSecretFile.cs");
        Assert.Contains("WellKnownSidType.BuiltinAdministratorsSid", app, StringComparison.Ordinal);
        Assert.Contains("WellKnownSidType.LocalSystemSid", app, StringComparison.Ordinal);
    }

    /// <summary>
    /// F5-06: перший запуск першого дня — на сервері, де бази ще немає (§0 обіцяє, що її створить скрипт). Приклад §2.2
    /// з <c>-FirstDeployment</c> без <c>-CreateDatabaseIfMissing</c> зупинявся на кроці 1 з «database missing».
    /// Мутація: прибрати <c>-CreateDatabaseIfMissing</c> з прикладу §2.2 → червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Приклад_першого_розгортання_створює_базу()
    {
        var script = Read("tools", "deploy-ecr.ps1");
        Assert.Contains("if ($CreateDatabaseIfMissing -and $FirstDeployment)", script, StringComparison.Ordinal);

        var example = Between(Read("docs", "build", "11-install-guide.md"), "### 2.2 Альтернатива", "### 2.3 ");
        Assert.Contains("-FirstDeployment", example, StringComparison.Ordinal);
        Assert.Contains("-CreateDatabaseIfMissing", example, StringComparison.Ordinal);
    }

    /// <summary>
    /// Z7-06: з <c>-SkipSchema</c> крок 2 не виконується, список <c>$disabledForSchema</c> порожній, тож крок 6
    /// нічого не повертає — «крок 6 повертає Automatic» на вузлі з <c>-SkipSchema</c> хибне; тип запуску повертає MSI,
    /// а на вузлі з уже встановленою версією його треба повернути вручну.
    /// Мутація: повернути стару фразу чи прибрати ручний <c>Set-Service … Automatic</c> з п. 3.1 → червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Runbook_не_обіцяє_що_крок_6_повертає_Automatic_з_SkipSchema()
    {
        var script = Read("tools", "deploy-ecr.ps1");
        Assert.Contains("$disabledForSchema = @()", script, StringComparison.Ordinal);
        Assert.Contains("Restore-EcrServicesAutoStart -Names $disabledForSchema", script, StringComparison.Ordinal);

        var section = Between(Read("docs", "admin", "operations-runbook.md"), "## 8. Оновлення версії", "## 9. Відкат");
        Assert.DoesNotContain("крок 6 — `Automatic`, якщо служба досі `Disabled`", Flat(section), StringComparison.Ordinal);
        Assert.Contains("Set-Service EcrApi -StartupType Automatic", section, StringComparison.Ordinal);
        Assert.Contains("з `-SkipSchema` крок 2 не виконується", Flat(section), StringComparison.Ordinal);
    }

    /// <summary>
    /// Z7-07: той самий MSI, що вже встановлено, без <c>REINSTALL</c> — режим обслуговування; повтор
    /// <c>deploy-ecr.ps1 -SkipSchema</c> службу воркера не повертає. §10.2 мусить давати рецепт з
    /// <c>REINSTALL=ALL … WORKER_ENABLED=1</c> і не радити голий повтор скрипта для цього випадку.
    /// Мутація: повернути «Якщо службу прибирали (чи ніколи не ставили) — найпростіше повторити» → червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Runbook_10_2_не_радить_голий_повтор_скрипта_тим_самим_MSI()
    {
        var script = Read("tools", "deploy-ecr.ps1");
        Assert.Contains("Служби EcrWorker немає після msiexec з WORKER_ENABLED=1", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "REINSTALL", Between(script, "$msiArgs       = @(", "$msiArgsShown  = $msiArgs.Clone()"), StringComparison.Ordinal);

        var section = Flat(Between(Read("docs", "admin", "operations-runbook.md"), "### 10.2. Увімкнути назад", "### 10.3. "));
        Assert.DoesNotContain("Якщо службу прибирали (чи ніколи не ставили) — найпростіше повторити", section, StringComparison.Ordinal);
        Assert.Contains("REINSTALL=ALL REINSTALLMODE=vomus WORKER_ENABLED=1", section, StringComparison.Ordinal);
    }

    /// <summary>
    /// Y3-04: передперевірка AN-80 спрацьовує всередині кроку 2, коли служби вже зупинені й <c>Disabled</c>:
    /// «зупинити Api» зайве, а <c>PUT category-rule</c> до Api, що не працює, неможливий. §8.7 мусить це казати.
    /// Мутація: повернути «1. `Stop-Service EcrApi`, повний бекап» у §8.7 або прибрати згадку <c>Disabled</c> → червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Runbook_8_7_враховує_що_служби_вже_Disabled()
    {
        var script = Read("tools", "deploy-ecr.ps1");
        var disable = script.IndexOf("$disabledForSchema = @(Disable-EcrServicesAutoStart)", StringComparison.Ordinal);
        var migration = script.IndexOf("Invoke-DeploySql -TargetDb $Database -File $migration", StringComparison.Ordinal);
        Assert.True(disable > 0 && migration > disable, "міграції більше не виконуються після Disabled служб: переглянь runbook §8.7");

        var section = Between(Read("docs", "admin", "operations-runbook.md"), "### 8.7. ", "## 9. Відкат");
        Assert.DoesNotContain("1. `Stop-Service EcrApi`, повний бекап", section, StringComparison.Ordinal);
        Assert.Contains("`Disabled`", section, StringComparison.Ordinal);
        Assert.Contains("PUT …/category-rule", section, StringComparison.Ordinal);
        Assert.Contains("неможливий", section, StringComparison.Ordinal);
    }
}
