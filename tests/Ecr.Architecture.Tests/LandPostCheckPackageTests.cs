using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Пакет пост-перевірки <c>tools/land/postcheck</c> (K1/вікна): пороги й середовище не зашиті. До узагальнення пакет
/// мав дефолт бази на живий стенд, літерал версії шаблону 1.0.11.0 (S-03c/A-16 червоніли на 1.0.12.0), ім'я бекапу
/// <c>post-k1</c> і шляхи дисків. Статичний сторож за прецедентом <c>LandBuildSqlConnectionRequiredTests</c>:
/// база обов'язкова й лише EcrLand*, без літералів версій, без шляхів на диски, без значення пароля у виводі.
/// </summary>
public sealed class LandPostCheckPackageTests
{
    private static string PackageDir => Path.Combine(SourceTree.Root, "tools", "land", "postcheck");

    private static IEnumerable<string> PackageFiles() =>
        Directory.EnumerateFiles(PackageDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".md", StringComparison.OrdinalIgnoreCase));

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Пакет_має_усі_файли()
    {
        foreach (var name in new[] { "Run-PostCheck.ps1", "PostCheck.sql", "ApiSmoke.ps1", "WindowExtra.sql", "README.md" })
        {
            Assert.True(File.Exists(Path.Combine(PackageDir, name)), $"Немає tools/land/postcheck/{name}");
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void База_обовязкова_лише_EcrLand_без_дефолту_на_живий_стенд()
    {
        foreach (var name in new[] { "Run-PostCheck.ps1", "ApiSmoke.ps1" })
        {
            var text = File.ReadAllText(Path.Combine(PackageDir, name));
            // Параметр -Database: Mandatory + ValidatePattern EcrLand*, без `= '...'`.
            Assert.Matches(
                new Regex(@"\[Parameter\(Mandatory\s*=\s*\$true\)\]\[ValidatePattern\('\^EcrLand[^']*'\)\]\[string\]\$Database\s*,", RegexOptions.CultureInvariant),
                text);
            Assert.DoesNotMatch(new Regex(@"\$Database\s*=\s*['""]", RegexOptions.CultureInvariant), text);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Немає_літералів_живої_бази_версій_шаблону_і_шляхів_на_диски()
    {
        var live = new Regex(@"EcrLandUi", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        var version = new Regex(@"\b1\.0\.1[0-9]\.0\b", RegexOptions.CultureInvariant);
        var drivePath = new Regex(@"(?<![A-Za-z0-9])[A-Za-z]:\\", RegexOptions.CultureInvariant);
        var oldBackupName = new Regex(@"post-k1|post-w7", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        var problems = new List<string>();
        foreach (var file in PackageFiles())
        {
            var text = File.ReadAllText(file);
            var rel = Path.GetFileName(file);
            if (live.IsMatch(text)) { problems.Add($"{rel}: літерал живої бази EcrLandUi"); }
            if (version.IsMatch(text)) { problems.Add($"{rel}: літерал версії шаблону 1.0.1x.0"); }
            if (drivePath.IsMatch(text)) { problems.Add($"{rel}: шлях на диск (X:\\...)"); }
            if (oldBackupName.IsMatch(text)) { problems.Add($"{rel}: зашите ім'я бекапу post-k1/post-w7"); }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Бекап_не_перезаписує_існуючий_файл_і_пароль_не_друкується()
    {
        var api = File.ReadAllText(Path.Combine(PackageDir, "ApiSmoke.ps1"));
        Assert.Contains("-WindowTag", File.ReadAllText(Path.Combine(PackageDir, "Run-PostCheck.ps1")), StringComparison.Ordinal);
        Assert.Contains("Test-Path -LiteralPath $bak", api, StringComparison.Ordinal);
        Assert.Contains("NOINIT", api, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"Write-Host[^\r\n]*\$acc\b|Write-Host[^\r\n]*Password", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), api);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Run_без_EcrLand_бази_відмовляє_до_будь_якого_підключення()
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                     Path.Combine(PackageDir, "Run-PostCheck.ps1"), "-Since", "2026-01-01T00:00:00", "-Database", "Ecr",
                 })
        {
            start.ArgumentList.Add(a);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запустився");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "PowerShell не завершився за 60 с.");
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("Database", stdout + stderr, StringComparison.Ordinal);
    }
}
