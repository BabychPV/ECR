using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// L3-01: кожен <c>SqlQuery&lt;T&gt;</c>, чий <c>T</c> несе <see cref="DateTime"/>, ставить
/// <c>DateTimeKind.Utc</c> у ТОМУ САМОМУ члені, що матеріалізує результат.
/// </summary>
/// <remarks>
/// ⛔ Предмет — джерело, бо в моделі цього немає: <c>SqlQuery</c> у ad-hoc тип оминає конвертер
/// <c>UtcDateTimeColumns</c> (його ставлять лише властивостям сутностей), тож момент приходить із
/// <c>Kind = Unspecified</c> і в JSON іде без «Z» — клієнт читає його як місцевий час. Так було з
/// <c>CoverageIsland</c> (аудит 2026-10-03), потім із <c>FreshnessRow</c>/<c>RegistryChangeRow</c> у
/// <c>MethodologyStore</c> (L3-01, аудит 2026-10-09).
///
/// ⚠ Як сторож не червоніє на законному коді:
/// <list type="bullet">
///   <item>коментарі й літерали відсікає <see cref="SourceTree.CodeLines"/> — слово <c>SqlQuery</c> у
///   поясненні нічого не означає;</item>
///   <item>перевіряється ЧЛЕН (метод з його тілом), а не весь файл: <c>SpecifyKind</c> в іншому методі
///   того самого файла не рятує запит, який його не має;</item>
///   <item><c>ValueDate</c> — календарна дата (<c>date</c>), а не момент: Kind там навмисно <c>Unspecified</c>
///   (<c>UtcDateTimeColumns.CalendarDates</c>);</item>
///   <item>виняток — лише <see cref="Allowed"/> із причиною, і запис без живого виклику теж червоний.</item>
/// </list>
///
/// Мутація: прибрати <c>DateTime.SpecifyKind</c> з <c>MethodologyStore.GetCalculationFreshnessAsync</c> —
/// перший тест називає <c>FreshnessRow</c>.
/// </remarks>
public sealed class SqlQueryDateTimeKindTests
{
    /// <summary>Календарні дати: читаються як є, без зони (дзеркало <c>UtcDateTimeColumns.CalendarDates</c>).</summary>
    private static readonly HashSet<string> CalendarDateProperties = new(StringComparer.Ordinal) { "ValueDate" };

    /// <summary>Виклики, що свідомо не ставлять <c>Kind</c> у тому ж члені: (файл, тип) → причина.</summary>
    private static readonly IReadOnlyDictionary<(string File, string Type), string> Allowed =
        new Dictionary<(string, string), string>
        {
            [("src/Ecr.Infrastructure/Persistence/StaleResultsQuery.cs", "StaleDocumentRow")] =
                "результат лишається IQueryable і компонується в підзапити; Kind ставить споживач "
                + "DocumentStore.StaleResultsBatchAsync, де значення матеріалізується",
            [("src/Ecr.Infrastructure/Persistence/DocumentVersionMigrationStore.cs", "DateTime")] =
                "SYSUTCDATETIME() лише повертається в SQL параметром того самого переносу, у відповідь не йде",
        };

    private static readonly Regex CallRegex = new(
        @"\bSqlQuery<\s*([A-Za-z_][\w.]*)(\?)?\s*>", RegexOptions.Compiled);

    private static readonly Regex KindRegex = new(
        @"SpecifyKind\s*\(|DateTimeKind\.Utc", RegexOptions.Compiled);

    private static readonly Regex DateTimePropertyRegex = new(
        @"\bDateTime\??\s+(\w+)", RegexOptions.Compiled);

    private static readonly Regex ControlHeaderRegex = new(
        @"^(?:if|else|for|foreach|while|using|lock|switch|try|catch|finally|do|checked|unchecked|fixed|new|return)\b",
        RegexOptions.Compiled);

