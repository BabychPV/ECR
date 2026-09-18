using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Каталог рядків збірки (<c>09-seed.sql</c>) написаний МОВОЮ КАТАЛОГУ і
/// нічим іншим: ані кирилиці, ані внутрішніх позначень для розробника.
/// </summary>
/// <remarks>
/// ⛔ Предмет. Мов у системі три — <c>en</c>, <c>ru</c>, <c>kz</c> (<c>D-95</c>),
/// і української серед них немає. Прохід інтерфейсом 2026-09-18
/// (<c>docs/build/UI-WALKTHROUGH.md</c>, F5) знайшов рівно один рядок із 1338,
/// який це порушував: <c>tables.readOnlyHint</c> закінчувався кодом вимоги
/// «(ФВ-7.1)» — позначкою, яка щось означає для того, хто пише шаблони, і
/// нічого для того, хто читає англійський екран.
///
/// ⚠ Чому це сторож, а не разова правка. Рядок прожив до живого проходу
/// саме тому, що ЛАМАТИ йому було нічого: SQL виконується, тест зелений, на
/// екрані текст. Помітити його можна було лише очима, а очі доходять до
/// <c>/admin/templates/:id/versions/:versionId/relations</c> у замороженій
/// версії нечасто. Такий дефект або ловить машина, або він повертається.
///
/// ⚠ Предмет перевірки — ЗНАЧЕННЯ, а не файл. У самому <c>09-seed.sql</c>
/// кирилиця законна й потрібна: коментарі пояснюють, чому рядок саме такий.
/// Сторож дивиться рівно в третю колонку рядка каталогу.
///
/// ⚠ Це не дублює <c>ErrorTitleCatalogTests</c>: там перевіряються КЛЮЧІ
/// (чи заведено заголовок під кожен код, чи немає в заголовку підстановок) і
/// жодного разу — сам текст.
/// </remarks>
public sealed partial class SeedCatalogTextTests
{
    /// <summary>Сід каталогу рядків відносно кореня репозиторію.</summary>
    private const string SeedFile = "src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql";

    /// <summary>Мова, якою заводяться рядки збірки (ФВ-14.9, <c>D-95</c>).</summary>
    private const string DefaultLanguage = "en";

