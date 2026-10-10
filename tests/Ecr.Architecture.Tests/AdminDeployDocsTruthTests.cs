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
}
