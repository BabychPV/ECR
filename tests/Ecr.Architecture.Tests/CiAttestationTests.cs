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

            // ⛔ протилежна умова з'єднана ЛИШЕ через `&&`: `||` зробив би її «або», і повне
            // завдання запускалось би (чи атестація пропускалась) там, де не мусить
            Assert.DoesNotContain("||", condition, StringComparison.Ordinal);
            var at = condition!.IndexOf(FullGuard, StringComparison.Ordinal);
            var before = condition[..at].TrimEnd();
            var after = condition[(at + FullGuard.Length)..].TrimStart();
            Assert.True(
                before.EndsWith("&&", StringComparison.Ordinal) || before.EndsWith("${{", StringComparison.Ordinal),
                $"{file}: у «{id}» перед протилежною умовою має стояти `&&` або початок виразу.");
            Assert.True(
                after.StartsWith("&&", StringComparison.Ordinal) || after.StartsWith("}}", StringComparison.Ordinal),
                $"{file}: у «{id}» після протилежної умови має стояти `&&` або кінець виразу.");
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
        Assert.Contains("(((.head_branch // \"\") | startswith(\"rc/\")) | not)", script, StringComparison.Ordinal);
        // той самий SHA, а не гілка
        Assert.Contains("head_sha=$HEAD_SHA", script, StringComparison.Ordinal);
        Assert.Contains("github.event.pull_request.head.sha", job, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("evidence.yml")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипт_атестації_виходить_нулем_лише_на_success_і_відхиляє_форки(string file)
    {
        var job = Jobs(file).Single(j => j.Id == "attest").Body;
        var script = Script(job);

        Assert.StartsWith("set -euo pipefail", script, StringComparison.Ordinal);

        // єдиний `exit 0` — усередині гілки `success*)`; жодної гілки-«усе інше» (`*)`)
        Assert.Single(Regex.Matches(script, @"exit\s+0\b"));
        Assert.Matches(@"(?ms)^\s*success\*\)\s*\n[^\n]*\n\s*exit 0\s*\n\s*;;\s*\n\s*esac", script);
        Assert.DoesNotMatch(@"(?m)^\s*\*\)", script);
        Assert.DoesNotMatch(@"(?m)^\s*\S*\|\s*\*\)", script);

        // перевірка форка: умова + вихід 1, і джерело змінної — репозиторій head PR
        Assert.Matches(
            @"(?ms)if \[ ""\$HEAD_REPO"" != ""\$REPO"" \]; then\s*\n\s*echo [^\n]*\n\s*exit 1\s*\n\s*fi",
            script);
        Assert.Contains("HEAD_REPO: ${{ github.event.pull_request.head.repo.full_name }}", job, StringComparison.Ordinal);
        Assert.Contains("REPO=\"$GITHUB_REPOSITORY\"", script, StringComparison.Ordinal);
        Assert.Matches(@"(?ms)if \[ -z ""\$HEAD_SHA"" \]; then\s*\n\s*echo [^\n]*\n\s*exit 1\s*\n\s*fi", script);

        // кінець скрипта — помилка й `exit 1`, а не успіх
        var last = script.Split('\n').Last(l => l.Trim().Length > 0).Trim();
        Assert.Equal("exit 1", last);
    }

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("evidence.yml")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Атестація_чекає_і_завершені_прогони_і_їх_відсутність_до_межі_очікування(string file)
    {
        var job = Jobs(file).Single(j => j.Id == "attest").Body;
        var script = Script(job);

        Assert.Contains(
            "if { [ \"$pending\" -gt 0 ] || [ \"$total\" -eq 0 ]; } && [ \"$(date +%s)\" -lt \"$deadline\" ]; then",
            script,
            StringComparison.Ordinal);

        var wait = Regex.Match(job, @"ATTEST_WAIT_MIN:\s*'(?<m>\d+)'");
        Assert.True(wait.Success, $"{file}: немає ATTEST_WAIT_MIN.");
        var minutes = int.Parse(wait.Groups["m"].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(minutes, 1, 15);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Імена_повних_завдань_на_rc_відмінні_від_атестацій_а_на_інших_подіях_незмінні()
    {
        // (1) імена, які push-прогін повних завдань дає за не-rc події, і ті, що чекає атестація.
        var ciJobs = Jobs("ci.yml");
        var evidenceJobs = Jobs("evidence.yml");

        var themes = Matrix(ciJobs.Single(j => j.Id == "a11y").Body, "theme");
        Assert.Equal("dark,light", string.Join(',', themes.OrderBy(x => x, StringComparer.Ordinal)));

        var pushNames = new HashSet<string>(StringComparer.Ordinal);
        var rcNames = new HashSet<string>(StringComparer.Ordinal);
        var attestNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (jobs, ids) in new[]
                 {
                     (ciJobs, new[] { "server", "client", "a11y", "windows" }),
                     (evidenceJobs, new[] { "build", "test" }),
                 })
        {
            foreach (var id in ids)
            {
                var name = SingleLine(jobs.Single(j => j.Id == id).Body, "name");
                Assert.NotNull(name);
                var m = Regex.Match(
                    name!,
                    @"^\$\{\{ \(github\.event_name == 'pull_request' && startsWith\(github\.head_ref, 'rc/'\)\) && '(?<rc>[^']+)' \|\| (?<other>'[^']+'|format\('a11y \(\{0\}\)', matrix\.theme\)) \}\}$");
                Assert.True(m.Success, $"«{id}»: ім'я не має форми «rc ? інше-ім'я : звичайне». Фактично: {name}");

                var rc = m.Groups["rc"].Value;
                Assert.Equal($"{(id == "a11y" ? "a11y" : id)} (повний, пропущено на rc)", rc);

                if (id == "a11y")
                {
                    Assert.Equal("format('a11y ({0})', matrix.theme)", m.Groups["other"].Value);
                    foreach (var t in themes)
                    {
                        pushNames.Add($"a11y ({t})");
                    }
                }
                else
                {
                    Assert.Equal($"'{id}'", m.Groups["other"].Value);
                    pushNames.Add(id);
                }

                rcNames.Add(rc);
            }
        }

        foreach (var jobs in new[] { ciJobs, evidenceJobs })
        {
            foreach (var n in Matrix(jobs.Single(j => j.Id == "attest").Body, "check"))
            {
                attestNames.Add(n);
            }
        }

        // атестація шукає ті імена, які справді дають повні завдання, і навпаки
        Assert.Subset(pushNames, attestNames);
        Assert.Subset(attestNames, pushNames);
        // кожне обов'язкове атестоване ім'я присутнє
        foreach (var name in Required.Except(NotAttested))
        {
            Assert.Contains(name, attestNames);
            Assert.Contains(name, pushNames);
        }

        // skipped-близнюк на rc-PR не має спільного імені з атестацією
        Assert.Empty(rcNames.Intersect(attestNames, StringComparer.Ordinal));
        Assert.Empty(rcNames.Intersect(Required, StringComparer.Ordinal));
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

    /// <summary>Елементи рядка матриці <c>key: [a, b]</c> (лапки одинарні або без них).</summary>
    private static string[] Matrix(string body, string key)
    {
        var m = Regex.Match(body, @"(?m)^\s+" + Regex.Escape(key) + @":\s*\[(?<l>[^\]]*)\]");
        Assert.True(m.Success, $"немає матриці {key}.");
        return m.Groups["l"].Value
            .Split(',')
            .Select(x => x.Trim().Trim('\''))
            .Where(x => x.Length > 0)
            .ToArray();
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
