using System.ComponentModel;
using System.Diagnostics;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Блоки <c>generated:config</c>, <c>generated:sql-order</c>, <c>generated:prereqs</c> в інструкціях встановлення й
/// runbook ГЕНЕРУЮТЬСЯ <c>tools/gen-install-docs.ps1</c> з коду (ключі конфігурації, <c>$scripts</c> розгортання, версії).
/// Тест запускає той самий інструмент у режимі <c>-Check</c>: розбіжність блоку з джерелом — червоне з командою виправлення.
/// </summary>
/// <remarks>
/// ⚠ Блок <c>generated:grants</c> звіряє окремий сторож <c>ServiceAccountGrantsRunbookTests</c> — тут <c>-SkipGrants</c>
/// (інакше <c>dotnet test</c> запускав би <c>dotnet test</c>). Потрібен PowerShell: <c>pwsh</c> (CI на Linux) або
/// <c>powershell</c> (Windows); якщо жодного немає — тест червоніє, а не мовчки пропускається.
/// </remarks>
public sealed class GeneratedInstallDocsTests
{
    private const string FixCommand = "powershell -File tools/gen-install-docs.ps1";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Згенеровані_блоки_інструкцій_збігаються_з_джерелами_у_режимі_Check()
    {
        var root = SolutionRoot();
        var script = Path.Combine(root, "tools", "gen-install-docs.ps1");
        Assert.True(File.Exists(script), $"Немає {script}.");

        var (exitCode, output) = RunPowerShell(root, script);

        Assert.True(
            exitCode == 0,
            "Згенеровані блоки в docs розійшлися з джерелами (або джерела між собою):" + Environment.NewLine + output
            + Environment.NewLine + "Виправлення: " + FixCommand);
    }

    [Theory]
    [InlineData("docs/admin/operations-runbook.md", "config")]
    [InlineData("docs/build/11-install-guide.md", "sql-order")]
    [InlineData("docs/build/11-install-guide.md", "prereqs")]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Документ_містить_згенерований_блок(string doc, string block)
    {
        var lines = File.ReadAllText(Path.Combine(SolutionRoot(), doc.Replace('/', Path.DirectorySeparatorChar)))
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        Assert.Single(lines, l => l == $"<!-- generated:{block} -->");
        Assert.Single(lines, l => l == $"<!-- /generated:{block} -->");
    }

    private static (int ExitCode, string Output) RunPowerShell(string root, string script)
    {
        Win32Exception? last = null;
        foreach (var shell in new[] { "pwsh", "powershell" })
        {
            var info = new ProcessStartInfo(shell)
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Check", "-SkipGrants" })
            {
                info.ArgumentList.Add(arg);
            }

            try
            {
                using var process = Process.Start(info)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
                {
                    process.Kill(entireProcessTree: true);
                    Assert.Fail($"{shell} {script}: не завершився за 3 хвилини.");
                }

                process.WaitForExit();
                return (process.ExitCode, stdout.Result + stderr.Result);
            }
            catch (Win32Exception ex)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException(
            "Для звірки згенерованих блоків потрібен PowerShell (pwsh або powershell) у PATH.", last);
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }
}
