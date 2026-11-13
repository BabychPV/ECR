// tests/Ecr.Architecture.Tests/DocumentTemplateVersionDirectCallGuardTests.cs
using System.Text.RegularExpressions;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// B74 P2 (пункт 1, R1): де в запиті вже є рядок <c>doc.Document</c>, версію шаблону за період дає
/// <c>doc.fn_TemplateVersionOf(d.ProjectId, d.TemplateId, …)</c> НАПРЯМУ, а не <c>doc.fn_VersionOf</c>. Після PT01
/// <c>fn_VersionOf</c> — вкладена скалярна UDF (основний шаблон проєкту → <c>fn_TemplateVersionOf</c>): без inlining
/// (поріг SQL 2016) читання екземплярів року документа 96 подорожчало 14.5 → 25.1 мс, а документу другого шаблону вона
/// дає версію ОСНОВНОГО шаблону — тобто неправду.
/// </summary>
/// <remarks>
/// Єдина дозволена форма <c>fn_VersionOf</c> поруч із документом — запасна гілка для <c>TemplateId IS NULL</c> (рядок
/// старої збірки у вікні відкату або до ремонту <c>99-epilogue.sql</c>): там вона дає рівно те, що й досі.
/// <list type="bullet">
/// <item>Сирий SQL (рядки в C# і <c>*.sql</c> під <c>src/</c>, без <c>Migrations/</c>): оператор (між <c>;</c>), що
/// згадує <c>doc.Document</c>, не кличе <c>fn_VersionOf</c> інакше, ніж
/// <c>WHEN &lt;псевдонім&gt;.TemplateId IS NULL THEN doc.fn_VersionOf(</c>.</item>
/// <item>LINQ (C#): оператор, що звертається до <c>.Documents</c>, не кличе <c>EcrDbContext.VersionOf(</c> інакше, ніж
/// <c>….TemplateId == null ? EcrDbContext.VersionOf(</c>.</item>
/// </list>
/// Коментарі (<c>//</c>, <c>/* */</c>, SQL <c>--</c>) вирізаються до пошуку. Виклики без документа в операторі (рівень
/// проєкту: мітка версії групи, зріз звіту) — поза предметом. Перелік винятків порожній; храповик — лише вниз.
/// Самоперевірки нижче доводять сканер на синтетиці. Мутаційний доказ (локально): повернути
/// <c>doc.fn_VersionOf(d.ProjectId, x.PeriodKey)</c> у <c>CalculationResultStore.Year.cs</c> — червоніє
/// <see cref="Поруч_із_doc_Document_сирий_SQL_не_кличе_fn_VersionOf"/>.
/// </remarks>
public sealed partial class DocumentTemplateVersionDirectCallGuardTests
{
    /// <summary>Обґрунтовані винятки: файл → кількість (порожньо).</summary>
    private static readonly Dictionary<string, int> AllowedSql = new(StringComparer.Ordinal);

    /// <summary>Обґрунтовані винятки LINQ: файл → кількість (порожньо).</summary>
    private static readonly Dictionary<string, int> AllowedLinq = new(StringComparer.Ordinal);

    [Fact]
    [Trait("Requirement", "B74")]
    public void Поруч_із_doc_Document_сирий_SQL_не_кличе_fn_VersionOf()
        => AssertRatchet(Scan(SqlNestedCalls, ".cs", ".sql"), AllowedSql,
            "Сирий SQL із doc.Document кличе doc.fn_VersionOf (вкладена UDF, версія ОСНОВНОГО шаблону). Пиши "
            + "CASE WHEN d.TemplateId IS NULL THEN doc.fn_VersionOf(d.ProjectId, k) "
            + "ELSE doc.fn_TemplateVersionOf(d.ProjectId, d.TemplateId, k) END (B74 P2).");

    [Fact]
    [Trait("Requirement", "B74")]
    public void Поруч_із_Documents_LINQ_не_кличе_EcrDbContext_VersionOf()
        => AssertRatchet(Scan(LinqNestedCalls, ".cs"), AllowedLinq,
            "LINQ із db.Documents кличе EcrDbContext.VersionOf (вкладена UDF, версія ОСНОВНОГО шаблону). Пиши "
            + "d.TemplateId == null ? EcrDbContext.VersionOf(d.ProjectId, k) "
            + ": EcrDbContext.TemplateVersionOf(d.ProjectId, d.TemplateId.Value, k) (B74 P2).");

