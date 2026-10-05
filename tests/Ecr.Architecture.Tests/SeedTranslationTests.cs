using System.Text.RegularExpressions;
using Ecr.Application.Localization;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Базові переклади ru/kz у <c>09-seed.sql</c> (секції <c>I18N:</c>) не
/// розходяться з англійським каталогом.
/// </summary>
/// <remarks>
/// ⛔ Рішення людини 2026-09-29: мови продукту — en, ru, kz; переклади веде
/// людина, але базові тексти, що вже є, мусять лежати в БД одразу після
/// встановлення. Тому після батча з <c>MERGE sys_ecr.UiString AS t</c> (лише en)
/// стоять порції перекладів — кожна окремим батчем, з джерелом
/// <c>) AS v ([Key], Lang, Val)</c>, у тимчасову <c>#I18N</c>, звідки їх бере
/// один MERGE. Область (<c>Scope</c>) переклад бере з англійського рядка в
/// самому SQL, тож тут її немає.
///
/// ⚠ Що ловить сторож: переклад ключа, якого в каталозі вже немає (його
/// прибрали з MERGE, а переклад лишився — і вставлявся б на кожному старті
/// без оригіналу), підстановку, яку перекладач переклав або загубив
/// (<c>UiStringResolver.Format</c> лишив би сирі дужки), дубль (MERGE упав би
/// на <c>PK_UiString</c>), рядок, довший за <c>nvarchar(1000)</c>, і
/// українські літери в тексті мовою, де їх немає.
///
/// ⚠ Повноти (кожен en-ключ має ru і kz) сторож НЕ вимагає: нові ключі
/// перекладає людина через <c>PUT /api/v1/ui-strings/{lang}/{key}</c>, а
/// відсутній переклад падає на мову за замовчуванням, а не на порожнечу.
/// </remarks>
public sealed partial class SeedTranslationTests
{
    private const string SeedFile = "src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql";

    /// <summary>Хвіст блоку перекладів: джерело без області.</summary>
    private const string TranslationTail = ") AS v ([Key], Lang, Val)";

    private static readonly string[] TranslationLanguages = ["ru", "kz"];

    /// <summary>Межа <c>sys_ecr.UiString.Value nvarchar(1000)</c> (<c>08-system-tables.sql</c>).</summary>
    private const int MaxValueLength = 1000;

