using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// B101 (ретро 382, інцидент 13.11): тест, що залежить від СТАНУ періоду, на справжньому годиннику не бере
/// періоду поряд із сьогоднішньою датою — лише 2099xx або період від годинника (<c>siteToday</c>).
/// </summary>
/// <remarks>
/// ⛔ Інцидент: <c>DocumentVersionMigrationTests.FromMonth</c> брав 202609 на справжньому годиннику; 13.11 о 19:00Z
/// пораховане закриття вересня настало, і api-1 почервонів у КОЖНІЙ lane без жодної зміни коду (виправлено
/// f7c851bdb: 209909/209910).
/// <para>
/// Евристика по тексту (Roslyn у цьому проєкті немає): коментарі вирізано
/// (<see cref="MessageKeyRatchetTests.MaskComments"/>), partial-класи зведено за проєктом і іменем, клас поділено на
/// методи, а виклики методів того самого класу розгорнуто в замикання. Тест (<c>[Fact]</c>/<c>[Theory]</c>)
/// червоний, коли в замиканні є ВСЕ: межі періоду пораховано (<c>RecomputeBoundaries</c>,
/// <c>POST projects/…/activate</c>, <c>new ActivateProjectHandler</c>, <c>period-policies/</c>), проєкт активовано
/// (<c>.Activate(</c>, той самий POST чи обробник), годинник справжній (<c>DateTime.UtcNow</c>,
/// <c>DateTimeOffset.UtcNow</c>, <c>TimeProvider.System</c>, <c>new EcrApiFactory(</c>), — і у ВЛАСНОМУ тексті тесту
/// (атрибути й тіло, без списку параметрів) є близький ключ: літерал 2024xx–2029xx, константа класу з таким
/// значенням без <c>.</c> попереду або <c>.BuildAsync(…)</c> без ключа (типово 202601).
/// </para>
/// <para>
/// ⚠ Свідомі межі: типові значення параметрів допоміжних методів (<c>int periodKey = 202601</c>) не рахуються —
/// лише явний ключ у тесті. <c>YearGraceOffsetDays … = 0</c> у замиканні знешкоджує тест: для періоду, який уже
/// пораховано <c>Closed</c>, назад у <c>Grace</c> його могло повернути лише річне вікно D-204
/// (<c>ReopenedPeriodExpiryTests</c>). Сторож годинника не читає — сам із часом не червоніє.
/// </para>
/// Доказ червоного: у FromMonth повернути 209909/209910 → 202609/202610 — червоні обидва тести з 2099; прибрати
/// <c>YearGraceOffsetDays = 0</c> з <c>ReopenedPeriodExpiryTests</c> — червоний він. Без правки дерева —
/// <see cref="Сторож_бачить_бомбу_і_не_чіпає_безпечні_зразки"/>.
/// </remarks>
public sealed partial class PeriodClockBombGuardTests
{
    /// <summary>Свідомі винятки: (файл, метод, причина). Кожен — з письмовою причиною; зайвий запис червонить.</summary>
    private static readonly (string File, string Method, string Reason)[] Allowed =
    [
        (
            "tests/Ecr.Api.Tests/Security/ProjectExistenceOracleApiTests.cs",
            "S17_невидимий_проєкт_і_період_відповідають_як_неіснуючі",
            "POST projects/…/activate тут ВІДМОВЛЕНО (404 чужому, 403 читачу) — проєкт лишається Draft, і стану "
            + "періоду тест не читає: лише форму відмови й 200 на перелік періодів."),
    ];

    private const string SelfProject = "tests/Ecr.Architecture.Tests/";