    [Theory]
    [Trait("Requirement", "B74")]
    [InlineData("SELECT doc.fn_VersionOf(d.ProjectId, t.PeriodKey) FROM arc.TableInstance t JOIN doc.Document d ON d.Id = t.DocumentId;", 1)]
    [InlineData("SELECT [doc].[fn_VersionOf](d.ProjectId, 1) FROM [doc].[Document] AS d", 1)]
    [InlineData("SELECT CASE WHEN d.TemplateId IS NULL THEN doc.fn_VersionOf(d.ProjectId, 1) ELSE doc.fn_TemplateVersionOf(d.ProjectId, d.TemplateId, 1) END FROM doc.Document d;", 0)]
    [InlineData("SELECT CASE WHEN @k IS NULL THEN 0 WHEN d.TemplateId IS NULL THEN doc.fn_VersionOf(d.ProjectId, @k) END, doc.fn_VersionOf(p.Id, @k) FROM doc.Document d JOIN doc.Project p ON p.Id = d.ProjectId;", 1)]
    [InlineData("SELECT doc.fn_VersionOf(p.Id, 1) FROM doc.Project p;", 0)]
    [InlineData("SELECT doc.fn_VersionOf(p.Id, 1) FROM doc.Project p; SELECT 1 FROM doc.Document d;", 0)]
    [InlineData("SELECT doc.fn_VersionOf(d.ProjectId, 1) FROM doc.DocumentSheet s JOIN doc.Project p ON 1 = 1;", 0)]
    [InlineData("-- doc.fn_VersionOf(d.ProjectId, 1) FROM doc.Document d\nSELECT 1;", 0)]
    [InlineData("/* SELECT doc.fn_VersionOf(d.ProjectId, 1) FROM doc.Document d */ SELECT 1;", 0)]
    [InlineData("// var s = \"SELECT doc.fn_VersionOf(d.ProjectId, 1) FROM doc.Document d\";\nvar y = 1;", 0)]
    public void Сканер_SQL_бачить_вкладений_виклик_і_пропускає_запасну_гілку(string text, int expected)
        => Assert.Equal(expected, SqlNestedCalls(text).Count);

    [Theory]
    [Trait("Requirement", "B74")]
    [InlineData("var v = await db.Documents.Where(d => d.Id == id).Select(d => EcrDbContext.VersionOf(d.ProjectId, k)).FirstAsync();", 1)]
    [InlineData("var v = from d in db.Documents join p in db.Projects on d.ProjectId equals p.Id select EcrDbContext.VersionOf(p.Id, k);", 1)]
    [InlineData("var v = db.Documents.Select(d => d.TemplateId == null ? EcrDbContext.VersionOf(d.ProjectId, k) : EcrDbContext.TemplateVersionOf(d.ProjectId, d.TemplateId.Value, k));", 0)]
    [InlineData("var v = db.Documents.Select(x => x.TemplateId == null\n    ? EcrDbContext.VersionOf(x.ProjectId, k)\n    : EcrDbContext.TemplateVersionOf(x.ProjectId, x.TemplateId.Value, k));", 0)]
    [InlineData("var v = db.Projects.Select(p => EcrDbContext.VersionOf(p.Id, k));", 0)]
    [InlineData("// db.Documents.Select(d => EcrDbContext.VersionOf(d.ProjectId, k));\nvar y = 1;", 0)]
    public void Сканер_LINQ_бачить_вкладений_виклик_і_пропускає_запасну_гілку(string text, int expected)
        => Assert.Equal(expected, LinqNestedCalls(text).Count);

    /// <summary>Файл (відносно кореня) → номери рядків порушень.</summary>
    private static Dictionary<string, List<int>> Scan(Func<string, List<int>> scanner, params string[] extensions)
    {
        var found = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var path in SourceTree.Walk(Path.Combine(SourceTree.Root, "src")))
        {
            var relative = Path.GetRelativePath(SourceTree.Root, path).Replace('\\', '/');
            if (!extensions.Any(e => relative.EndsWith(e, StringComparison.Ordinal))
                || relative.Contains("/Migrations/", StringComparison.Ordinal))
            {
                continue;
            }

            var hits = scanner(File.ReadAllText(path));
            if (hits.Count > 0)
            {
                found[relative] = hits;
            }
        }

