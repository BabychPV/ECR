using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Сторож «e2e-stand без споживача» (ретро #8, 2026-10-13): прогін e2e на PR `rc/**` іде 23-38 хв
/// і не повинен жити, коли PR змержено/закрито чи вийшов новіший пуш.
/// </summary>
/// <remarks>
/// ⛔ Тримає: (1) concurrency-група прив'язана до номера PR, а <c>cancel-in-progress</c> увімкнено для
/// <c>pull_request</c> — новий пуш скасовує попередній run; (2) завдання <c>cancel-when-closed</c>
/// опитує стан PR і скасовує run, якщо він не <c>open</c> (GitHub цього сам не робить); (3) ім'я
/// завдання <c>e2e-stand (linux)</c>, за яким стежить спостерігач, не змінилось; (4) <c>push:</c> /
/// <c>schedule:</c> не з'явились. ⚠ Читає ТЕКСТ workflow, як решта сторожів; обов'язкових
/// перевірок <c>main</c> цей workflow не торкається.
/// </remarks>
public sealed class CiE2eStandCancelTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Concurrency_групується_за_номером_PR_і_скасовує_попередній_run()
    {
        var text = Read();
        var start = text.IndexOf("\nconcurrency:\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "e2e-stand.yml: немає верхньорівневого concurrency.");
        var end = text.IndexOf("\nenv:", start, StringComparison.Ordinal);
        Assert.True(end > start, "e2e-stand.yml: concurrency має стояти перед env:.");
        var block = Code(text[start..end]);

        var group = Regex.Match(block, @"(?m)^  group:\s*(?<v>.+?)\s*$");
        Assert.True(group.Success, "concurrency без group.");
        Assert.Contains("github.event.pull_request.number", group.Groups["v"].Value, StringComparison.Ordinal);

        var cancel = Regex.Match(block, @"(?m)^  cancel-in-progress:\s*(?<v>.+?)\s*$");
        Assert.True(cancel.Success, "concurrency без cancel-in-progress.");
        Assert.NotEqual("false", cancel.Groups["v"].Value.Trim());
        Assert.Contains("pull_request", cancel.Groups["v"].Value, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Спостерігач_скасовує_run_коли_PR_закрито()
    {
        var text = Read();
        var e2e = Job(text, "e2e");
        Assert.Matches(@"(?m)^    name: e2e-stand \(linux\)\s*$", e2e);

        var watcher = Job(text, "cancel-when-closed");

        // лише PR із цього ж репозиторію й лише rc/** — як і саме e2e
        var condition = Regex.Match(watcher, @"(?ms)^    if:\s*(?<v>.+?)^    runs-on:");
        Assert.True(condition.Success, "cancel-when-closed: немає if:.");
        Assert.Contains("github.event_name == 'pull_request'", condition.Groups["v"].Value, StringComparison.Ordinal);
        Assert.Contains("head.repo.full_name == github.repository", condition.Groups["v"].Value, StringComparison.Ordinal);
        Assert.Contains("startsWith(github.head_ref, 'rc/')", condition.Groups["v"].Value, StringComparison.Ordinal);

        // права скасовувати run і читати PR — лише на цьому завданні
        Assert.Matches(@"(?m)^    permissions:\s*\n(\s+.+\n)*?\s+actions: write\s*$", watcher);
        Assert.Contains("pull-requests: read", watcher, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^    needs:", watcher);

        // опитування стану PR і скасування, коли стан не open
        Assert.Contains("repos/$REPO/pulls/$PR", watcher, StringComparison.Ordinal);
        Assert.Matches(@"\[ ""\$state"" != ""open"" \]", watcher);
        Assert.Contains("gh run cancel \"$RUN_ID\"", watcher, StringComparison.Ordinal);

        // зупиняється, щойно завершилось саме e2e-завдання
        Assert.Contains("e2e-stand (linux)", watcher, StringComparison.Ordinal);
        Assert.Contains("= \"completed\"", watcher, StringComparison.Ordinal);

        // права на рівні workflow лишаються мінімальними
        var top = text[..text.IndexOf("\nconcurrency:\n", StringComparison.Ordinal)];
        Assert.DoesNotContain("actions: write", top, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void E2e_stand_і_далі_без_push_і_schedule()
    {
        var text = Read();
        var end = text.IndexOf("\npermissions:", StringComparison.Ordinal);
        var on = Code(text[..end]);

        Assert.DoesNotMatch(@"(?m)^  push:", on);
        Assert.DoesNotContain("schedule:", on, StringComparison.Ordinal);
    }

    private static string Job(string text, string id)
    {
        var lines = text.Split('\n');
        var start = Array.IndexOf(lines, "jobs:");
        Assert.True(start >= 0, "e2e-stand.yml: немає jobs:.");

        var body = new List<string>();
        var inside = false;
        for (var i = start + 1; i < lines.Length; i++)
        {
            var header = Regex.Match(lines[i], @"^  ([a-z][a-z0-9-]*):\s*$");
            if (header.Success)
            {
                if (inside)
                {
                    break;
                }

                inside = header.Groups[1].Value == id;
                continue;
            }

            if (inside && !lines[i].TrimStart().StartsWith('#'))
            {
                body.Add(lines[i]);
            }
        }

        Assert.True(inside && body.Count > 0, $"e2e-stand.yml: немає завдання «{id}».");
        return string.Join('\n', body);
    }

    private static string Code(string block)
        => string.Join('\n', block.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    private static string Read()
        => File.ReadAllText(Path.Combine(Root(), ".github", "workflows", "e2e-stand.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }
}