    private const string Hint =
        "Бери 2099xx або період від годинника (siteToday); для періоду, що вже закритий, вимкни річне вікно на СВОЄМУ "
        + "проєкті (YearGraceOffsetDays = 0). Див. B101.";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Тест_зі_станом_періоду_на_справжньому_годиннику_не_бере_близького_періоду()
    {
        var files = SourceTree.Walk(Path.Combine(SourceTree.Root, "tests"))
            .Where(p => p.EndsWith(".cs", StringComparison.Ordinal))
            .Select(p => new SourceFile(Path.GetRelativePath(SourceTree.Root, p).Replace('\\', '/'), File.ReadAllText(p)))
            .Where(f => !f.Path.StartsWith(SelfProject, StringComparison.Ordinal))
            .ToList();

        var scan = Scan(files);

        // ⛔ Самоперевірка: порожній обхід чи розбір, що не бачить жодного «озброєного» тесту, дав би зелене саме тоді,
        // коли сторож осліп (сьогодні таких тестів десятки — усі на періоді від годинника).
        Assert.True(files.Count > 100, $"знайдено лише {files.Count} файлів тестів — сторож дивиться не туди");
        Assert.True(scan.ArmedTests >= 10, $"лише {scan.ArmedTests} тестів із порахованими межами на справжньому годиннику — розбір зламано");

        var offenders = scan.Findings
            .Where(f => !Array.Exists(Allowed, a => a.File == f.File && a.Method == f.Method))
            .Select(f => $"{f.File}:{f.Method} — близький ключ {f.Key}")
            .ToList();
        var stale = Allowed
            .Where(a => !scan.Findings.Any(f => f.File == a.File && f.Method == a.Method))
            .Select(a => $"{a.File}:{a.Method}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Тест читає стан періоду поряд із сьогоднішнім на справжньому годиннику — бомба за календарем:\n"
            + string.Join("\n", offenders) + "\n" + Hint);
        Assert.True(stale.Count == 0, "Дозвіл більше не потрібен — прибери з Allowed: " + string.Join("; ", stale));
    }

    /// <remarks>
    /// Доказ самого сторожа на синтетичному коді. Кожен зразок «озброєний» (межі + активація + справжній годинник у
    /// замиканні), крім зразка з фіксованим годинником, тож нуль знахідок — не від того, що сторож нічого не бачить.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData(NearLiteralSample, 1, "202609")]
    [InlineData(CommentedSample, 1, null)]
    [InlineData(FarFutureSample, 1, null)]
    [InlineData(DefaultKeySample, 1, "BuildAsync")]
    [InlineData(ClassConstSample, 1, "Key = 202610")]
    [InlineData(PropertySample, 1, null)]
    [InlineData(HelperDefaultSample, 1, null)]
    [InlineData(HttpActivationSample, 1, "BuildAsync")]
    [InlineData(YearWindowOffSample, 1, null)]
    [InlineData(FixedClockSample, 0, null)]
    public void Сторож_бачить_бомбу_і_не_чіпає_безпечні_зразки(string code, int armed, string? expectedKey)
    {
        var scan = Scan([new SourceFile("tests/Sample.Tests/Sample.cs", code)]);

        Assert.Equal(armed, scan.ArmedTests);
        if (expectedKey is null)
        {
            Assert.Empty(scan.Findings);
        }
        else
        {
            var finding = Assert.Single(scan.Findings);
            Assert.Equal(("tests/Sample.Tests/Sample.cs", "T"), (finding.File, finding.Method));
            Assert.Contains(expectedKey, finding.Key, StringComparison.Ordinal);
        }
    }

    /// <remarks>
    /// Активація в іншому файлі того самого partial-класу доходить до тесту; однойменний клас ІНШОГО проєкту — ні.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Сторож_зводить_partial_класи_лише_в_межах_проєкту()
    {
        const string Test = "public sealed partial class S\n{\n    [Fact]\n    public async Task T()\n    {\n"
            + "        var d = await b.BuildAsync(202609);\n        await OpenAsync(d);\n    }\n}\n";
        const string Opener = "public sealed partial class S\n{\n    private async Task OpenAsync(object d)\n    {\n"
            + "        p.Activate(DateTime.UtcNow);\n        q.RecomputeBoundaries(policy, zone);\n    }\n}\n";

        var scan = Scan(
        [
            new SourceFile("tests/Sample.Tests/S.Test.cs", Test),
            new SourceFile("tests/Sample.Tests/S.Open.cs", Opener),
            new SourceFile("tests/Other.Tests/S.cs", Test),
        ]);

        var finding = Assert.Single(scan.Findings);
        Assert.Equal(("tests/Sample.Tests/S.Test.cs", "T"), (finding.File, finding.Method));
    }

