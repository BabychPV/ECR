using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Аудит R8-Z7: документація адміністратора (runbook, install-guide, нотатки тестувальника) мусить
/// збігатися з тим, що справді робить <c>tools/deploy-ecr.ps1</c>, бо за нею адміністратор планує
/// вікно оновлення й відкат. Кожен тест — одна знахідка; предмет — текст документів і скрипта.
/// </summary>
/// <remarks>Тести/мутація — CI, локально не запускались.</remarks>
public sealed class AdminDeployDocsTruthTests
{
    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine([SourceTree.Root, .. parts]));

    private static string Runbook() => Read("docs", "admin", "operations-runbook.md");

    private static string InstallGuide() => Read("docs", "build", "11-install-guide.md");

    /// <summary>Текст від заголовка <paramref name="heading"/> до наступного заголовка того ж рівня.</summary>
    private static string Section(string text, string heading)
    {
        var start = text.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"немає заголовка «{heading}»");
        var level = heading[..(heading.IndexOf(' ', StringComparison.Ordinal) + 1)];
        var next = text.IndexOf("\n" + level, start + heading.Length, StringComparison.Ordinal);
        return next < 0 ? text[start..] : text[start..next];
    }

    /// <summary>
    /// R8-Z7-03: передперевірки міграцій виконуються всередині <c>migration.sql</c>, тобто ПІСЛЯ зупинки
    /// служб, <c>Disabled</c> і штампа <c>incomplete:</c>. Фраза «стара версія лишається робочою» хибна.
    /// Мутації: повернути фразу в runbook чи нотатки тестувальника або прибрати блок «Якщо крок 2 упав» → червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Після_відмови_кроку_2_документи_не_обіцяють_робочу_стару_версію()
    {
        var script = Read("tools", "deploy-ecr.ps1");
        var stop = script.IndexOf("$stoppedForSchema = @(Stop-EcrServicesForSchema)", StringComparison.Ordinal);
        var stamp = script.IndexOf("('incomplete:' +", StringComparison.Ordinal);
        var migration = script.IndexOf("Invoke-DeploySql -TargetDb $Database -File $migration", StringComparison.Ordinal);
        Assert.True(stop > 0 && stamp > stop && migration > stamp,
            "порядок кроку 2 змінився (зупинка → штамп → migration.sql): переглянь runbook п. 8 «Якщо крок 2 упав»");

        var runbook = Runbook();
        Assert.Contains("**якщо крок 2 упав**", runbook, StringComparison.Ordinal);
        foreach (var doc in new[] { runbook, Read("docs", "build", "TESTER-HANDOVER.md"), Read("docs", "build", "TESTER-GUIDE.md") })
        {
            var flat = doc.Replace("\r\n", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
            Assert.DoesNotContain("стара версія лишається робочою", flat, StringComparison.Ordinal);
            Assert.DoesNotContain("MSI не встановлено, стара версія працює", flat, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// R8-Z7-01: індекси на <c>calc.CalculationResult</c> у <c>Sql/*.sql</c> будуються на першому оновленні
    /// бази без них, у <c>07-partition-tables.sql</c> — офлайн на будь-якій редакції. Кожен такий індекс
    /// мусить бути названий у runbook (вікно обслуговування), а install-guide не обіцяє «хвилин».
    /// Мутації: прибрати рядок індексу з runbook §8.2 або повернути «зазвичай хвилини» → червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Офлайн_індекси_результатів_описані_у_вікні_оновлення()
    {
        var sqlDir = Path.Combine(SourceTree.Root, "src", "Ecr.Infrastructure", "Persistence", "Sql");
        var names = Directory.GetFiles(sqlDir, "*.sql")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f),
                    @"CREATE\s+(?:UNIQUE\s+)?(?:NONCLUSTERED\s+|CLUSTERED\s+)?INDEX\s+(\w+)\s+ON\s+calc\.CalculationResult\b",
                    RegexOptions.IgnoreCase)
                .Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.Contains("IX_CalculationResult_DocRun", names);

        var runbook = Runbook();
        foreach (var name in names)
        {
            Assert.Contains("`" + name + "`", runbook, StringComparison.Ordinal);
        }

        var guide = InstallGuide().Replace("\r\n", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        Assert.DoesNotContain("зазвичай хвилини", guide, StringComparison.Ordinal);
        Assert.Contains("IX_CalculationResult_DocRun", guide, StringComparison.Ordinal);
    }

    /// <summary>
    /// R8-Z7-02: MSI не пам'ятає <c>SERVICE_ACCOUNT</c>; без <c>-ServiceAccount</c> <c>deploy-ecr.ps1</c> не передає
    /// його в msiexec, і служба перереєстровується під LocalSystem з Manual (<c>Service.wxs</c>). Приклад
    /// оновлення в install-guide §4 мусить містити <c>-ServiceAccount</c>.
    /// Мутації: прибрати <c>-ServiceAccount</c> з прикладу або умову в скрипті → червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Приклад_оновлення_передає_обліковий_запис_служби()
    {
        var script = Read("tools", "deploy-ecr.ps1");
        Assert.Contains("if ($ServiceAccount) {\r\n    $msiArgs      += \"SERVICE_ACCOUNT=$ServiceAccount\"",
            script.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal),
            StringComparison.Ordinal);

        var section = Section(InstallGuide(), "## 4. Оновлення на нову версію");
        var blocks = Regex.Matches(section, @"```powershell\r?\n(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .Where(b => b.Contains("deploy-ecr.ps1", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(blocks);
        Assert.All(blocks, b => Assert.Contains("-ServiceAccount", b, StringComparison.Ordinal));
        Assert.Contains("StartName", section, StringComparison.Ordinal);
    }

    /// <summary>
    /// R8-Z7-04: після збою кроку 2 служби <c>Disabled</c> (<c>Disable-EcrServicesAutoStart</c>), а MSI не ставився;
    /// голий msiexec попереднього MSI без <c>SERVICE_ACCOUNT</c> лишає службу під LocalSystem. Runbook §9 мусить
    /// мати гілку «Крок 2» з поверненням <c>Automatic</c> і ставити попередню версію з обліковим записом служби.
    /// Мутації: прибрати <c>Set-Service … Automatic</c>, <c>-ServiceAccount</c> чи рецепт <c>REINSTALL=ALL</c> з §9 → червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Відкат_враховує_вимкнені_служби_і_обліковий_запис()
    {
        Assert.Contains("$disabledForSchema = @(Disable-EcrServicesAutoStart)", Read("tools", "deploy-ecr.ps1"), StringComparison.Ordinal);

        var rollback = Section(Runbook(), "## 9. Відкат");
        Assert.Contains("Set-Service EcrApi -StartupType Automatic", rollback, StringComparison.Ordinal);
        Assert.Contains("**Крок 2**", rollback, StringComparison.Ordinal);
        Assert.Contains("-ServiceAccount", rollback, StringComparison.Ordinal);
        Assert.Contains("REINSTALL=ALL REINSTALLMODE=vomus SERVICE_ACCOUNT=", rollback, StringComparison.Ordinal);
    }
}
