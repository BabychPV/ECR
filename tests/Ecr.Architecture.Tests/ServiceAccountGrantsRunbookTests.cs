using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// DB-1: перелік прав облікового запису служби — один (<see cref="ServiceAccountGrants"/>), і він збігається з runbook
/// та з тим, що застосунок насправді викликає.
/// </summary>
/// <remarks>
/// ⛔ Права видає DBA за абзацом «Права облікового запису служби» в <c>docs/admin/operations-runbook.md</c>, а тест
/// найменших прав (<c>ServiceAccountLeastPrivilegeTests</c>) ганяє застосунок під <see cref="ServiceAccountGrants"/>.
/// Розійдуться вони — тест зелений, а служба замовника впаде на першому записі. Тому тут три звірки:
/// (1) runbook ↔ перелік (TVP-типи, процедури, функції — в обидва боки); (2) кожен <c>CREATE TYPE … AS TABLE</c>
/// у <c>Sql/*.sql</c> і кожна послідовність моделі EF — у переліку; (3) кожна процедура, яку код виконує
/// (<c>"EXEC схема.usp_…"</c>), і кожна скалярна функція, яку код викликає у власних запитах, — у переліку.
/// Новий тип/процедура/функція без рядка в runbook — червоне тут, а не у замовника.
/// </remarks>
public sealed partial class ServiceAccountGrantsRunbookTests
{
    /// <summary>Процедури, текст виклику яких застосунок лише ПОКАЗУЄ DBA, а не виконує.</summary>
    private static readonly Dictionary<string, string> NotExecutedByApplication = new(StringComparer.Ordinal)
    {
        ["arc.usp_EnsurePartitions"] = "GetPartitionScriptHandler.Command — текст для DBA (D15-12); виконує SQL Agent під своїм principal",
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Runbook_називає_рівно_ті_типи_процедури_й_функції_що_й_перелік_прав()
    {
        var paragraph = RunbookParagraph();
        var named = BacktickedObject().Matches(paragraph).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var granted = ServiceAccountGrants.TableTypes.Concat(ServiceAccountGrants.Procedures).Concat(ServiceAccountGrants.Functions)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            named.SetEquals(granted),
            $"runbook без переліку: [{string.Join(", ", named.Except(granted))}]; перелік без runbook: [{string.Join(", ", granted.Except(named))}]");

        foreach (var role in ServiceAccountGrants.Roles)
        {
            Assert.Contains(role, paragraph, StringComparison.Ordinal);
        }

        Assert.Contains("UPDATE` на послідовностях", paragraph, StringComparison.Ordinal);
        Assert.Contains("ServiceAccountLeastPrivilegeTests", paragraph, StringComparison.Ordinal);
        Assert.DoesNotContain("не перевірено", paragraph, StringComparison.Ordinal);
    }

    /// <summary>Документи, де DBA мусить знайти ПОВНИЙ скрипт прав (рішення людини 02.11: «у інструкцію встановлення»).</summary>
    private static readonly string[] InstallDocs =
    [
        "docs/admin/operations-runbook.md",
        "docs/build/11-install-guide.md",
        "docs/delivery/QUICKSTART-FIRST-TEST.md",
    ];

    private const string BlockStart = "<!-- generated:grants -->";

    private const string BlockEnd = "<!-- /generated:grants -->";

    private const string FixCommand = "powershell -File tools/gen-install-docs.ps1";

