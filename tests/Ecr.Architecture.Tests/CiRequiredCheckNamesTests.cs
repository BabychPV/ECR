using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Обов'язкові перевірки <c>main</c> на PR <c>rc/**</c> → <c>main</c> без завдань-атестацій (B159, 2026-11-16).
/// </summary>
/// <remarks>
/// ⛔ На PR з <c>rc/**</c> повні завдання пропущені протилежною умовою на <c>head_ref</c>, і нічого їх не
/// замінює. Безпека тримається на трьох фактах: (1) обов'язкові перевірки GitHub рахує по SHA коміту, тож
/// зелені check-run push-прогону того ж SHA — це вердикт PR; (2) завдання, пропущене своїм <c>if</c>,
/// <c>name:</c> не обчислює — його check-run зветься сирим текстом виразу, а не обов'язковим ім'ям;
/// (3) отже перевірка з обов'язковим ім'ям є на SHA лише тоді, коли повне завдання справді відпрацювало,
/// інакше PR заблокований (fail-closed). Зламати це можна одним способом: дати обов'язкове ім'я ще
/// якомусь завданню — пропущене завдання з ПРОСТИМ ім'ям GitHub зараховує як пройдене. Цей сторож
/// тримає: (a) кожне обов'язкове ім'я, крім <c>honesty-guard</c> і <c>pr-source</c>, дає РІВНО ОДНЕ
/// повне завдання з іменем-виразом; (b) повні завдання мають протилежну умову лише через <c>&amp;&amp;</c>;
/// (c) жодне інше завдання жодного workflow не може дати обов'язкове ім'я (простий <c>name:</c>, id без
/// <c>name:</c>, <c>matrix.*</c>, будь-який вираз, що МОЖЕ дати це ім'я); (d) атестацій немає.
///
/// ⚠ Читає ТЕКСТ workflow, а не запускає його — як решта сторожів цього каталогу. Розбір навмисно
/// консервативний: вираз, значення якого сторож не може вивести, вважається здатним дати БУДЬ-ЯКЕ ім'я.
/// </remarks>
public sealed class CiRequiredCheckNamesTests
{
    private const string RcPr = "(github.event_name == 'pull_request' && startsWith(github.head_ref, 'rc/'))";

    private const string FullGuard = "!" + RcPr;

    /// <summary>Обов'язкові перевірки <c>main</c> (branch protection), які сторож знає поіменно.</summary>
    private static readonly string[] Required =
    [
        "build", "test", "honesty-guard", "server", "client", "a11y (dark)", "a11y (light)", "pr-source",
    ];

    /// <summary>Обов'язкове ім'я → єдине завдання, що має право його давати.</summary>
    private static readonly (string Name, string File, string Job)[] Producers =
    [
        ("build", "evidence.yml", "build"),
        ("test", "evidence.yml", "test"),
        ("server", "ci.yml", "server"),
        ("client", "ci.yml", "client"),
        ("a11y (dark)", "ci.yml", "a11y"),
        ("a11y (light)", "ci.yml", "a11y"),
        ("honesty-guard", "evidence.yml", "honesty-guard"),
        ("pr-source", "ci.yml", "pr-source"),
    ];

    /// <summary>Обов'язкові імена, що на PR rc/** дають справжні (не пропущені протилежною умовою) завдання.</summary>
    private static readonly string[] Exceptions = ["honesty-guard", "pr-source"];

    /// <summary>Повні завдання з іменем-виразом «rc ? інше : обов'язкове» (<c>windows</c> — не обов'язкове, але зведене).</summary>
    private static readonly (string File, string Job)[] ExpressionNamed =
    [
        ("ci.yml", "server"), ("ci.yml", "client"), ("ci.yml", "a11y"), ("ci.yml", "windows"),
        ("evidence.yml", "build"), ("evidence.yml", "test"),
    ];

