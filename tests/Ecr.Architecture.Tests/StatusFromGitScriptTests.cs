using System.Diagnostics;
using System.Text;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// <c>tools/status-from-git.ps1</c>: статус задачі черги — з комітів, а не з рядка черги.
/// ID у коміті <c>main</c> → «випущено», лише в гілці зведення → «зведено».
/// </summary>
/// <remarks>
/// ⛔ Предмет — справжній скрипт у <c>pwsh</c> на тимчасовому git-репозиторії (як у
/// <see cref="DeployScriptHarness"/>, без копії логіки). Сценарій ловить хибні «випущено», які
/// дав би наївний пошук підрядка: <c>AN-3</c> у <c>WLAN-3</c>, <c>AN-6</c> в <c>AN-6б</c>, ID у тексті
/// merge-коміту, коміт, що лише переписує <c>WORK-QUEUE.md</c>.
/// Мутації (прогнано локально в pwsh 7.5): прибрати <c>--no-merges</c> → <c>AN-4</c> «зведено»;
/// прибрати праву межу ID → <c>AN-6</c> «зведено» (з <c>AN-6б</c>); прибрати ліву межу →
/// <c>AN-3</c> «зведено» (з <c>WLAN-3</c>); прибрати пропуск облікових
/// комітів → <c>AN-3</c> «випущено»; прибрати запасний пошук за хешем → <c>AN-5</c>/<c>AN-7</c>
/// «—». Кожна червонить тест.
/// </remarks>
public sealed class StatusFromGitScriptTests
{
    private static readonly Lazy<Run> Result = new(Execute);

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("AN-1", "випущено (+1 зведено)")]
    [InlineData("AN-2", "зведено")]
    [InlineData("AN-3", "—")]
    [InlineData("AN-4", "—")]
    [InlineData("AN-5", "зведено (за хешем із черги)")]
    [InlineData("AN-6", "—")]
    [InlineData("AN-7", "випущено (за хешем із черги)")]
    [InlineData("AN-10", "—")]
    [InlineData("AN-10b", "зведено")]
    public void Статус_задачі_береться_з_комітів(string id, string expected) =>
        Assert.Equal(expected, Status(Result.Value.Tasks, id));

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("L1-04", "випущено")]
    [InlineData("L1-05", "зведено")]
    [InlineData("L1-04b", "—")]
    public void Статус_коду_знахідки_з_рядка_черги(string code, string expected) =>
        Assert.Equal(expected, Status(Result.Value.Findings, code));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Черга_не_змінюється() =>
        Assert.True(Result.Value.QueueUnchanged, "status-from-git.ps1 змінив файл черги");

    private sealed record Run(Dictionary<string, string> Tasks, Dictionary<string, string> Findings, bool QueueUnchanged);

    private static string Status(Dictionary<string, string> rows, string id)
    {
        Assert.True(rows.TryGetValue(id, out var status), $"рядка {id} немає у звіті: {string.Join(", ", rows.Keys)}");
        return status!;
    }

    private static Run Execute()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ecr-status-from-git-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(dir, "repo");
        Directory.CreateDirectory(repo);
        try
        {
            Git(repo, "init", "-q", "-b", "main");
            Commit(repo, "a.txt", "[CODE] AN-1: перша робота\n\nЗакриває L1-04.");
            Commit(repo, "a.txt", "[CODE] без ID у тексті");
            var releasedUntagged = Git(repo, "rev-parse", "--short=8", "HEAD");
            // Лише облікова правка черги: AN-3 у тексті не робить задачу випущеною.
            Commit(repo, "docs/build/WORK-QUEUE.md", "[DOCS] WORK-QUEUE: статуси AN-1, AN-2, AN-3");

            Git(repo, "checkout", "-q", "-b", "dev");
            Commit(repo, "b.txt", "[FIX] AN-2 / L1-05: зведена робота");
            Commit(repo, "b.txt", "[CODE] AN-100 і AN-10b, але не десята");
            // Межі ID — без букв будь-якої абетки: ні WLAN-3, ні AN-6б (кирилична «б») не є AN-3/AN-6.
            Commit(repo, "b.txt", "[CODE] WLAN-3 і AN-6б");
            Commit(repo, "a.txt", "[FIX] AN-1: хвіст після випуску");
            Git(repo, "checkout", "-q", "-b", "side");
            Commit(repo, "c.txt", "[CODE] робота без ID");
            var integratedUntagged = Git(repo, "rev-parse", "--short=8", "HEAD");
            Git(repo, "checkout", "-q", "dev");
            Commit(repo, "d.txt", "[CODE] паралельна правка");
            Git(repo, "merge", "-q", "--no-ff", "side", "-m", "Merge AN-4 into dev");

            var queue = Path.Combine(dir, "WORK-QUEUE.md");
            File.WriteAllText(queue, string.Join('\n',
                "# Черга",
                "",
                "## 1. Розділ",
                "",
                "| № | Задача | Статус | Гілка / хеш |",
                "|---|---|---|---|",
                "| AN-1 (P1) | **перша**, знахідки L1-04, L1-04b | todo | — |",
                "| AN-2 (P1) | друга, L1-05 | doing | — |",
                "| AN-3 | третя | todo | — |",
                "| AN-4 | четверта | todo | — |",
                $"| AN-5 | п'ята | done {integratedUntagged} | — |",
                "| AN-6 | шоста | done deadbeef | — |",
                $"| AN-7 | сьома | done | `{releasedUntagged}` |",
                "| AN-10 | десята | todo | — |",
                "| AN-10b | десята-б | todo | — |",
                ""), new UTF8Encoding(false));
            var before = File.ReadAllBytes(queue);
            var output = Path.Combine(dir, "out", "status.md");

            RunScript(repo, queue, output);

            return Parse(File.ReadAllLines(output, Encoding.UTF8), before.AsSpan().SequenceEqual(File.ReadAllBytes(queue)));
        }
        finally
        {
            DeleteTree(dir);
        }
    }

    // Розділ «## Задачі»: | ID | Статус | …; розділ «## Коди знахідок»: | Код | Задачі | Статус | ….
    private static Run Parse(string[] lines, bool queueUnchanged)
    {
        var tasks = new Dictionary<string, string>(StringComparer.Ordinal);
        var findings = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string>? target = null;
        var statusCell = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("## Задачі", StringComparison.Ordinal))
            {
                (target, statusCell) = (tasks, 2);
            }
            else if (line.StartsWith("## Коди", StringComparison.Ordinal))
            {
                (target, statusCell) = (findings, 3);
            }
            else if (target is not null && line.StartsWith('|') && !line.StartsWith("|---", StringComparison.Ordinal))
            {
                var cells = line.Split('|');
                target[cells[1].Trim()] = cells[statusCell].Trim();
            }
        }

        return new Run(tasks, findings, queueUnchanged);
    }

    private static void RunScript(string repo, string queue, string output)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                     "-File", Path.Combine(SourceTree.Root, "tools", "status-from-git.ps1"),
                     "-RepoRoot", repo, "-Queue", queue, "-Integration", "dev", "-Release", "main", "-OutFile", output,
                 })
        {
            start.ArgumentList.Add(argument);
        }

        var (code, stdout, stderr) = Start(start);
        Assert.True(code == 0, $"status-from-git.ps1: код {code}\n{stderr}\n{stdout}");
    }

    private static void Commit(string repo, string file, string message)
    {
        var path = Path.Combine(repo, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, message + "\n");
        Git(repo, "add", "--", file);
        Git(repo, "commit", "-q", "-m", message);
    }

    private static string Git(string repo, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-C", repo, "-c", "user.name=ecr-test", "-c", "user.email=ecr-test@example.invalid", "-c", "commit.gpgsign=false" }
                     .Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }

        var (code, stdout, stderr) = Start(start);
        Assert.True(code == 0, $"git {string.Join(' ', arguments)}: код {code}\n{stderr}");
        return stdout.Trim();
    }

    private static (int Code, string Stdout, string Stderr) Start(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{start.FileName} не запустився");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{start.FileName} не завершився за 2 хв");
        }

        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    // Об'єкти git — лише для читання; на Windows Directory.Delete на них падає.
    private static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(dir, recursive: true);
    }
}
