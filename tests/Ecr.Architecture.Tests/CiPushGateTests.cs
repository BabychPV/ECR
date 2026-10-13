using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Сторож `gate`: push у <c>dev/integration</c> із вершиною, що вже в <c>main</c>, не має запускати
/// повні прогони (CI-швидкість, 2026-10-13), а <c>e2e-stand</c> не стартує на push.
/// </summary>
/// <remarks>
/// ⛔ Тримає три речі: (1) кожне важке завдання <c>ci.yml</c>/<c>evidence.yml</c>/<c>smoke.yml</c>
/// залежить від <c>gate</c> і має в <c>if</c> умову <c>needs.gate.outputs.skip != 'true'</c> — без цього
/// пропуск мовчки перестав би працювати; (2) <c>gate</c> пропускає ЛИШЕ push у <c>dev/integration</c>
/// (lane/**, integ-*, PR, main, dispatch, schedule — завжди <c>skip=false</c>) і відкритий при помилці
/// API; (3) <c>e2e-stand.yml</c> не має <c>push:</c>. ⚠ Читає ТЕКСТ workflow, як решта сторожів.
/// </remarks>
public sealed class CiPushGateTests
{
    private const string SkipCondition = "needs.gate.outputs.skip != 'true'";

    private static readonly (string File, string[] Heavy)[] Workflows =
    [
        ("ci.yml", ["server-part", "server", "client", "a11y", "worker-windows", "msi-windows", "windows"]),
        ("evidence.yml", ["build", "test"]),
        ("smoke.yml", ["smoke"]),
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void E2e_stand_не_запускається_на_push()
    {
        var text = Read("e2e-stand.yml");
        var start = text.IndexOf("\non:\n", StringComparison.Ordinal);
        var end = text.IndexOf("\npermissions:", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "e2e-stand.yml: немає блоку on: перед permissions:.");

        var on = string.Join(
            '\n',
            text[start..end].Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

        Assert.DoesNotMatch(@"(?m)^  push:", on);
        Assert.DoesNotContain("schedule:", on, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^  workflow_dispatch:", on);
        Assert.Matches(@"(?m)^  pull_request:", on);
    }

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("evidence.yml")]
    [InlineData("smoke.yml")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Gate_пропускає_лише_push_у_dev_integration_вершина_якого_уже_в_main(string file)
    {
        var gate = Jobs(file).Single(j => j.Id == "gate").Body;

        // gate виконується завжди (без `if`), а без нього залежні `needs` не побачили б виходу
        Assert.DoesNotMatch(@"(?m)^    if:", gate);
        Assert.DoesNotContain("continue-on-error", gate, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^      skip: \$\{\{ steps\.g\.outputs\.skip \}\}\s*$", gate);

        // за замовчуванням — НЕ пропускати; пропуск лише всередині гілки push у dev/integration
        Assert.Contains("skip=false", gate, StringComparison.Ordinal);
        Assert.Contains(
            "if [ \"$EVENT_NAME\" = \"push\" ] && [ \"$REF\" = \"refs/heads/dev/integration\" ]; then",
            gate,
            StringComparison.Ordinal);

        // «вершина вже в main» == compare main...SHA дає identical|behind (семантика merge-base --is-ancestor)
        Assert.Contains("compare/main...$SHA", gate, StringComparison.Ordinal);
        Assert.Contains("identical|behind) skip=true ;;", gate, StringComparison.Ordinal);
        // помилка API не пропускає прогін (відкрито)
        Assert.Contains("|| true", gate, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(gate, @"skip=true"));
    }

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("evidence.yml")]
    [InlineData("smoke.yml")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожне_важке_завдання_залежить_від_gate_і_пропускається_за_його_виходом(string file)
    {
        var heavy = Workflows.Single(w => w.File == file).Heavy;
        var jobs = Jobs(file);

        foreach (var id in heavy)
        {
            var body = jobs.SingleOrDefault(j => j.Id == id)?.Body;
            Assert.True(body is not null, $"{file}: немає завдання «{id}».");

            var needs = Regex.Match(body!, @"(?m)^    needs:\s*(?<v>.+?)\s*$");
            Assert.True(needs.Success, $"{file}: у «{id}» немає `needs:`.");
            Assert.Matches(@"(^|[\s\[,])gate([\s\],]|$)", needs.Groups["v"].Value);

            var condition = Regex.Match(body!, @"(?m)^    if:\s*(?<v>.+?)\s*$");
            Assert.True(condition.Success, $"{file}: у «{id}» немає `if:`.");
            Assert.Contains(SkipCondition, condition.Groups["v"].Value, StringComparison.Ordinal);
            Assert.DoesNotContain("||", condition.Groups["v"].Value, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("evidence.yml")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Атестація_не_залежить_від_gate(string file)
    {
        // attest йде лише на PR rc/**; gate на ньому завжди skip=false, але залежність зайва й
        // могла б зробити атестацію «skipped» при падінні gate — тобто хибно пройденою.
        var attest = Jobs(file).Single(j => j.Id == "attest").Body;
        Assert.DoesNotMatch(@"\bgate\b", attest);
    }

    private sealed record JobBlock(string Id, string Body);

    private static List<JobBlock> Jobs(string file)
    {
        var lines = Read(file).Split('\n');
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

    private static string Read(string file)
        => File.ReadAllText(Path.Combine(Root(), ".github", "workflows", file))
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