    /// <summary>
    /// Ключі, значення яких свідомо містить кирилицю.
    /// </summary>
    /// <remarks>
    /// ⛔ Порожній, і це факт, а не поблажливість: єдиний такий рядок
    /// (<c>tables.readOnlyHint</c>) виправлено разом із появою цього сторожа.
    /// Перелік лишається ФОРМОЮ — той самий прийом, що
    /// <c>ErrorTitleCatalogTests.TitlesNotSeeded</c>: рядок, якому кирилиця
    /// справді потрібна (назва мови в перемикачі, власна назва), називається
    /// тут поіменно і з причиною, а не послаблює регулярку. Перевірка йде в
    /// ДВА боки — ключ, який кирилицю втратив, зобов'язаний звідси зникнути.
    /// </remarks>
    private static readonly string[] CyrillicByDesign = [];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-14.9")]
    public void Жоден_рядок_каталогу_не_написаний_кирилицею()
    {
        var rows = Rows();

        // ⛔ Без цього регулярка, яка перестала збігатися (сід змінив форму
        // рядка, блок переїхав), дала б порожній перелік і ЗЕЛЕНЕ.
        Assert.NotEmpty(rows);

        var offenders = rows
            .Where(row => Cyrillic().IsMatch(row.Value))
            .Select(row => row.Key)
            .Except(CyrillicByDesign, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // ⛔ `Assert.True` з готовим текстом, а не `Assert.Empty`: xUnit друкує
        // колекцію обрізаною, і зникає першою саме та частина, де сказано, що
        // робити.
        Assert.True(
            offenders.Count == 0,
            $"Рядки каталогу {SeedFile} з кирилицею — мов у системі три "
            + $"({DefaultLanguage}, ru, kz) і української серед них немає:"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                offenders.Select(key =>
                    $"  {key}: перепиши значення мовою каталогу. Внутрішні позначення "
                    + "(коди вимог «ФВ-…», «D-…», номери директив) у текст для користувача не йдуть — "
                    + "їхнє місце в коментарі поруч. Якщо кирилиця тут справді потрібна — "
                    + "назви ключ у CyrillicByDesign із причиною, а не розширюй перевірку.")));

        var healed = CyrillicByDesign
            .Where(key => rows.Any(
                row => string.Equals(row.Key, key, StringComparison.Ordinal)
                       && !Cyrillic().IsMatch(row.Value)))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            healed.Count == 0,
            "У CyrillicByDesign названі ключі, у яких кирилиці вже немає — прибери їх звідти: "
            + string.Join(", ", healed));
    }

    /// <summary>
    /// Сід заводить рядки ЛИШЕ мовою збірки: переклади — дані реєстру (D-95).
    /// </summary>
    /// <remarks>
    /// ⚠ Тримає чесним сторожа вище: рядок іншою мовою — це кирилиця, якій
    /// місце є, і без цієї перевірки її поява була б приводом розширити
    /// <c>CyrillicByDesign</c> замість розмови про те, що переклад поїхав у
    /// збірку.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-14.9")]
    public void Сід_заводить_рядки_лише_мовою_збірки()
    {
        var rows = Rows();

        Assert.NotEmpty(rows);

        var foreign = rows
            .Where(row => !string.Equals(row.Language, DefaultLanguage, StringComparison.Ordinal))
            .Select(row => $"{row.Key} ({row.Language})")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            foreign.Count == 0,
            $"У {SeedFile} заведено рядки не мовою збірки: {string.Join(", ", foreign)}. "
            + "Переклади — дані реєстру (PUT /api/v1/ui-strings/{lang}/{key}), а не збірки (D-95).");
    }

    /// <summary>Рядки блоку <c>MERGE sys_ecr.UiString</c>: ключ, мова, значення.</summary>
    /// <remarks>
    /// ⛔ Береться САМЕ блок каталогу, а не весь файл: поруч у сіді лежать
    /// <c>sec.Permission</c>, <c>uom.Unit</c> і решта, і їхні назви — дані
    /// реєстру, а не тексти інтерфейсу.
    /// </remarks>
    private static List<(string Key, string Language, string Value)> Rows()
    {
        var text = File.ReadAllText(
            Path.Combine(SourceTree.Root, SeedFile.Replace('/', Path.DirectorySeparatorChar)));

        // ⚠ Саме `AS t`, а не голе `MERGE sys_ecr.UiString`: на сорок рядків
        // вище стоїть `MERGE sys_ecr.UiStringRevision`, і префікс збігається.
        // Сторож, націлений на нього, брав блок у 96 символів, не знаходив
        // жодного рядка — і впав на `Assert.NotEmpty` замість того, щоб мовчки
        // позеленіти. Саме для цього та перевірка й стоїть.
        var start = text.IndexOf("MERGE sys_ecr.UiString AS t", StringComparison.Ordinal);
        Assert.True(start >= 0, $"У {SeedFile} немає блоку MERGE sys_ecr.UiString AS t — сторож дивиться не туди.");

        var end = text.IndexOf("WHEN NOT MATCHED", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Блок MERGE sys_ecr.UiString AS t у {SeedFile} не закінчується — сторож дивиться не туди.");

        return SeedRow().Matches(text[start..end])
            .Select(m => (
                Key: m.Groups[1].Value,
                Language: m.Groups[2].Value,
                Value: m.Groups[3].Value))
            .ToList();
    }

    /// <summary>
    /// Рядок сіду <c>(N'ключ', N'мова', N'значення', 0|1)</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>(?:[^']|'')*</c> перетинає переніс рядка навмисно: довгі підказки
    /// в сіді записані у два рядки, і перевірка по одному рядку пропустила б
    /// саме найдовші тексти — тобто ті, де внутрішня позначка найімовірніша.
    /// </remarks>
    [GeneratedRegex(@"\(\s*N'((?:[^']|'')*)'\s*,\s*N'([a-z]{2})'\s*,\s*N'((?:[^']|'')*)'\s*,\s*[01]\s*\)")]
    private static partial Regex SeedRow();

    /// <summary>Будь-яка кирилична літера, разом з українськими.</summary>
    [GeneratedRegex(@"[Ѐ-ӿ]")]
    private static partial Regex Cyrillic();
}