    private const string Open = "public sealed class S\n{\n";
    private const string TestHead = "    [Fact]\n    public async Task T()\n    {\n        ";
    private const string ArmTail =
        "\n        var (x, y) = await ArmAsync(d);\n    }\n\n"
        + "    private static async Task<(string A, int B)> ArmAsync(object d)\n    {\n"
        + "        p.Activate(DateTime.UtcNow);\n        q.RecomputeBoundaries(policy, zone);\n        return (\"a\", 1);\n    }\n}\n";
    private const string Const = "    private const int Key = 202610;\n\n";

    private const string NearLiteralSample = Open + TestHead + "var d = await b.BuildAsync(202609);" + ArmTail;
    private const string CommentedSample =
        Open + TestHead + "var d = await b.BuildAsync(209909); // було 202609\n        /* і 202610 */" + ArmTail;
    private const string FarFutureSample = Open + TestHead + "var d = await b.BuildAsync(209909, columnCount: 2);" + ArmTail;
    private const string DefaultKeySample = Open + TestHead + "var d = await b.BuildAsync(ct: CancellationToken.None);" + ArmTail;
    private const string ClassConstSample = Open + Const + TestHead + "var d = await b.BuildAsync(Key);" + ArmTail;
    private const string PropertySample = Open + Const + TestHead + "var d = await b.BuildAsync(c.Key);" + ArmTail;
    private const string HelperDefaultSample =
        Open + TestHead + "await ArrangeAsync();\n    }\n\n    private async Task ArrangeAsync(int periodKey = 202601)\n    {\n"
        + "        var d = await b.BuildAsync(periodKey);\n        p.Activate(DateTime.UtcNow);\n"
        + "        q.RecomputeBoundaries(policy, zone);\n    }\n}\n";
    private const string HttpActivationSample =
        Open + TestHead + "var d = await b.BuildAsync();\n        using var app = new EcrApiFactory(sql);\n"
        + "        await c.PostAsync(new Uri($\"/api/v1/projects/{d.ProjectId}/activate\", UriKind.Relative), null);\n    }\n}\n";
    private const string YearWindowOffSample =
        Open + TestHead + "var d = await b.BuildAsync();\n"
        + "        db.Entry(p).Property(x => x.YearGraceOffsetDays).CurrentValue = 0;" + ArmTail;
    private const string FixedClockSample =
        Open + TestHead + "var d = await b.BuildAsync(202601);\n        await ArmAsync(d);\n    }\n\n"
        + "    private static async Task ArmAsync(object d)\n    {\n"
        + "        p.Activate(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));\n"
        + "        q.RecomputeBoundaries(policy, zone);\n    }\n}\n";

    /// <summary>Знахідка: тест і близький ключ, який він бере.</summary>
    internal sealed record Finding(string File, string Method, string Key);

    /// <summary>Результат обходу: знахідки і скільки тестів «озброєні» (межі + активація + справжній годинник).</summary>
    internal sealed record ScanResult(IReadOnlyList<Finding> Findings, int ArmedTests);

    [Flags]
    private enum Marks
    {
        None = 0,
        Boundaries = 1,
        Activation = 2,
        RealClock = 4,
        YearWindowOff = 8,
    }

    /// <summary>Метод класу: власний текст тесту (атрибути + тіло), виклики і позначки замикання.</summary>
    private sealed record MethodBlock(
        string File, string Name, bool IsTest, string Own, string OwnCode, IReadOnlySet<string> Calls, Marks Marks);

    /// <summary>Усі частини partial-класу одного проєкту.</summary>
    private sealed class TypeGroup
    {
        public List<MethodBlock> Methods { get; } = [];

