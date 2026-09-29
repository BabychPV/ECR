using System.Text.RegularExpressions;
using Ecr.Application.Localization;
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
/// встановлення. Тому поруч із <c>MERGE sys_ecr.UiString AS t</c> (лише en)
/// стоять окремі блоки перекладів — по одному на мову, з джерелом
/// <c>) AS v ([Key], Lang, Val)</c>. Область (<c>Scope</c>) переклад бере з
/// англійського рядка в самому SQL, тож тут її немає.
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

    [GeneratedRegex(@"\(\s*N'((?:[^']|'')*)'\s*,\s*N'([a-z]{2})'\s*,\s*N'((?:[^']|'')*)'\s*,\s*[01]\s*\)")]
    private static partial Regex CatalogRow();

    [GeneratedRegex(@"\(\s*N'((?:[^']|'')*)'\s*,\s*N'([a-z]{2})'\s*,\s*N'((?:[^']|'')*)'\s*\)")]
    private static partial Regex TranslationRow();

    [GeneratedRegex(@"[іїєґІЇЄҐ]")]
    private static partial Regex RuForeign();

    [GeneratedRegex(@"[їєґЇЄҐ]")]
    private static partial Regex KzForeign();
}
