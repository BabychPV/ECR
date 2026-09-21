using System.Text;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Кожне право з каталогу <c>sec.Permission</c> (<c>09-seed.sql</c>) перевіряє
/// хоч один обробник — або стоїть у <see cref="NotYetChecked"/> з причиною.
/// </summary>
/// <remarks>
/// ⛔ Предмет (<c>BE-28</c>). Право, яке можна видати й яке нічого не
/// відкриває, — галочка в ролі, якій адміністратор вірить: він «дав» або
/// «забрав» доступ, а поведінка не змінилась.
///
/// «Використання» — рядковий літерал коду права в КОДІ <c>src/Ecr.Application</c>
/// (<c>const string Permission = "…"</c>, <c>RequireAsync(…, "…", …)</c>).
/// Згадка в коментарі чи в тесті використанням не є; тому коментарі
/// вирізаються, а літерали — ні (<see cref="SourceTree.CodeLines"/> вирізає
/// обидва, тож тут власний сканер).
///
/// ⚠ Перелік винятків живе тут, а не окремим файлом: причина стоїть поруч
/// із твердженням, яке падає, і не потребує ще одного розбору тексту.
/// </remarks>
public sealed partial class UncheckedPermissionTests
{
    private const string SeedFile = "src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql";

    /// <summary>
    /// Права з сіду, яких станом на 2026-09-21 не перевіряє жоден обробник.
    /// </summary>
    /// <remarks>
    /// ⛔ Ratchet у два боки: нове неперевірене право без рядка тут — червоне;
    /// право звідси, яке отримало перевірку, — теж червоне, доки рядок не
    /// прибрано. Порожній перелік — законний кінцевий стан, а не збій.
    /// Закрити рядок можна двома шляхами: обробник із перевіркою або
    /// видалення права із сіду рішенням людини (як <c>Template.Migrate</c>).
    /// </remarks>
    private static readonly Dictionary<string, string> NotYetChecked = new(StringComparer.Ordinal)
    {
        ["Registry.Publish"] = "BE-24 крок 2: викликача ще немає; рішення людини 2026-09-21 — право лишити.",    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожне_право_з_сіду_перевіряє_обробник_або_воно_в_переліку()
    {
        var used = UsedLiterals();

        var missing = SeedPermissions()
            .Where(code => !used.Contains(code) && !NotYetChecked.ContainsKey(code))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"Права з {SeedFile}, яких не перевіряє жоден обробник у src/Ecr.Application:"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                missing.Select(code =>
                    $"  {code}: підключи перевірку (PermissionCheck.RequireAsync) або допиши в "
                    + "NotYetChecked з причиною одним рядком. Галочка, яка нічого не відкриває, — брехня в матриці доступу.")));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Перелік_неперевірених_прав_не_приховує_зробленого()
    {
        var used = UsedLiterals();
        var seed = SeedPermissions();

        var stale = NotYetChecked.Keys
            .Where(code => used.Contains(code) || !seed.Contains(code))
            .Order(StringComparer.Ordinal)
            .Select(code => used.Contains(code)
                ? $"  {code}: уже перевіряється в src/Ecr.Application — прибери з NotYetChecked."
                : $"  {code}: його немає в {SeedFile} — прибери з NotYetChecked.")
            .ToList();

        Assert.True(
            stale.Count == 0,
            "NotYetChecked застарів:" + Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    /// <summary>
    /// Самоперевірка сканера: без неї сканер, що перестав бачити літерали,
    /// оголосив би неперевіреним усе, а той, що перестав вирізати коментарі, —
    /// перевіреним усе, що згадано в документації.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Сканер_бере_літерали_коду_і_пропускає_коментарі()
    {
        const string sample = """"
            /// Право <c>"Doc.Comment"</c>.
            // "Line.Comment"
            /* "Block.Comment" */
            var url = "http://x"; var a = "Code.One";
            var b = $"{Get("Code.Two")}:{{x}}"; var c = @"say ""hi""";
            var d = '"'; var e = "Code.Three"; // "Tail.Comment"
            """";

        Assert.Equal(
            ["http://x", "Code.One", "Code.Two", "{}:{x}", "say \"hi\"", "Code.Three"],
            Literals(sample));

        // Число літералом: регулярка сіду, яка перестала збігатися, дала б
        // порожній каталог — і обидва сторожі вище позеленіли б мовчки.
        Assert.True(SeedPermissions().Count >= 40, $"У {SeedFile} розібрано підозріло мало прав.");
        Assert.Contains("Document.View", UsedLiterals());
    }

    private static HashSet<string> SeedPermissions()
    {
        var text = File.ReadAllText(
            Path.Combine(SourceTree.Root, SeedFile.Replace('/', Path.DirectorySeparatorChar)));

        var start = text.IndexOf("MERGE sec.Permission AS t", StringComparison.Ordinal);
        Assert.True(start >= 0, $"У {SeedFile} немає блоку MERGE sec.Permission AS t — сторож дивиться не туди.");
        var end = text.IndexOf("WHEN NOT MATCHED", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Блок MERGE sec.Permission AS t у {SeedFile} не закінчується.");

        var block = string.Join(
            '\n',
            text[start..end].Split('\n').Select(line =>
                line.IndexOf("--", StringComparison.Ordinal) is var dash and >= 0 ? line[..dash] : line));

        return PermissionRow().Matches(block)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> UsedLiterals() =>
        SourceTree.Production("Ecr.Application")
            .SelectMany(file => Literals(file.Text))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Вміст рядкових літералів коду C#, без коментарів.</summary>
    internal static List<string> Literals(string source)
    {
        var found = new List<string>();
        var i = 0;
        ScanCode(source, ref i, found, insideHole: false);
        return found;
    }

    private static void ScanCode(string s, ref int i, List<string> found, bool insideHole)
    {
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            var next = i + 1 < s.Length ? s[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                var eol = s.IndexOf('\n', i);
                i = eol < 0 ? s.Length : eol;
                continue;
            }

            if (c == '/' && next == '*')
            {
                var close = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? s.Length : close + 2;
                continue;
            }

            if (c == '\'')
            {
                i++;
                while (i < s.Length && s[i] != '\'')
                {
                    i += s[i] == '\\' ? 2 : 1;
                }

                i++;
                continue;
            }

            var quote = i;
            while (quote < s.Length && s[quote] is '$' or '@')
            {
                quote++;
            }

            if (quote < s.Length && s[quote] == '"' && (quote > i || c == '"'))
            {
                var prefix = s[i..quote];
                i = quote;
                found.Add(ReadString(s, ref i, found, prefix.Contains('@'), prefix.Contains('$')));
                continue;
            }

            if (insideHole && c == '{')
            {
                depth++;
            }
            else if (insideHole && c == '}')
            {
                if (depth == 0)
                {
                    return;
                }

                depth--;
            }

            i++;
        }
    }

    private static string ReadString(string s, ref int i, List<string> found, bool verbatim, bool interpolated)
    {
        var text = new StringBuilder();
        i++;
        while (i < s.Length)
        {
            var c = s[i];
            var next = i + 1 < s.Length ? s[i + 1] : '\0';

            if (c == '"' && verbatim && next == '"')
            {
                text.Append('"');
                i += 2;
                continue;
            }

            if (c == '"' || (c == '\n' && !verbatim))
            {
                i++;
                break;
            }

            if (c == '\\' && !verbatim)
            {
                text.Append(c).Append(next);
                i += 2;
                continue;
            }

            if (interpolated && (c is '{' or '}') && next == c)
            {
                text.Append(c);
                i += 2;
                continue;
            }

            if (interpolated && c == '{')
            {
                i++;
                ScanCode(s, ref i, found, insideHole: true);
                i++;
                text.Append("{}");
                continue;
            }

            text.Append(c);
            i++;
        }

        return text.ToString();
    }

    /// <summary>Рядок каталогу <c>(N'Група.Дія', N'Група', 0|1)</c>.</summary>
    [GeneratedRegex(@"\(\s*N'([A-Za-z]+\.[A-Za-z]+)'\s*,\s*N'[A-Za-z]+'\s*,\s*[01]\s*\)")]
    private static partial Regex PermissionRow();
}
