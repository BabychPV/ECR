using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Секції «змінені тексти» і «прибрані ключі» у <c>09-seed.sql</c> не
/// розходяться з каталогом.
/// </summary>
/// <remarks>
/// ⛔ Секція оновлює «старе → нове» на вже розгорнутих базах. «Нове», що не
/// збігається зі значенням у <c>MERGE sys_ecr.UiString</c>, дало б розгорнутій
/// базі один текст, а свіжій — інший; а наступна зміна того самого ключа без
/// рядка тут просто не дійшла б до розгорнутих баз.
/// </remarks>
public sealed partial class SeedTextUpdateTests
{
    private const string SeedFile = "src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-14.9")]
    public void Нове_значення_кожного_оновлення_збігається_з_каталогом()
    {
        var text = File.ReadAllText(
            Path.Combine(SourceTree.Root, SeedFile.Replace('/', Path.DirectorySeparatorChar)));

        var updates = UpdateRows(text);
        var catalog = CatalogRows(text);

        // ⛔ Регулярка, що перестала збігатися, дала б порожній перелік і зелене.
        Assert.NotEmpty(updates);
        Assert.NotEmpty(catalog);

        var problems = new List<string>();
        foreach (var (key, lang, oldValue, newValue) in updates)
        {
            if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                problems.Add($"  {key} ({lang}): старе й нове однакові — рядок нічого не оновлює.");
            }

            if (!catalog.TryGetValue((key, lang), out var current))
            {
                problems.Add($"  {key} ({lang}): ключа немає в MERGE sys_ecr.UiString.");
            }
            else if (!string.Equals(current, newValue, StringComparison.Ordinal))
            {
                problems.Add(
                    $"  {key} ({lang}): у секції нове «{newValue}», а в MERGE «{current}». "
                    + "Текст змінився ще раз — виправ «нове» в цьому рядку і додай рядок "
                    + $"«{newValue}» → «{current}».");
            }
        }

        var duplicates = updates
            .GroupBy(u => (u.Key, u.Lang, u.Old))
            .Where(g => g.Count() > 1)
            .Select(g => $"  {g.Key.Key} ({g.Key.Lang}): «{g.Key.Old}» повторюється.");
        problems.AddRange(duplicates);

        Assert.True(
            problems.Count == 0,
            $"Секція змінених текстів у {SeedFile} розійшлася з каталогом:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-14.9")]
    public void Прибраного_ключа_немає_в_каталозі()
    {
        var text = File.ReadAllText(
            Path.Combine(SourceTree.Root, SeedFile.Replace('/', Path.DirectorySeparatorChar)));

        var removed = Section(text, RemovedTail, RemovedRow())
            .Select(m => (Key: Unquote(m.Groups[1].Value), Lang: m.Groups[2].Value))
            .ToList();
        var catalog = CatalogRows(text);

        Assert.NotEmpty(removed);
        Assert.NotEmpty(catalog);

        // ⛔ Ключ в обох місцях: MERGE вставив би його знову, а наступний старт
        // видалив би — і так на кожному старті, з інкрементом Revision щоразу.
        var both = removed
            .Where(r => catalog.ContainsKey((r.Key, r.Lang)))
            .Select(r => $"{r.Key} ({r.Lang})")
            .ToList();

        Assert.True(
            both.Count == 0,
            $"Ключі з секції «Прибрані ключі» в {SeedFile} досі є в MERGE sys_ecr.UiString — "
            + "або поверни ключ (прибери рядок із секції), або прибери його з MERGE: "
            + string.Join(", ", both));
    }

    /// <summary>Рядки секції змінених текстів: ключ, мова, старе, нове.</summary>
    private static List<(string Key, string Lang, string Old, string New)> UpdateRows(string text)
        => Section(text, UpdateTail, UpdateRow())
            .Select(m => (Unquote(m.Groups[1].Value), m.Groups[2].Value,
                          Unquote(m.Groups[3].Value), Unquote(m.Groups[4].Value)))
            .ToList();

    /// <summary>Рядки <c>VALUES</c> секції, що закінчується <paramref name="tail"/>.</summary>
    private static MatchCollection Section(string text, string tail, Regex row)
    {
        var end = text.IndexOf(tail, StringComparison.Ordinal);
        Assert.True(end >= 0, $"У {SeedFile} немає секції, що закінчується «{tail}».");

        var start = text.LastIndexOf("(VALUES", end, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Секція «{tail}» у {SeedFile} не має VALUES.");

        return row.Matches(text[start..end]);
    }

    /// <summary>Рядки <c>MERGE sys_ecr.UiString AS t</c>: (ключ, мова) → значення.</summary>
    private static Dictionary<(string, string), string> CatalogRows(string text)
    {
        var start = text.IndexOf("MERGE sys_ecr.UiString AS t", StringComparison.Ordinal);
        Assert.True(start >= 0, $"У {SeedFile} немає блоку MERGE sys_ecr.UiString AS t.");

        var end = text.IndexOf("WHEN NOT MATCHED", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Блок MERGE sys_ecr.UiString AS t у {SeedFile} не закінчується.");

        var rows = new Dictionary<(string, string), string>();
        foreach (Match m in CatalogRow().Matches(text[start..end]))
        {
            rows[(Unquote(m.Groups[1].Value), m.Groups[2].Value)] = Unquote(m.Groups[3].Value);
        }

        return rows;
    }

    private static string Unquote(string sql) => sql.Replace("''", "'", StringComparison.Ordinal);

    private const string UpdateTail = ") AS s ([Key], Lang, OldVal, NewVal)";

    private const string RemovedTail = ") AS s ([Key], Lang, OldVal)";

    [GeneratedRegex(@"\(\s*N'((?:[^']|'')*)'\s*,\s*N'([a-z]{2})'\s*,\s*N'((?:[^']|'')*)'\s*,\s*N'((?:[^']|'')*)'\s*\)")]
    private static partial Regex UpdateRow();

    [GeneratedRegex(@"\(\s*N'((?:[^']|'')*)'\s*,\s*N'([a-z]{2})'\s*,\s*N'((?:[^']|'')*)'\s*\)")]
    private static partial Regex RemovedRow();

    [GeneratedRegex(@"\(\s*N'((?:[^']|'')*)'\s*,\s*N'([a-z]{2})'\s*,\s*N'((?:[^']|'')*)'\s*,\s*[01]\s*\)")]
    private static partial Regex CatalogRow();
}