    /// <summary>Повні завдання, які мусять мати протилежну умову.</summary>
    private static readonly (string File, string[] Jobs)[] FullJobs =
    [
        ("ci.yml", ["server-part", "server", "client", "a11y", "worker-windows", "msi-windows", "windows"]),
        ("evidence.yml", ["build", "test"]),
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Перелік_виробників_покриває_всі_обовязкові_імена()
    {
        Assert.Equal(
            Required.OrderBy(x => x, StringComparer.Ordinal),
            Producers.Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Subset(Required.ToHashSet(StringComparer.Ordinal), Exceptions.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожне_обовязкове_імя_дає_рівно_одне_завдання_в_усіх_workflow()
    {
        var all = AllJobs();
        Assert.Contains(all, j => j.File == "ci.yml");
        Assert.Contains(all, j => j.File == "evidence.yml");

        foreach (var (name, file, job) in Producers)
        {
            var producers = all.Where(j => CanProduce(j, name)).ToList();

            Assert.True(
                producers.Count > 0,
                $"Обов'язкову перевірку «{name}» не дає жодне завдання — вона ніколи не з'явиться на SHA, і кожен PR у main стоятиме заблокованим.");

            var extra = producers.Where(j => j.File != file || j.Id != job).ToList();
            Assert.True(
                extra.Count == 0,
                $"Обов'язкове ім'я «{name}» може дати не лише {file}:{job}, а й {string.Join(", ", extra.Select(j => $"{j.File}:{j.Id} (name: {j.Name ?? "<немає, id>"})"))}. "
                + "Пропущене завдання з простим ім'ям GitHub зараховує як пройдене, а друге завдання з тим самим ім'ям на rc-PR підмінило б "
                + "відсутній повний прогін — PR без повного CI на SHA замержився б.");

            Assert.Single(producers);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Повне_завдання_має_імя_виразом_і_на_rc_воно_не_обовязкове()
    {
        var all = AllJobs();

        foreach (var (file, id) in ExpressionNamed)
        {
            var job = all.SingleOrDefault(j => j.File == file && j.Id == id);
            Assert.True(job is not null, $"{file}: немає повного завдання «{id}» — обов'язкова перевірка ніколи не з'явиться.");

            var name = job!.Name;
            Assert.True(name is not null, $"{file}: «{id}» без `name:` — ім'я завдання тоді дорівнює id і є навіть у пропущеного завдання, тобто пропуск = зелено.");
            var m = Regex.Match(
                name!,
                @"^\$\{\{ \(github\.event_name == 'pull_request' && startsWith\(github\.head_ref, 'rc/'\)\) && '(?<rc>[^']+)' \|\| (?<other>'[^']+'|format\('a11y \(\{0\}\)', matrix\.theme\)) \}\}$");
            Assert.True(
                m.Success,
                $"{file}: «{id}» має ім'я не у формі виразу «rc ? '<x> (повний, пропущено на rc)' : '<ім'я>'». Простий рядок у пропущеного завдання "
                + $"зараховується як пройдена обов'язкова перевірка; сирий текст виразу — ні. Фактично: {name}");

            var rc = m.Groups["rc"].Value;
            Assert.Equal($"{id} (повний, пропущено на rc)", rc);
            Assert.DoesNotContain(rc, Required);

            if (id == "a11y")
            {
                Assert.Equal("format('a11y ({0})', matrix.theme)", m.Groups["other"].Value);
                Assert.True(job.Matrix.TryGetValue("theme", out var themes), "ci.yml: a11y без явної матриці theme — теми не виводяться з тексту.");
                Assert.Equal("dark,light", string.Join(',', themes!.OrderBy(x => x, StringComparer.Ordinal)));
            }
            else
            {
                Assert.Equal($"'{id}'", m.Groups["other"].Value);
            }
        }
    }

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("evidence.yml")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Повні_завдання_мають_протилежну_умову(string file)
    {
        var jobs = AllJobs().Where(j => j.File == file).ToList();

        foreach (var id in FullJobs.Single(f => f.File == file).Jobs)
        {
            var job = jobs.SingleOrDefault(j => j.Id == id);
            Assert.True(job is not null, $"{file}: немає повного завдання «{id}».");
            AssertFullGuard(job!);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Атестацій_немає_і_на_rc_PR_обовязкові_імена_дають_лише_винятки()
    {
        foreach (var job in AllJobs())
        {
            Assert.False(
                job.Id.Contains("attest", StringComparison.OrdinalIgnoreCase),
                $"{job.File}: завдання «{job.Id}» — атестацію прибрано (B159); вердикт PR rc/** дають check-run push-прогону того ж SHA.");
            Assert.False(
                job.Name is not null && Regex.IsMatch(job.Name, @"^\$\{\{\s*matrix\.[\w-]+\s*\}\}$"),
                $"{job.File}: «{job.Id}» має ім'я лише з матриці ({job.Name}) — так виглядала атестація: завдання з обов'язковим ім'ям поруч із повним підміняє на rc-PR відсутній повний прогін (а пропущене з простим ім'ям зараховується як пройдене).");

            var produced = Required.Where(n => CanProduce(job, n)).ToList();
            if (produced.Count == 0 || produced.All(n => Exceptions.Contains(n)))
            {
                continue;
            }

            // будь-яке завдання з обов'язковим ім'ям, крім винятків, на PR rc/** мусить бути пропущене
            // (і тоді воно без обов'язкового імені), а не виконане/пропущене з простим іменем
            AssertFullGuard(job);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Honesty_guard_і_pr_source_лишаються_справжніми()
    {
        // honesty-guard: на PR перевіряє diff усього тижня проти main, чого push-прогін не робить;
        // pr-source: власна умова dev/integration. Обидва мають просте ім'я, тож не повинні ховатись за rc/.
        var all = AllJobs();
        var evidence = all.Single(j => j.File == "evidence.yml" && j.Id == "honesty-guard");
        var ci = all.Single(j => j.File == "ci.yml" && j.Id == "pr-source");

        Assert.True(
            SingleLine(evidence.Body, "if") is null,
            "evidence.yml: у honesty-guard з'явився `if` — пропущене завдання з простим ім'ям «honesty-guard» GitHub зараховує як пройдене, тобто на PR сторож чесності не працював би.");
        Assert.True(
            SingleLine(evidence.Body, "needs") is null,
            "evidence.yml: honesty-guard отримав `needs` — при падінні залежності він був би пропущений, а пропущене завдання з простим ім'ям зараховується як пройдене.");
        Assert.Equal("honesty-guard", evidence.Name);
        Assert.DoesNotContain("continue-on-error", evidence.Body, StringComparison.Ordinal);

        Assert.DoesNotContain("head_ref, 'rc/'", ci.Body, StringComparison.Ordinal);
        Assert.Equal(
            "github.event_name == 'pull_request' && github.head_ref == 'dev/integration'",
            SingleLine(ci.Body, "if"));
        Assert.DoesNotContain("continue-on-error", ci.Body, StringComparison.Ordinal);
    }

    private static void AssertFullGuard(Job job)
    {
        var condition = SingleLine(job.Body, "if");
        Assert.True(
            condition is not null && condition.Contains(FullGuard, StringComparison.Ordinal),
            $"{job.File}: у «{job.Id}» умова `if` не містить протилежної `{FullGuard}` — на PR rc/** завдання виконалось би або було б пропущене з простим ім'ям, і обов'язкова перевірка з'явилась би без повного прогону на SHA.");
        Assert.True(
            !condition!.Contains("|| true", StringComparison.Ordinal),
            $"{job.File}: у «{job.Id}» `|| true` в умові — протилежна умова перестає діяти.");
        Assert.True(
            !job.Body.Contains("continue-on-error", StringComparison.Ordinal),
            $"{job.File}: у «{job.Id}» `continue-on-error` — червоне повне завдання дало б зелену обов'язкову перевірку.");

        // ⛔ протилежна умова з'єднана ЛИШЕ через `&&`: `||` зробив би її «або», і повне
        // завдання на PR rc/** запускалось би з обов'язковим ім'ям замість push-прогону того ж SHA
        Assert.True(
            !condition.Contains("||", StringComparison.Ordinal),
            $"{job.File}: у «{job.Id}» умова має `||` — протилежна умова стає «або» і на PR rc/** більше не гарантує пропуску. Фактично: {condition}");
        var at = condition.IndexOf(FullGuard, StringComparison.Ordinal);
        var before = condition[..at].TrimEnd();
        var after = condition[(at + FullGuard.Length)..].TrimStart();
        Assert.True(
            before.EndsWith("&&", StringComparison.Ordinal) || before.EndsWith("${{", StringComparison.Ordinal),
            $"{job.File}: у «{job.Id}» перед протилежною умовою має стояти `&&` або початок виразу.");
        Assert.True(
            after.StartsWith("&&", StringComparison.Ordinal) || after.StartsWith("}}", StringComparison.Ordinal),
            $"{job.File}: у «{job.Id}» після протилежної умови має стояти `&&` або кінець виразу.");
    }

    // ---------- що може дати завдання як ім'я check-run ----------

    /// <summary>Чи МОЖЕ завдання дати check-run з ім'ям <paramref name="name"/> (консервативно).</summary>
    private static bool CanProduce(Job job, string name) => NamePattern(job).IsMatch(name);

    private static Regex NamePattern(Job job)
    {
        var raw = job.Name;
        string template;
        if (raw is null)
        {
            template = job.Id;
        }
        else if (raw.StartsWith('>') || raw.StartsWith('|') || raw.Length == 0)
        {
            return new Regex("^.*$", RegexOptions.Singleline); // блоковий скаляр — не виводимо, отже будь-що
        }
        else
        {
            template = raw;
        }

        var pattern = new System.Text.StringBuilder("^");
        var pos = 0;
        foreach (Match seg in Regex.Matches(template, @"\$\{\{(?<e>.*?)\}\}"))
        {
            pattern.Append(Regex.Escape(template[pos..seg.Index]));
            var alternatives = Alternatives(seg.Groups["e"].Value.Trim(), job.Matrix);
            pattern.Append("(?:").Append(string.Join('|', alternatives)).Append(')');
            pos = seg.Index + seg.Length;
        }

        pattern.Append(Regex.Escape(template[pos..]));

        // матричне завдання, чиє ім'я не бере `matrix.*`, GitHub називає «ім'я (значення, …)»
        if (job.HasMatrix && !template.Contains("matrix.", StringComparison.Ordinal))
        {
            pattern.Append(@"(?: \(.*\))?");
        }

        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.Singleline);
    }

    /// <summary>Можливі значення виразу як фрагменти регулярного виразу; невідоме — <c>.*</c>.</summary>
    private static List<string> Alternatives(string expr, IReadOnlyDictionary<string, string[]> matrix)
    {
        var rc = Regex.Match(
            expr,
            @"^\(github\.event_name == 'pull_request' && startsWith\(github\.head_ref, 'rc/'\)\) && '(?<rc>[^']*)' \|\| (?<other>.+)$");
        if (rc.Success)
        {
            return [Regex.Escape(rc.Groups["rc"].Value), .. Alternatives(rc.Groups["other"].Value.Trim(), matrix)];
        }

        var literal = Regex.Match(expr, @"^'(?<v>[^']*)'$");
        if (literal.Success)
        {
            return [Regex.Escape(literal.Groups["v"].Value)];
        }

        var key = Regex.Match(expr, @"^matrix\.(?<k>[\w-]+)$");
        if (key.Success)
        {
            return matrix.TryGetValue(key.Groups["k"].Value, out var values)
                ? values.Select(Regex.Escape).ToList()
                : [".*"];
        }

        var format = Regex.Match(expr, @"^format\('(?<t>[^']*)', matrix\.(?<k>[\w-]+)\)$");
        if (format.Success)
        {
            var t = format.Groups["t"].Value;
            return matrix.TryGetValue(format.Groups["k"].Value, out var values)
                ? values.Select(v => Regex.Escape(t.Replace("{0}", v, StringComparison.Ordinal))).ToList()
                : [string.Join(".*", t.Split("{0}").Select(Regex.Escape))];
        }

        return [".*"]; // будь-який інший вираз може дати будь-яке ім'я
    }

    // ---------- розбір тексту workflow ----------

    private sealed record Job(string File, string Id, string Body, string? Name, bool HasMatrix, IReadOnlyDictionary<string, string[]> Matrix);

    private static List<Job> AllJobs()
    {
        var dir = Path.Combine(Root(), ".github", "workflows");
        var files = Directory.GetFiles(dir, "*.yml").Concat(Directory.GetFiles(dir, "*.yaml")).OrderBy(f => f, StringComparer.Ordinal);
        return files.SelectMany(f => Jobs(Path.GetFileName(f), File.ReadAllText(f))).ToList();
    }

    private static List<Job> Jobs(string file, string raw)
    {
        var lines = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Array.IndexOf(lines, "jobs:");
        Assert.True(start >= 0, $"{file}: немає секції jobs:.");

        var blocks = new List<(string Id, List<string> Body)>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#'))
            {
                break; // наступний ключ верхнього рівня
            }

            var header = Regex.Match(line, @"^  (?<id>[A-Za-z_][A-Za-z0-9_-]*):\s*(#.*)?$");
            if (header.Success)
            {
                blocks.Add((header.Groups["id"].Value, []));
                continue;
            }

            if (blocks.Count > 0 && !line.TrimStart().StartsWith('#'))
            {
                blocks[^1].Body.Add(line);
            }
        }

        return blocks.Select(b =>
        {
            var body = string.Join('\n', b.Body);
            var name = SingleLine(body, "name");
            if (name is not null && !name.StartsWith("${{", StringComparison.Ordinal))
            {
                name = Regex.Replace(name, @"\s+#.*$", string.Empty);
                if (name.Length >= 2 && (name[0] == '\'' || name[0] == '"') && name[^1] == name[0])
                {
                    name = name[1..^1];
                }
            }

            var (hasMatrix, matrix) = Matrix(b.Body);
            return new Job(file, b.Id, body, name, hasMatrix, matrix);
        }).ToList();
    }

    /// <summary>Ключі матриці зі списком у рядку; при <c>include</c> або матриці-виразі значення невідомі.</summary>
    private static (bool HasMatrix, IReadOnlyDictionary<string, string[]> Matrix) Matrix(List<string> body)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var s = body.FindIndex(l => Regex.IsMatch(l, @"^    strategy:\s*$"));
        if (s < 0)
        {
            return (false, result);
        }

        var block = new List<string>();
        for (var i = s + 1; i < body.Count; i++)
        {
            var l = body[i];
            if (l.Trim().Length > 0 && l.Length - l.TrimStart().Length <= 4)
            {
                break;
            }

            block.Add(l);
        }

        var text = string.Join('\n', block);
        if (!Regex.IsMatch(text, @"(?m)^      matrix:"))
        {
            return (false, result);
        }

        if (Regex.IsMatch(text, @"(?m)^      matrix:[ \t]*[^\s#]") || Regex.IsMatch(text, @"(?m)^        include:"))
        {
            return (true, result); // значення невідомі — кожне `matrix.*` дасть «будь-що»
        }

        foreach (Match m in Regex.Matches(text, @"(?m)^        (?<k>[A-Za-z_][\w-]*):[ \t]*\[(?<l>[^\]\n]*)\][ \t]*$"))
        {
            result[m.Groups["k"].Value] = m.Groups["l"].Value
                .Split(',')
                .Select(x => x.Trim().Trim('\'', '"'))
                .Where(x => x.Length > 0)
                .ToArray();
        }

        return (true, result);
    }

    /// <summary>Однорядкове значення ключа рівня завдання (4 пробіли відступу), або null.</summary>
    private static string? SingleLine(string body, string key)
    {
        var m = Regex.Match(body, @"(?m)^    " + Regex.Escape(key) + @":[ \t]*(?<v>.*?)\s*$");
        return m.Success ? m.Groups["v"].Value : null;
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