    /// <summary>
    /// Скрипт прав в інструкціях встановлення ГЕНЕРУЄТЬСЯ з <see cref="ServiceAccountGrants"/> (між маркерами) і тут
    /// звіряється з переліком у пам'яті. Розбіжність — червоне з командою виправлення.
    /// </summary>
    /// <remarks>
    /// ⚠ Режим генерації: змінна <c>ECR_REGENERATE_DOCS=1</c> (її ставить <c>tools/gen-install-docs.ps1</c>) — тест
    /// переписує блоки між маркерами замість звірки. У CI змінної немає — лише звірка.
    /// ⛔ Будь-яка копія повного скрипту ПОЗА маркерами в <c>docs/**</c> (крім <c>docs/release-notes</c>, де лише нові
    /// права) — теж червоне: ручна копія старіє першою.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипт_прав_в_інструкціях_встановлення_згенеровано_з_переліку_прав()
    {
        var regenerate = Environment.GetEnvironmentVariable("ECR_REGENERATE_DOCS") == "1";
        var expected = $"{BlockStart}\n```sql\n{ServiceAccountGrants.InstallScript()}```\n{BlockEnd}";
        var root = SolutionRoot();
        var docs = Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();

        var problems = new List<string>();
        foreach (var doc in docs.Where(d => InstallDocs.Contains(d, StringComparer.Ordinal)
                                            || File.ReadAllText(Path.Combine(root, d)).Contains(BlockStart, StringComparison.Ordinal)))
        {
            var path = Path.Combine(root, doc);
            var text = File.ReadAllText(path);
            var crlf = text.Contains("\r\n", StringComparison.Ordinal);
            var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            var start = normalized.IndexOf(BlockStart, StringComparison.Ordinal);
            var end = normalized.IndexOf(BlockEnd, StringComparison.Ordinal);
            if (start < 0 || end < start || normalized.IndexOf(BlockStart, start + 1, StringComparison.Ordinal) >= 0)
            {
                problems.Add($"{doc}: має бути рівно один блок {BlockStart} … {BlockEnd}");
                continue;
            }

            var actual = normalized[start..(end + BlockEnd.Length)];
            if (actual == expected)
            {
                continue;
            }

            if (regenerate)
            {
                var updated = normalized[..start] + expected + normalized[(end + BlockEnd.Length)..];
                File.WriteAllText(path, crlf ? updated.Replace("\n", "\r\n", StringComparison.Ordinal) : updated);
                continue;
            }

            problems.Add($"{doc}: скрипт прав між маркерами розійшовся з ServiceAccountGrants");
        }

        // Ручні копії повного скрипту поза маркерами.
        foreach (var doc in docs.Where(d => !d.StartsWith("docs/release-notes/", StringComparison.Ordinal)))
        {
            var normalized = File.ReadAllText(Path.Combine(root, doc)).Replace("\r\n", "\n", StringComparison.Ordinal);
            var outside = Regex.Replace(normalized, Regex.Escape(BlockStart) + "[\\s\\S]*?" + Regex.Escape(BlockEnd), string.Empty);
            if (ScriptLineCopy().IsMatch(outside))
            {
                problems.Add($"{doc}: ручна копія скрипту прав поза маркерами — замінити на {BlockStart} {BlockEnd}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems) + Environment.NewLine + "Виправлення: " + FixCommand);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_табличний_тип_і_кожна_послідовність_схеми_є_в_переліку_прав()
    {
        var sql = Path.Combine(SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql");
        var types = Directory.EnumerateFiles(sql, "*.sql", SearchOption.AllDirectories)
            .SelectMany(f => TableTypeDeclaration().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(types);
        Assert.True(
            types.SetEquals(ServiceAccountGrants.TableTypes),
            $"типи у Sql: [{string.Join(", ", types)}]; у переліку: [{string.Join(", ", ServiceAccountGrants.TableTypes)}]");

        var snapshot = File.ReadAllText(Path.Combine(
            SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Migrations", "EcrDbContextModelSnapshot.cs"));
        var sequences = SequenceDeclaration().Matches(snapshot).Select(m => $"{m.Groups[2].Value}.{m.Groups[1].Value}")
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(sequences);
        Assert.True(
            sequences.SetEquals(ServiceAccountGrants.Sequences),
            $"послідовності моделі: [{string.Join(", ", sequences)}]; у переліку: [{string.Join(", ", ServiceAccountGrants.Sequences)}]");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожна_процедура_й_функція_яку_викликає_код_є_в_переліку_прав()
    {
        var code = Directory.EnumerateFiles(Path.Combine(SolutionRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();
        var granted = ServiceAccountGrants.Procedures.Concat(ServiceAccountGrants.Functions).ToHashSet(StringComparer.Ordinal);

        var executed = code.SelectMany(text => ExecutedProcedure().Matches(text).Select(m => m.Groups[1].Value))
            .Where(p => !NotExecutedByApplication.ContainsKey(p))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(executed);

        // Скалярні функції бази (створює міграція), на які посилається код поза міграціями: SQL-текст або HasDbFunction.
        var migrations = Path.Combine(SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Migrations");
        var functions = Directory.EnumerateFiles(migrations, "*.cs")
            .SelectMany(f => FunctionDeclaration().Matches(File.ReadAllText(f)).Select(m => (Schema: m.Groups[1].Value, Name: m.Groups[2].Value)))
            .Distinct()
            .Where(fn => code.Any(text => text.Contains($"{fn.Schema}.{fn.Name}(", StringComparison.Ordinal)
                                          || text.Contains($"HasSchema(\"{fn.Schema}\").HasName(\"{fn.Name}\")", StringComparison.Ordinal)))
            .Select(fn => $"{fn.Schema}.{fn.Name}");

        var missing = executed.Concat(functions).Where(o => !granted.Contains(o)).Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.True(
            missing.Count == 0,
            $"Застосунок викликає, а EXECUTE в переліку прав (і в runbook) немає: {string.Join(", ", missing)}. "
            + "Додайте в ServiceAccountGrants і в абзац «Права облікового запису служби» runbook.");
    }

    private static string RunbookParagraph()
    {
        var runbook = File.ReadAllLines(Path.Combine(SolutionRoot(), "docs", "admin", "operations-runbook.md"));
        var lines = runbook.Where(l => l.StartsWith("**Права облікового запису служби**", StringComparison.Ordinal)).ToList();
        Assert.Single(lines);
        return lines[0];
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

    /// <summary>Рядок СКРИПТУ (не згадка в тексті): повна копія прав починається з ролей і TVP.</summary>
    [GeneratedRegex(@"^\s*(GRANT EXECUTE ON TYPE::|ALTER ROLE db_datawriter ADD MEMBER)", RegexOptions.Multiline)]
    private static partial Regex ScriptLineCopy();

    [GeneratedRegex(@"`(\w+\.(?:\w+Tvp|usp_\w+|fn_?\w+))`")]
    private static partial Regex BacktickedObject();

    [GeneratedRegex(@"CREATE\s+TYPE\s+(\w+\.\w+)\s+AS\s+TABLE", RegexOptions.IgnoreCase)]
    private static partial Regex TableTypeDeclaration();

    [GeneratedRegex(@"HasSequence\(""(\w+)"",\s*""(\w+)""\)")]
    private static partial Regex SequenceDeclaration();

    [GeneratedRegex(@"""\s*EXEC(?:UTE)?\s+(\w+\.usp_\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutedProcedure();

    [GeneratedRegex(@"CREATE\s+(?:OR\s+ALTER\s+)?FUNCTION\s+\[?(\w+)\]?\.\[?(\w+)\]?", RegexOptions.IgnoreCase)]
    private static partial Regex FunctionDeclaration();
}