        public Dictionary<string, string> Consts { get; } = new(StringComparer.Ordinal);
    }

    internal static ScanResult Scan(IEnumerable<SourceFile> files)
    {
        var groups = new Dictionary<string, TypeGroup>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            Parse(file, groups);
        }

        var findings = new List<Finding>();
        var armed = 0;

        foreach (var group in groups.Values)
        {
            var byName = group.Methods.ToLookup(m => m.Name, StringComparer.Ordinal);

            foreach (var test in group.Methods.Where(m => m.IsTest))
            {
                var marks = Closure(test, byName).Aggregate(Marks.None, (all, m) => all | m.Marks);
                const Marks Armed = Marks.Boundaries | Marks.Activation | Marks.RealClock;
                if ((marks & Armed) != Armed)
                {
                    continue;
                }

                armed++;
                if (marks.HasFlag(Marks.YearWindowOff))
                {
                    continue;
                }

                var key = NearKey(test, group, byName);
                if (key is not null)
                {
                    findings.Add(new Finding(test.File, test.Name, key));
                }
            }
        }

        return new ScanResult(findings, armed);
    }

    private static List<MethodBlock> Closure(MethodBlock root, ILookup<string, MethodBlock> byName)
    {
        var seen = new HashSet<MethodBlock>(ReferenceEqualityComparer.Instance) { root };
        var pending = new Stack<MethodBlock>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            foreach (var name in pending.Pop().Calls)
            {
                foreach (var callee in byName[name])
                {
                    if (seen.Add(callee))
                    {
                        pending.Push(callee);
                    }
                }
            }
        }

        return [.. seen];
    }

    /// <summary>Близький ключ у власному тексті тесту або <c>null</c>.</summary>
    private static string? NearKey(MethodBlock test, TypeGroup group, ILookup<string, MethodBlock> byName)
    {
        var literal = NearLiteral().Match(test.Own);
        if (literal.Success)
        {
            return literal.Value;
        }

        foreach (var (name, value) in group.Consts)
        {
            if (ReferencesBare(test.Own, name))
            {
                return $"{name} = {value}";
            }
        }

        // Власний BuildAsync класу — помічник зі своїм типовим ключем (його параметр навмисно не рахується).
        if (!byName.Contains("BuildAsync"))
        {
            foreach (Match call in BuilderCall().Matches(test.OwnCode))
            {
                var arguments = Arguments(test.OwnCode, call.Index + call.Length - 1);
                if (arguments.Length == 0
                    || (FirstArgumentNamed().IsMatch(arguments) && !PeriodKeyArgument().IsMatch(arguments)))
                {
                    return "BuildAsync(…) без ключа періоду — типово 202601";
                }
            }
        }

        return null;
    }

    /// <summary>Ім'я як окремий ідентифікатор, перед яким немає <c>.</c> (тобто не властивість чужого об'єкта).</summary>
    private static bool ReferencesBare(string text, string name)
    {
        for (var at = text.IndexOf(name, StringComparison.Ordinal); at >= 0; at = text.IndexOf(name, at + 1, StringComparison.Ordinal))
        {
            var before = at == 0 ? ' ' : text[at - 1];
            var after = at + name.Length < text.Length ? text[at + name.Length] : ' ';
            if (!IsIdentifierChar(before) && before != '.' && !IsIdentifierChar(after))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static void Parse(SourceFile file, Dictionary<string, TypeGroup> groups)
    {
        var stripped = MessageKeyRatchetTests.MaskComments(file.Text);
        var masked = MaskLiteralsAndDirectives(stripped);
        var segments = file.Path.Split('/');
        var project = segments.Length > 2 ? $"{segments[0]}/{segments[1]}" : file.Path;

        foreach (Match type in TypeDeclaration().Matches(masked))
        {
            if (!InDeclarationContext(masked, type.Index))
            {
                continue;
            }

            var open = BodyStart(masked, type.Index + type.Length);
            if (open < 0)
            {
                continue;
            }

            var close = Matching(masked, open, '{', '}');
            var key = $"{project}/{type.Groups["name"].Value}";
            if (!groups.TryGetValue(key, out var group))
            {
                group = new TypeGroup();
                groups[key] = group;
            }

            foreach (Match constant in NearConst().Matches(stripped, open))
            {
                if (constant.Index > close)
                {
                    break;
                }

                group.Consts[constant.Groups["name"].Value] = constant.Groups["value"].Value;
            }

            ReadMembers(file.Path, stripped, masked, open, close, group);
        }
    }

    /// <summary>Члени тіла класу на його власному рівні (вкладені типи мають свій обхід).</summary>
    private static void ReadMembers(string path, string stripped, string masked, int open, int close, TypeGroup group)
    {
        var i = open + 1;
        while (i < close)
        {
            while (i < close && char.IsWhiteSpace(masked[i]))
            {
                i++;
            }

            if (i >= close)
            {
                break;
            }

            var start = i;
            var stop = HeaderEnd(masked, start, close);
            if (stop < 0)
            {
                break;
            }

            var terminator = masked[stop];
            if (terminator == ';')
            {
                i = stop + 1;
                continue;
            }

            if (terminator == '=' && (stop + 1 >= close || masked[stop + 1] != '>'))
            {
                i = StatementEnd(masked, stop, close) + 1;   // ініціалізатор поля чи властивості
                continue;
            }

            var bodyStart = stop;
            var bodyEnd = terminator == '{' ? Matching(masked, stop, '{', '}') : StatementEnd(masked, stop, close);
            i = bodyEnd + 1;

            var header = masked[start..stop];
            var attributesEnd = start + AttributesLength(header);
            var name = MethodName(masked[attributesEnd..stop]);
            if (name is null)
            {
                continue;
            }

            var full = stripped[start..(bodyEnd + 1)];
            var code = masked[start..(bodyEnd + 1)];
            var attributes = masked[start..attributesEnd];

            group.Methods.Add(new MethodBlock(
                path,
                name,
                TestAttribute().IsMatch(attributes),
                string.Concat(stripped.AsSpan(start, attributesEnd - start), " ", stripped.AsSpan(bodyStart, bodyEnd + 1 - bodyStart)),
                string.Concat(masked.AsSpan(start, attributesEnd - start), " ", masked.AsSpan(bodyStart, bodyEnd + 1 - bodyStart)),
                Call().Matches(code).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal),
                MarksOf(full)));
        }
    }

    private static Marks MarksOf(string text)
    {
        var marks = Marks.None;
        marks |= BoundariesMarker().IsMatch(text) ? Marks.Boundaries : Marks.None;
        marks |= ActivationMarker().IsMatch(text) ? Marks.Activation : Marks.None;
        marks |= RealClockMarker().IsMatch(text) ? Marks.RealClock : Marks.None;
        marks |= YearWindowOffMarker().IsMatch(text) ? Marks.YearWindowOff : Marks.None;
        return marks;
    }

    /// <summary>
    /// Індекс першого <c>{</c>, <c>;</c> чи <c>=</c> (зокрема <c>=&gt;</c>) поза дужками й атрибутами; <c>-1</c> — немає.
    /// </summary>
    private static int HeaderEnd(string masked, int from, int close)
    {
        var parens = 0;
        var brackets = 0;
        for (var i = from; i < close; i++)
        {
            switch (masked[i])
            {
                case '(':
                    parens++;
                    break;
                case ')':
                    parens--;
                    break;
                case '[':
                    brackets++;
                    break;
                case ']':
                    brackets--;
                    break;
                case '{' or ';' or '=' when parens == 0 && brackets == 0:
                    return i;
            }
        }

        return -1;
    }

    /// <summary>Кінець інструкції — <c>;</c> на нульовій глибині всіх дужок.</summary>
    private static int StatementEnd(string masked, int from, int close)
    {
        var depth = 0;
        for (var i = from; i < close; i++)
        {
            switch (masked[i])
            {
                case '(' or '{' or '[':
                    depth++;
                    break;
                case ')' or '}' or ']':
                    depth--;
                    break;
                case ';' when depth == 0:
                    return i;
            }
        }

        return close - 1;
    }

    /// <summary>Довжина ведучих атрибутів <c>[…]</c> у заголовку члена.</summary>
    private static int AttributesLength(string header)
    {
        var i = 0;
        while (true)
        {
            var next = i;
            while (next < header.Length && char.IsWhiteSpace(header[next]))
            {
                next++;
            }

            if (next >= header.Length || header[next] != '[')
            {
                return i;
            }

            i = Matching(header, next, '[', ']') + 1;
        }
    }

    /// <summary>
    /// Ім'я методу з заголовка без атрибутів: ідентифікатор перед першою групою <c>(…)</c> верхнього рівня, якій
    /// він передує (тож кортеж у типі результату, <c>Task&lt;(string A, int B)&gt;</c>, пропускається). Тип,
    /// властивість, подія — <c>null</c>.
    /// </summary>
    private static string? MethodName(string header)
    {
        // Ключове слово типу — лише ДО першої дужки: параметр на ім'я `record` чи `@event` методом бути не заважає.
        var firstParen = header.IndexOf('(', StringComparison.Ordinal);
        if (NotAMethod().IsMatch(firstParen < 0 ? header : header[..firstParen]))
        {
            return null;
        }

        var depth = 0;
        for (var i = 0; i < header.Length; i++)
        {
            if (header[i] == ')')
            {
                depth--;
                continue;
            }

            if (header[i] != '(')
            {
                continue;
            }

            if (depth++ != 0)
            {
                continue;
            }

            var end = i - 1;
            while (end >= 0 && char.IsWhiteSpace(header[end]))
            {
                end--;
            }

            if (end >= 0 && header[end] == '>')
            {
                var angles = 0;
                for (; end >= 0; end--)
                {
                    angles += header[end] switch { '>' => 1, '<' => -1, _ => 0 };
                    if (angles == 0)
                    {
                        break;
                    }
                }

                end--;
                while (end >= 0 && char.IsWhiteSpace(header[end]))
                {
                    end--;
                }
            }

            var begin = end;
            while (begin >= 0 && IsIdentifierChar(header[begin]))
            {
                begin--;
            }

            var name = header[(begin + 1)..(end + 1)];
            if (name.Length > 0 && !char.IsDigit(name[0]) && !Keywords.Contains(name))
            {
                return name;
            }
        }

        return null;
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "switch", "using", "lock", "catch", "return", "new", "nameof", "typeof",
        "sizeof", "default", "base", "this", "await", "when", "fixed", "checked", "unchecked",
    };

    /// <summary>Вміст списку аргументів від <c>(</c> на <paramref name="open"/> до парної <c>)</c>, обрізаний.</summary>
    private static string Arguments(string code, int open) => code[(open + 1)..Matching(code, open, '(', ')')].Trim();

    /// <summary>Індекс парної дужки (або останній символ, якщо її немає).</summary>
    private static int Matching(string text, int open, char opening, char closing)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == opening)
            {
                depth++;
            }
            else if (text[i] == closing && --depth == 0)
            {
                return i;
            }
        }

        return text.Length - 1;
    }

    /// <summary>Перший <c>{</c> тіла типу після його імені; <c>;</c> раніше (запис без тіла) — <c>-1</c>.</summary>
    private static int BodyStart(string masked, int from)
    {
        var parens = 0;
        for (var i = from; i < masked.Length; i++)
        {
            switch (masked[i])
            {
                case '(':
                    parens++;
                    break;
                case ')':
                    parens--;
                    break;
                case '{' when parens == 0:
                    return i;
                case ';' when parens == 0:
                    return -1;
            }
        }

        return -1;
    }

    /// <summary>Оголошення типу стоїть там, де може початись член: на початку, після <c>{ } ; ]</c>.</summary>
    private static bool InDeclarationContext(string masked, int index)
    {
        var i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(masked[i]))
        {
            i--;
        }

        return i < 0 || masked[i] is '{' or '}' or ';' or ']';
    }

    /// <summary>
    /// Вміст рядкових і символьних літералів і директиви препроцесора — пробілами (довжина й переноси зберігаються):
    /// для дужок і викликів. Ключі й маркери шукаються в тексті з літералами — URL і SQL несуть їх саме там.
    /// </summary>
    private static string MaskLiteralsAndDirectives(string stripped)
    {
        var chars = stripped.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is '"' or '\'')
            {
                var end = MessageKeyRatchetTests.SkipLiteral(stripped, i);
                for (var k = i + 1; k < end; k++)
                {
                    if (chars[k] != '\n')
                    {
                        chars[k] = ' ';
                    }
                }

                i = end;
            }
        }

        var lineStart = true;
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '\n')
            {
                lineStart = true;
            }
            else if (lineStart && chars[i] == '#')
            {
                for (; i < chars.Length && chars[i] != '\n'; i++)
                {
                    chars[i] = ' ';
                }

                i--;
            }
            else if (!char.IsWhiteSpace(chars[i]))
            {
                lineStart = false;
            }
        }

        return new string(chars);
    }

    [GeneratedRegex(@"(?<![\w.])(?:(?:public|private|protected|internal|static|sealed|abstract|partial|file|readonly|unsafe|new|ref)\s+)*(?:class|struct|interface|record(?:\s+(?:class|struct))?)\s+(?<name>[\p{L}_]\w*)", RegexOptions.CultureInvariant)]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(@"\b(?:class|struct|interface|enum|record|delegate|event|operator)\b", RegexOptions.CultureInvariant)]
    private static partial Regex NotAMethod();

    [GeneratedRegex(@"(?<![\w.])(?:[\p{L}_]\w*\.)*\w*(?:Fact|Theory)(?:Attribute)?\s*[\](,]", RegexOptions.CultureInvariant)]
    private static partial Regex TestAttribute();

    [GeneratedRegex(@"(?:(?<![\w.])|(?<=\bthis\s*\.\s*))(?<name>[\p{L}_]\w*)\s*(?:<[^<>;{}()]*>)?\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex Call();

    [GeneratedRegex(@"\b(?:const|static\s+readonly)\s+(?:int|long)\s+(?<name>[\p{L}_]\w*)\s*=\s*(?<value>20(?:2[4-9])(?:0[1-9]|1[0-2]))\s*;", RegexOptions.CultureInvariant)]
    private static partial Regex NearConst();

    [GeneratedRegex(@"(?<![\w.])20(?:2[4-9])(?:0[1-9]|1[0-2])(?!\w)", RegexOptions.CultureInvariant)]
    private static partial Regex NearLiteral();

    [GeneratedRegex(@"\.\s*BuildAsync\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex BuilderCall();

    [GeneratedRegex(@"^[\p{L}_]\w*\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex FirstArgumentNamed();

    [GeneratedRegex(@"\bperiodKey\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex PeriodKeyArgument();

    [GeneratedRegex(@"\bRecomputeBoundaries\s*\(|projects/[^""\s]*/activate\b|\bnew\s+ActivateProjectHandler\s*\(|period-policies/", RegexOptions.CultureInvariant)]
    private static partial Regex BoundariesMarker();

    [GeneratedRegex(@"\.\s*Activate\s*\(|projects/[^""\s]*/activate\b|\bnew\s+ActivateProjectHandler\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex ActivationMarker();

    [GeneratedRegex(@"\bDateTime(?:Offset)?\s*\.\s*UtcNow\b|\bTimeProvider\s*\.\s*System\b|\bnew\s+EcrApiFactory\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex RealClockMarker();

    [GeneratedRegex(@"\bYearGraceOffsetDays\b[^;=]*=\s*0(?![\w.])", RegexOptions.CultureInvariant)]
    private static partial Regex YearWindowOffMarker();
}