        return found;
    }

    /// <summary>Номери рядків <c>fn_VersionOf</c> у операторах із <c>doc.Document</c>, крім запасної гілки NULL.</summary>
    internal static List<int> SqlNestedCalls(string text)
        => Violations(text, DocumentTable(), SqlFallback(), SqlCall());

    /// <summary>Номери рядків <c>EcrDbContext.VersionOf</c> у операторах із <c>.Documents</c>, крім запасної гілки null.</summary>
    internal static List<int> LinqNestedCalls(string text)
        => Violations(text, DocumentsSet(), LinqFallback(), LinqCall());

    private static List<int> Violations(string text, Regex scope, Regex fallback, Regex call)
    {
        // Коментарі - пробілами тієї ж довжини, переноси рядків зберігаються: номери рядків лишаються точними.
        var clean = Comment().Replace(text, m => new string(m.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()));
        var result = new List<int>();
        var offset = 0;
        foreach (var statement in clean.Split(';'))
        {
            if (scope.IsMatch(statement))
            {
                // Запасна гілка (той самий виклик, що й до B74, лише для TemplateId NULL) - пробілами тієї ж довжини.
                var rest = fallback.Replace(statement, m => new string(' ', m.Length));
                result.AddRange(call.Matches(rest).Select(h => 1 + clean.AsSpan(0, offset + h.Index).Count('\n')));
            }

            offset += statement.Length + 1;
        }

        return result;
    }

    private static void AssertRatchet(Dictionary<string, List<int>> found, Dictionary<string, int> allowed, string hint)
    {
        var bad = found.Where(f => f.Value.Count > allowed.GetValueOrDefault(f.Key))
            .Select(f => $"{f.Key}: рядки {string.Join(",", f.Value)}").ToList();
        Assert.True(bad.Count == 0, hint + "\n  " + string.Join("\n  ", bad));

        var stale = allowed.Where(a => found.GetValueOrDefault(a.Key)?.Count is not { } c || c < a.Value)
            .Select(a => $"{a.Key}: дозволено {a.Value}, є {found.GetValueOrDefault(a.Key)?.Count ?? 0}").ToList();
        Assert.True(stale.Count == 0, "Винятків поменшало - зменш перелік (храповик):\n  " + string.Join("\n  ", stale));
    }

    // Той самий вирізувач, що в ProjectTemplateVersionB74GuardTests: `//` не після `:` / лапок / слова, `/* */`, `--` з
    // пробілом або кінцем рядка (не `i--`).
    [GeneratedRegex(@"(?<![:""'\w/])//[^\r\n]*|/\*[\s\S]*?\*/|(?<![\w)\]'""\-])--(?=[ \t\r\n]|$)[^\r\n]*", RegexOptions.Multiline)]
    private static partial Regex Comment();

    // doc.Document / [doc].[Document] (не DocumentSheet, не DocumentPeriodReopen).
    [GeneratedRegex(@"\[?doc\]?\s*\.\s*\[?Document\]?(?!\w)", RegexOptions.IgnoreCase)]
    private static partial Regex DocumentTable();

    [GeneratedRegex(@"\[?doc\]?\s*\.\s*\[?fn_VersionOf\]?\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex SqlCall();

    [GeneratedRegex(
        @"WHEN\s+\[?\w+\]?\s*\.\s*\[?TemplateId\]?\s+IS\s+NULL\s+THEN\s+\[?doc\]?\s*\.\s*\[?fn_VersionOf\]?\s*\(",
        RegexOptions.IgnoreCase)]
    private static partial Regex SqlFallback();

    [GeneratedRegex(@"\.\s*Documents\b")]
    private static partial Regex DocumentsSet();

    [GeneratedRegex(@"\bEcrDbContext\s*\.\s*VersionOf\s*\(")]
    private static partial Regex LinqCall();

    [GeneratedRegex(@"\.\s*TemplateId\s*==\s*null\s*\?\s*EcrDbContext\s*\.\s*VersionOf\s*\(")]
    private static partial Regex LinqFallback();
}
