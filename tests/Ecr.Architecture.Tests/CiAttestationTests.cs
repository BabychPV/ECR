using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Атестація замість важких завдань на PR <c>rc/**</c> → <c>main</c> (CI-швидкість, 2026-10-12).
/// </summary>
/// <remarks>
/// ⛔ На PR з <c>rc/**</c> важкі завдання не запускаються, а пропущену обов'язкову перевірку
/// GitHub зараховує як пройдену. Тому замість кожного з них іде завдання з ТИМ САМИМ ім'ям, що
/// питає API, чи є на тому ж SHA зелений повний прогін. Цей сторож тримає чотири речі, зламавши
/// будь-яку з яких, перевірка тихо перетворилась би на «пропущено = зелено»:
/// (1) кожне обов'язкове ім'я має атестацію з умовою на <c>head_ref</c> <c>rc/</c>;
/// (2) кожне повне завдання має ПРОТИЛЕЖНУ умову; (3) в атестації немає способу позеленіти
/// без прогону (<c>|| true</c>, <c>continue-on-error</c>, <c>always()</c>); (4) скрипт обох
/// копій (ci.yml, evidence.yml) однаковий і приймає лише <c>conclusion == "success"</c>
/// на подіях не-PR (або PR не з <c>rc/</c>).
///
/// ⚠ Читає ТЕКСТ workflow, а не запускає його — як решта сторожів цього каталогу.
/// </remarks>
public sealed class CiAttestationTests
{
    private const string AttestIf =
        "${{ github.event_name == 'pull_request' && startsWith(github.head_ref, 'rc/') }}";

    private const string FullGuard =
        "!(github.event_name == 'pull_request' && startsWith(github.head_ref, 'rc/'))";

    /// <summary>Обов'язкові перевірки <c>main</c> (ruleset), які сторож знає поіменно.</summary>
    private static readonly string[] Required =
    [
        "build", "test", "honesty-guard", "server", "client", "a11y (dark)", "a11y (light)", "pr-source",
    ];

    /// <summary>Обов'язкові імена, що НЕ атестуються, і чому (див. коментарі в workflow).</summary>
    private static readonly string[] NotAttested = ["honesty-guard", "pr-source"];

    /// <summary>Атестації ci.yml: імена == матриця <c>check</c> (<c>windows</c> — не обов'язкове, але зведене).</summary>
    private static readonly string[] CiAttested = ["server", "client", "a11y (light)", "a11y (dark)", "windows"];

    private static readonly string[] EvidenceAttested = ["build", "test"];

    /// <summary>Повні завдання ci.yml, які мусять мати протилежну умову.</summary>
    private static readonly string[] CiFullJobs =
        ["server-part", "server", "client", "a11y", "worker-windows", "msi-windows", "windows"];

    private static readonly string[] EvidenceFullJobs = ["build", "test"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожне_обовязкове_імя_або_атестується_або_названо_винятком()
    {
        var attested = CiAttested.Concat(EvidenceAttested).Concat(NotAttested).ToHashSet(StringComparer.Ordinal);

        foreach (var name in Required)
        {
            Assert.True(
                attested.Contains(name),
                $"Обов'язкова перевірка «{name}» не має атестації й не названа винятком у NotAttested.");
        }
    }

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("evidence.yml")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Атестація_має_умову_head_ref_rc_і_ім_я_з_матриці(string file)
    {
        var job = Jobs(file).Single(j => j.Id == "attest").Body;

        Assert.Equal(AttestIf, SingleLine(job, "if"));
        Assert.Equal("${{ matrix.check }}", SingleLine(job, "name"));

        var expected = file == "ci.yml" ? CiAttested : EvidenceAttested;
        var matrix = Regex.Match(job, @"(?m)^\s+check:\s*\[(?<l>[^\]]*)\]");
        Assert.True(matrix.Success, $"{file}: attest без матриці check.");
        var actual = Regex.Matches(matrix.Groups["l"].Value, "'([^']*)'").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), actual.OrderBy(x => x, StringComparer.Ordinal));

        Assert.Contains($"WORKFLOW_FILE: {file}", job, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^\s+actions:\s*read\s*$", job);
    }

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("evidence.yml")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Повні_завдання_мають_протилежну_умову(string file)
    {
        var expected = file == "ci.yml" ? CiFullJobs : EvidenceFullJobs;
        var jobs = Jobs(file);

        foreach (var id in expected)
        {
            var job = jobs.SingleOrDefault(j => j.Id == id);
            Assert.True(job is not null, $"{file}: немає повного завдання «{id}».");
            var condition = SingleLine(job!.Body, "if");
            Assert.True(
                condition is not null && condition.Contains(FullGuard, StringComparison.Ordinal),
                $"{file}: у «{id}» умова `if` не містить протилежної `{FullGuard}` — на PR rc/** обидва завдання (повне й атестація) або жодне.");
            Assert.DoesNotContain("|| true", condition, StringComparison.Ordinal);
            Assert.DoesNotContain("continue-on-error", job.Body, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("evidence.yml")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Атестація_не_може_позеленіти_без_прогону(string file)
    {
        var job = Jobs(file).Single(j => j.Id == "attest").Body;

        Assert.DoesNotContain("continue-on-error", job, StringComparison.Ordinal);
        Assert.DoesNotContain("|| true", job, StringComparison.Ordinal);
        Assert.DoesNotContain("always()", job, StringComparison.Ordinal);
        Assert.DoesNotContain("failure()", job, StringComparison.Ordinal);

        var script = Script(job);
        Assert.Contains("exit 1", script, StringComparison.Ordinal);
        // приймається лише success; skipped/cancelled/neutral не годяться
        Assert.Contains(".conclusion == \"success\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("skipped", script, StringComparison.Ordinal);
        Assert.DoesNotContain("neutral", script, StringComparison.Ordinal);
        // події: push / dispatch / schedule, а PR — лише не з rc/ (сама атестація не є «повним прогоном»)
        Assert.Contains(".event == \"push\"", script, StringComparison.Ordinal);
        Assert.Contains("(.head_branch | startswith(\"rc/\")) | not", script, StringComparison.Ordinal);
        // той самий SHA, а не гілка
        Assert.Contains("head_sha=$HEAD_SHA", script, StringComparison.Ordinal);
        Assert.Contains("github.event.pull_request.head.sha", job, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипт_атестації_в_обох_workflow_однаковий()
    {
        var ci = Script(Jobs("ci.yml").Single(j => j.Id == "attest").Body);
        var evidence = Script(Jobs("evidence.yml").Single(j => j.Id == "attest").Body);

        Assert.Equal(ci, evidence);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Honesty_guard_і_pr_source_лишаються_справжніми()
    {
        // honesty-guard: на PR перевіряє diff усього тижня проти main, чого push-прогін не робить;
        // pr-source: власна умова dev/integration. Обидва не повинні ховатись за rc/.
        var evidence = Jobs("evidence.yml").Single(j => j.Id == "honesty-guard").Body;
        var ci = Jobs("ci.yml").Single(j => j.Id == "pr-source").Body;

        Assert.Null(SingleLine(evidence, "if"));
        Assert.DoesNotContain("head_ref, 'rc/'", ci, StringComparison.Ordinal);
    }

    private sealed record JobBlock(string Id, string Body);

    private static List<JobBlock> Jobs(string file)
    {
        var text = File.ReadAllText(Path.Combine(Root(), ".github", "workflows", file))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = text.Split('\n');
        var start = Array.IndexOf(lines, "jobs:");
        Assert.True(start >= 0, $"{file}: немає секції jobs:.");

        var result = new List<JobBlock>();
        string? id = null;
        var body = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var header = Regex.Match(lines[i], @"^  ([a-z][a-z0-9-]*):\s*$");
            if (header.Success)
            {
                if (id is not null)
                {
                    result.Add(new JobBlock(id, string.Join('\n', body)));
                }

                id = header.Groups[1].Value;
                body = [];
                continue;
            }

            if (id is not null && !lines[i].TrimStart().StartsWith('#'))
            {
                body.Add(lines[i]);
            }
        }

        if (id is not null)
        {
            result.Add(new JobBlock(id, string.Join('\n', body)));
        }

        return result;
    }

    /// <summary>Однорядкове значення ключа рівня завдання (4 пробіли відступу), або null.</summary>
    private static string? SingleLine(string body, string key)
    {
        var m = Regex.Match(body, @"(?m)^    " + Regex.Escape(key) + @":\s*(?<v>.+?)\s*$");
        return m.Success ? m.Groups["v"].Value : null;
    }

    /// <summary>Блок <c>run: |</c> кроку атестації, без відступу.</summary>
    private static string Script(string job)
    {
        var lines = job.Split('\n');
        var i = Array.FindIndex(lines, l => l.Trim() == "run: |");
        Assert.True(i >= 0, "attest: немає кроку `run: |`.");
        var indent = lines[i].Length - lines[i].TrimStart().Length + 2;
        var script = new List<string>();
        for (var j = i + 1; j < lines.Length; j++)
        {
            if (lines[j].Trim().Length > 0 && lines[j].Length - lines[j].TrimStart().Length < indent)
            {
                break;
            }

            script.Add(lines[j].Length >= indent ? lines[j][indent..] : lines[j].Trim());
        }

        return string.Join('\n', script).TrimEnd();
    }

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