    /// <summary>
    /// Найбільша порція перекладів в одному батчі. Порція в 500 рядків
    /// компілюється за ~0.1 с і ~10 МБ; обидві мови одним MERGE (~6000 рядків)
    /// — 2.5 с і 110 МБ у батчі з en-MERGE.
    /// </summary>
    private const int MaxChunkRows = 500;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-14.9")]
    public void Переклади_лежать_порціями_кожна_окремим_батчем()
    {
        // ⛔ Регресія, яку стереже тест: переклади ru/kz одним MERGE на мову в
        // батчі з en-MERGE. Компіляція такого батча — 4.8 с замість 1.3, під
        // навантаженням 26 с при таймауті фікстури 30 с (`SqlServerFixture`), а
        // план у кеші — +10 МБ на КОЖНУ тестову базу. Семантику це не ламає,
        // тому жоден інтеграційний тест цього не бачить.
        //
        // ⚠ Батчі — рівно ті, що виконує `SeedRunner` (`SqlBatches.Split`).
        var batches = SqlBatches.Split(SeedText());

        var chunks = batches.Where(b => b.Contains(TranslationTail, StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(chunks);

        var problems = new List<string>();

        // Саме та форма, що й коштувала 4.8 с: переклади в батчі з en-MERGE.
        var catalog = batches.Single(b => b.Contains("MERGE sys_ecr.UiString AS t", StringComparison.Ordinal)
                                          && b.Contains("N'en'", StringComparison.Ordinal)
                                          && !b.Contains("#I18N", StringComparison.Ordinal));
        if (catalog.Contains(TranslationTail, StringComparison.Ordinal))
        {
            problems.Add("  блок перекладів у батчі з en-MERGE — винеси його в порції #I18N нижче.");
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var tails = CountOf(chunk, TranslationTail);
            var rows = TranslationRow().Count(chunk);

            if (tails != 1)
            {
                problems.Add($"  порція #{i + 1}: {tails} блоків VALUES в одному батчі — кожна порція окремим GO.");
            }

            if (rows > MaxChunkRows)
            {
                problems.Add($"  порція #{i + 1}: {rows} рядків, межа {MaxChunkRows} — розбий на кілька батчів.");
            }

            if (MergeStatement().IsMatch(chunk))
            {
                problems.Add($"  порція #{i + 1}: MERGE у батчі з VALUES перекладів — порція лише наповнює #I18N.");
            }

            if (!chunk.Contains("INSERT INTO #I18N", StringComparison.Ordinal)
                || !chunk.Contains("OPTION (RECOMPILE)", StringComparison.Ordinal))
            {
                problems.Add($"  порція #{i + 1}: не `INSERT INTO #I18N … OPTION (RECOMPILE)` — план із літералами осяде в кеші.");
            }
        }

        Assert.True(
            problems.Count == 0,
            $"Переклади в {SeedFile} не порціями:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-14.9")]
    public void Кожен_переклад_має_англійський_оригінал_і_відому_мову()
    {
        var text = SeedText();
        var catalog = CatalogRows(text);
        var translations = TranslationRows(text);

        // ⛔ Регулярка, що перестала збігатися, дала б порожній перелік і зелене.
        Assert.NotEmpty(catalog);
        Assert.NotEmpty(translations);

        var problems = new List<string>();
        foreach (var (key, lang, _) in translations)
        {
            if (!TranslationLanguages.Contains(lang, StringComparer.Ordinal))
            {
                problems.Add($"  {key} ({lang}): мови немає серед перекладів ({string.Join(", ", TranslationLanguages)}).");
            }

            if (!catalog.ContainsKey(key))
            {
                problems.Add(
                    $"  {key} ({lang}): ключа немає в MERGE sys_ecr.UiString (en). Прибрав ключ — прибери й переклад "
                    + "і додай рядок (ключ, мова, останнє значення) у секцію «Прибрані ключі».");
            }
        }

        problems.AddRange(translations
            .GroupBy(t => (t.Key, t.Lang))
            .Where(g => g.Count() > 1)
            .Select(g => $"  {g.Key.Key} ({g.Key.Lang}): переклад повторюється — MERGE упаде на PK_UiString."));

        Assert.True(
            problems.Count == 0,
            $"Переклади в {SeedFile} розійшлися з каталогом:" + Environment.NewLine
            + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-14.9")]
    public void Підстановки_перекладу_збігаються_з_оригіналом()
    {
        var text = SeedText();
        var catalog = CatalogRows(text);
        var translations = TranslationRows(text);

        Assert.NotEmpty(translations);

        var problems = new List<string>();
        foreach (var (key, lang, value) in translations)
        {
            if (!catalog.TryGetValue(key, out var original))
            {
                continue; // ловить перший сторож
            }

            // ⚠ Те саме правило, яким `PUT /ui-strings/{lang}/{key}` відхиляє
            // переклад адміністратора (BE-13): НАБІР підстановок, не порядок.
            if (!UiStringResolver.SamePlaceholders(original, value))
            {
                var expected = UiStringResolver.Placeholders(original);
                var actual = UiStringResolver.Placeholders(value);
                problems.Add(
                    $"  {key} ({lang}): підстановки en [{string.Join(" ", expected)}], "
                    + $"переклад [{string.Join(" ", actual)}].");
            }
        }

        Assert.True(
            problems.Count == 0,
            $"Переклади в {SeedFile} змінили підстановки — резолвер лишив би на екрані сирі дужки:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-14.9")]
    public void Переклад_вміщується_в_колонку_і_написаний_своєю_мовою()
    {
        var translations = TranslationRows(SeedText());

        Assert.NotEmpty(translations);

        var problems = new List<string>();
        foreach (var (key, lang, value) in translations)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                problems.Add($"  {key} ({lang}): порожній переклад.");
            }

            if (value.Length > MaxValueLength)
            {
                problems.Add($"  {key} ({lang}): {value.Length} символів, колонка вміщує {MaxValueLength}.");
            }

            // ⚠ «і» законна в казахській, але не в російській; «ї», «є», «ґ» —
            // лише українські. Мови `uk` у продукті немає.
            var foreign = string.Equals(lang, "ru", StringComparison.Ordinal) ? RuForeign() : KzForeign();
            if (foreign.IsMatch(value))
            {
                problems.Add($"  {key} ({lang}): літера, якої в цій мові немає (українська?): «{value}».");
            }
        }

        Assert.True(
            problems.Count == 0,
            $"Переклади в {SeedFile}:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Ключі, які <c>RegistrySyncJob</c> кладе в текст відмови (<c>messageKey=…</c>), заведені в каталозі
    /// en/ru/kz (рецензія an33b: <c>elementNameAmbiguous</c> був названий, але не заведений).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Ключі_відмов_синку_довідника_є_в_каталозі_трьома_мовами()
    {
        var source = File.ReadAllText(Path.Combine(
            SourceTree.Root, "src", "Ecr.Infrastructure", "Jobs", "RegistrySyncJob.cs"));
        var keys = Regex.Matches(source, @"messageKey=(err\.[A-Za-z0-9.\-]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Contains("err.ECR-INT-0422.elementNameAmbiguous", keys);

        var text = SeedText();
        var catalog = CatalogRows(text);
        var translations = TranslationRows(text);

        var missing = keys
            .SelectMany(key => (catalog.ContainsKey(key) ? [] : new[] { $"{key} en" })
                .Concat(TranslationLanguages
                    .Where(lang => !translations.Any(t => t.Key == key && t.Lang == lang))
                    .Select(lang => $"{key} {lang}")))
            .ToList();

        Assert.True(missing.Count == 0, $"Немає в {SeedFile}: " + string.Join(", ", missing));
    }

    private static string SeedText()
        => File.ReadAllText(Path.Combine(SourceTree.Root, SeedFile.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Англійський каталог: <c>MERGE sys_ecr.UiString AS t</c> до першого <c>WHEN NOT MATCHED</c>.</summary>
    private static Dictionary<string, string> CatalogRows(string text)
    {
        var start = text.IndexOf("MERGE sys_ecr.UiString AS t", StringComparison.Ordinal);
        Assert.True(start >= 0, $"У {SeedFile} немає блоку MERGE sys_ecr.UiString AS t.");

        var end = text.IndexOf("WHEN NOT MATCHED", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Блок MERGE sys_ecr.UiString AS t у {SeedFile} не закінчується.");

        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in CatalogRow().Matches(text[start..end]))
        {
            if (string.Equals(m.Groups[2].Value, "en", StringComparison.Ordinal))
            {
                rows[Unquote(m.Groups[1].Value)] = Unquote(m.Groups[3].Value);
            }
        }

        return rows;
    }

    /// <summary>Рядки всіх блоків перекладів (хвіст <see cref="TranslationTail"/>).</summary>
    private static List<(string Key, string Lang, string Value)> TranslationRows(string text)
    {
        var rows = new List<(string, string, string)>();
        for (var end = text.IndexOf(TranslationTail, StringComparison.Ordinal);
             end >= 0;
             end = text.IndexOf(TranslationTail, end + TranslationTail.Length, StringComparison.Ordinal))
        {
            var start = text.LastIndexOf("(VALUES", end, StringComparison.Ordinal);
            Assert.True(start >= 0, $"Блок перекладів у {SeedFile} не має VALUES.");

            rows.AddRange(TranslationRow().Matches(text[start..end])
                .Select(m => (Unquote(m.Groups[1].Value), m.Groups[2].Value, Unquote(m.Groups[3].Value))));
        }

        return rows;
    }

    private static string Unquote(string sql) => sql.Replace("''", "'", StringComparison.Ordinal);

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [GeneratedRegex(@"\(\s*N'((?:[^']|'')*)'\s*,\s*N'([a-z]{2})'\s*,\s*N'((?:[^']|'')*)'\s*,\s*[01]\s*\)")]
    private static partial Regex CatalogRow();

    [GeneratedRegex(@"\(\s*N'((?:[^']|'')*)'\s*,\s*N'([a-z]{2})'\s*,\s*N'((?:[^']|'')*)'\s*\)")]
    private static partial Regex TranslationRow();

    [GeneratedRegex(@"^\s*MERGE\s", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex MergeStatement();

    [GeneratedRegex(@"[іїєґІЇЄҐ]")]
    private static partial Regex RuForeign();

    [GeneratedRegex(@"[їєґЇЄҐ]")]
    private static partial Regex KzForeign();
}
