using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// R9-F5/F5-04 (аудит 2026-10-10): без <c>-ServiceAccount</c> MSI ставить ОБИДВІ служби з типом запуску
/// <c>Manual</c> (L10-03, <c>Service.wxs</c>/<c>Worker.wxs</c>), а <c>EcrApi</c> отримує <c>Executor=Worker</c>.
/// Install-guide подавав цей шлях як «достатньо», велів стартувати лише <c>EcrApi</c> (перерахунок стоїть) і
/// мовчав про перезавантаження; перевірка йшла на <c>http://localhost:5000</c> одразу після прикладу з
/// <c>-AppPort 443</c>.
/// </summary>
/// <remarks>Тести/мутація — CI, локально не запускались.</remarks>
public sealed class InstallGuideLocalSystemTests
{
    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine([SourceTree.Root, .. parts]));

    private static string Section(string text, string heading)
    {
        var start = text.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"немає заголовка «{heading}»");
        var level = heading[..(heading.IndexOf(' ', StringComparison.Ordinal) + 1)];
        var next = text.IndexOf("\n" + level, start + heading.Length, StringComparison.Ordinal);
        return next < 0 ? text[start..] : text[start..next];
    }

    private static string Flat(string text) => string.Join(' ', text.Split((char[])['\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries));

    /// <remarks>
    /// Мутації: прибрати <c>EcrWorker</c> з блоку «Далі вручну» чи §4, повернути «цього достатньо» або
    /// <c>http://localhost:5000</c> після прикладу з <c>-AppPort 443</c> → червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Шлях_без_облікового_запису_стартує_обидві_служби_і_не_обіцяє_автостарту()
    {
        // Передумова з коду: обидві служби без SERVICE_ACCOUNT — Manual; скрипт сам попереджає про обидві.
        Assert.Contains("config EcrApi start= demand", Read("installer", "Ecr.Installer", "Service.wxs"), StringComparison.Ordinal);
        Assert.Contains("config EcrWorker start= demand", Read("installer", "Ecr.Installer", "Worker.wxs"), StringComparison.Ordinal);
        Assert.Contains("запускай ОБИДВІ служби", Read("tools", "deploy-ecr.ps1"), StringComparison.Ordinal);

        var guide = Read("docs", "build", "11-install-guide.md");
        var script = Section(guide, "### 2.2 ");
        var manual = script[script.IndexOf("Далі вручну", StringComparison.Ordinal)..];
        Assert.Contains("Start-Service EcrWorker", manual, StringComparison.Ordinal);
        Assert.DoesNotContain("http://localhost:5000/health/live", script, StringComparison.Ordinal);
        Assert.Contains("перезавантаження", Flat(manual), StringComparison.Ordinal);

        var account = Flat(Section(guide, "### 2.3 "));
        Assert.DoesNotContain("цього достатньо", account, StringComparison.Ordinal);
        Assert.Contains("`Manual`", account, StringComparison.Ordinal);

        var update = Section(guide, "## 4. ");
        Assert.Contains("Start-Service EcrWorker", update, StringComparison.Ordinal);
    }
}