    private static readonly Regex TypeHeaderRegex = new(
        @"\b(?:class|record|struct|interface|enum|namespace)\b", RegexOptions.Compiled);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Directive", "L3-01")]
    public void SqlQuery_з_DateTime_ставить_Kind_Utc_у_тому_самому_члені()
    {
        var files = SourceTree.Production();

        var result = Scan(files);

        Assert.True(result.Sites >= 20, $"знайдено лише {result.Sites} викликів SqlQuery — розбір зламався?");
        Assert.True(result.WithDateTime >= 5, $"викликів із DateTime лише {result.WithDateTime} — розбір типів зламався?");

        Assert.True(
            result.Offenders.Count == 0,
            "SqlQuery<T> з DateTime без DateTime.SpecifyKind(…, DateTimeKind.Utc) у тому самому члені — момент піде в JSON "
            + "без «Z» (L3-01):" + Environment.NewLine
            + string.Join(Environment.NewLine, result.Offenders.Order(StringComparer.Ordinal)));

        var dead = Allowed.Keys.Where(k => !result.UsedAllowed.Contains(k)).Select(k => $"{k.File} → {k.Type}").ToList();
        Assert.True(
            dead.Count == 0,
            "Запис винятку без живого виклику — приберіть його:" + Environment.NewLine + string.Join(Environment.NewLine, dead));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Directive", "L3-01")]
    public void Сторож_ловить_запит_без_SpecifyKind_і_не_чіпає_законні_форми()
    {
        const string Bad = """
            namespace X;

            public sealed class Store(Db db)
            {
                public async Task<Row> ReadAsync(CancellationToken ct)
                {
                    var rows = await db.Database
                        .SqlQuery<Row>($"SELECT 1 AS Id, SYSUTCDATETIME() AS At")
                        .ToListAsync(ct);
                    return rows[0];
                }

                public Row Other(Row r) => r with { At = DateTime.SpecifyKind(r.At, DateTimeKind.Utc) };

                public sealed record Row(int Id, DateTime At);
            }
            """;

        var bad = Scan([new SourceFile("src/X/Store.cs", Bad)]);
        Assert.Equal(1, bad.WithDateTime);
        Assert.Single(bad.Offenders);

        // SpecifyKind в ІНШОМУ методі файла запит не рятує.
        Assert.Contains("Row", bad.Offenders[0], StringComparison.Ordinal);

        const string Good = """
            namespace X;

            public sealed class Store(Db db)
            {
                public async Task<Row> ReadAsync(CancellationToken ct)
                {
                    // SqlQuery<Row> у коментарі нічого не означає.
                    var rows = await db.Database
                        .SqlQuery<Row>($"SELECT 1 AS Id, SYSUTCDATETIME() AS At")
                        .ToListAsync(ct);
                    return rows[0] with { At = DateTime.SpecifyKind(rows[0].At, DateTimeKind.Utc) };
                }

                public async Task<int> CountAsync(CancellationToken ct)
                    => await db.Database.SqlQuery<int>($"SELECT 1").SingleAsync(ct);

                public async Task<Cell> CellAsync(CancellationToken ct)
                    => await db.Database.SqlQuery<Cell>($"SELECT CAST(NULL AS date) AS ValueDate").SingleAsync(ct);

                public sealed record Row(int Id, DateTime At);

                public sealed record Cell(DateTime? ValueDate);
            }
            """;

        var good = Scan([new SourceFile("src/X/Store.cs", Good)]);
        Assert.Equal(1, good.WithDateTime);
        Assert.Empty(good.Offenders);

        // Вираз-тіло: Kind немає в самому виразі — червоний.
        const string Expression = """
            namespace X;

            public sealed class Store(Db db)
            {
                public async Task<DateTime> NowAsync(CancellationToken ct)
                    => await db.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME()").SingleAsync(ct);

                public DateTime Other() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);
            }
            """;

        Assert.Single(Scan([new SourceFile("src/X/Store.cs", Expression)]).Offenders);
    }

    private sealed record ScanResult(
        int Sites, int WithDateTime, List<string> Offenders, HashSet<(string File, string Type)> UsedAllowed);

    private static ScanResult Scan(IReadOnlyList<SourceFile> files)
    {
        var offenders = new List<string>();
        var used = new HashSet<(string, string)>();
        var sites = 0;
        var withDateTime = 0;

        var codeByFile = files.ToDictionary(f => f.Path, f => f.CodeLines().ToList(), StringComparer.Ordinal);

        foreach (var file in files)
        {
            var code = codeByFile[file.Path];

            for (var index = 0; index < code.Count; index++)
            {
                foreach (Match call in CallRegex.Matches(code[index].Text))
                {
                    sites++;

                    var type = call.Groups[1].Value;
                    var dateTimes = type == "DateTime"
                        ? new List<string> { "(значення)" }
                        : DateTimeProperties(type, file, files, codeByFile);

                    if (dateTimes.Count == 0)
                    {
                        continue;
                    }

                    withDateTime++;

                    if (Allowed.ContainsKey((file.Path, type)))
                    {
                        used.Add((file.Path, type));
                        continue;
                    }

                    var (start, end) = Scope(code, index);
                    var hasKind = Enumerable.Range(start, end - start + 1).Any(i => KindRegex.IsMatch(code[i].Text));
                    if (!hasKind)
                    {
                        offenders.Add($"{file.Path}:{code[index].Line} SqlQuery<{type}> ({string.Join(", ", dateTimes)})");
                    }
                }
            }
        }

        return new ScanResult(sites, withDateTime, offenders, used);
    }

    /// <summary>Імена <see cref="DateTime"/>-властивостей типу рядка, крім календарних дат; порожньо — типу немає або моментів у ньому немає.</summary>
    private static List<string> DateTimeProperties(
        string typeName, SourceFile local, IReadOnlyList<SourceFile> all,
        Dictionary<string, List<(int Line, string Text)>> codeByFile)
    {
        var simple = typeName[(typeName.LastIndexOf('.') + 1)..];
        var declaration = new Regex(@"\b(?:record|class|struct)\s+(?:struct\s+|class\s+)?" + Regex.Escape(simple) + @"\b");

        // Спершу файл виклику: типи рядків (`Row`) в різних файлах — різні типи з однією назвою.
        foreach (var file in new[] { local }.Concat(all.Where(f => f.Path != local.Path)))
        {
            var code = codeByFile[file.Path];
            var at = code.FindIndex(c => declaration.IsMatch(c.Text));
            if (at < 0)
            {
                continue;
            }

            return [.. DateTimePropertyRegex
                .Matches(DeclarationText(code, at))
                .Select(m => m.Groups[1].Value)
                .Where(name => !CalendarDateProperties.Contains(name))
                .Distinct(StringComparer.Ordinal)];
        }

        return [];
    }

    /// <summary>Текст оголошення: від рядка типу до <c>;</c> після параметрів або до кінця блоку.</summary>
    private static string DeclarationText(List<(int Line, string Text)> code, int at)
    {
        var text = new System.Text.StringBuilder();
        int paren = 0, brace = 0;
        var opened = false;

        for (var i = at; i < code.Count; i++)
        {
            var line = code[i].Text;
            text.AppendLine(line);

            foreach (var ch in line)
            {
                switch (ch)
                {
                    case '(': paren++; break;
                    case ')': paren--; break;
                    case '{': brace++; opened = true; break;
                    case '}': brace--; break;
                    default: break;
                }
            }

            if (paren <= 0 && ((opened && brace <= 0) || (!opened && line.EndsWith(';'))))
            {
                break;
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Межі (рядки <c>code</c>) члена, що містить виклик: метод разом з тілом. Для виразу-тіла (охопний блок —
    /// тип) — оператор від попереднього <c>;</c>/<c>{</c>/<c>}</c> до власного <c>;</c>.
    /// </summary>
    private static (int Start, int End) Scope(List<(int Line, string Text)> code, int index)
    {
        var depth = 0;

        for (var i = index; i >= 0; i--)
        {
            var text = code[i].Text;

            for (var c = text.Length - 1; c >= 0; c--)
            {
                if (text[c] == '}')
                {
                    depth++;
                    continue;
                }

                if (text[c] != '{')
                {
                    continue;
                }

                if (depth > 0)
                {
                    depth--;
                    continue;
                }

                var header = Header(code, i, c);

                if (TypeHeaderRegex.IsMatch(header))
                {
                    return Statement(code, index);
                }

                if (IsMember(header))
                {
                    return (HeaderStart(code, i, c), ClosingLine(code, i, c));
                }
            }
        }

        return Statement(code, index);
    }

    private static (int Start, int End) Statement(List<(int Line, string Text)> code, int index)
    {
        var start = index;
        while (start > 0 && !EndsStatement(code[start - 1].Text))
        {
            start--;
        }

        var end = index;
        while (end < code.Count - 1 && !code[end].Text.TrimEnd().EndsWith(';'))
        {
            end++;
        }

        return (start, end);
    }

    private static bool EndsStatement(string text)
    {
        var t = text.TrimEnd();
        return t.EndsWith(';') || t.EndsWith('{') || t.EndsWith('}');
    }

    private static bool IsMember(string header)
        => header.Contains('(', StringComparison.Ordinal)
           && !header.Contains("=>", StringComparison.Ordinal)
           && !ControlHeaderRegex.IsMatch(header);

    /// <summary>Заголовок блоку, що відкривається на рядку <paramref name="line"/> у позиції <paramref name="column"/>.</summary>
    private static string Header(List<(int Line, string Text)> code, int line, int column)
    {
        var first = HeaderStart(code, line, column);
        var parts = new List<string>();

        for (var i = first; i <= line; i++)
        {
            parts.Add(i == line ? code[i].Text[..column] : code[i].Text);
        }

        return string.Join(' ', parts).Trim();
    }

    private static int HeaderStart(List<(int Line, string Text)> code, int line, int column)
    {
        var first = line;
        if (code[line].Text[..column].Trim().Length == 0)
        {
            // `{` на власному рядку (Allman): заголовок — рядки вище.
            first = line - 1;
        }

        while (first > 0 && !EndsStatement(code[first - 1].Text) && !code[first - 1].Text.StartsWith('['))
        {
            first--;
        }

        return Math.Max(first, 0);
    }

    private static int ClosingLine(List<(int Line, string Text)> code, int line, int column)
    {
        var depth = 0;

        for (var i = line; i < code.Count; i++)
        {
            var text = code[i].Text;
            for (var c = i == line ? column : 0; c < text.Length; c++)
            {
                if (text[c] == '{')
                {
                    depth++;
                }
                else if (text[c] == '}' && --depth == 0)
                {
                    return i;
                }
            }
        }

        return code.Count - 1;
    }
}
