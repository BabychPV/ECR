using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Кожен код помилки має ЗАГОЛОВОК у каталозі рядків: ключ рівно
/// <c>err.&lt;код&gt;</c> у <c>09-seed.sql</c>.
/// </summary>
/// <remarks>
/// ⛔ Предмет. <c>ExceptionHandlingMiddleware.LocalizedTitleAsync</c> бере
/// <c>Title</c> відповіді <c>problem+json</c> за ключем рівно
/// <c>err.&lt;код&gt;</c> — без суфікса. Ключа немає — повертається САМ КОД, і
/// клієнт друкує його першим рядком плашки (<c>ErrorAlert.tsx</c> рендерить
/// <c>problem.title</c> заголовком). Замір до цієї роботи: 76 кодів у
/// <c>ErrorCodes.cs</c> проти 11 заголовків у сіді — для 65 кодів користувач
/// бачив заголовком «ECR-CALC-0437», а під ним людське речення. Дефект
/// мовчазний за побудовою: відсутній ключ нічого не ламає й не логується.
///
/// ⚠ Чому сторож ЗА ДЖЕРЕЛАМИ, а не за базою. Розрив відкривається в момент,
/// коли в <c>ErrorCodes.cs</c> дописують константу, — тобто в тому самому
/// комміті, який має дописати рядок у сід. Перевірка по піднятій базі побачила
/// б це лише на інтеграційному прогоні (категорія <c>Integration</c>, поза
/// звичайним <c>test</c>), а перевірка по джерелах — одразу і без СУБД.
///
/// ⚠ Джерело істини про коди — <c>ErrorCodes.cs</c>, а не таблиця §7
/// <c>02-contracts.md</c>: у таблиці є коди, яких у каталозі констант немає
/// (<c>ECR-JOB-0404</c>, <c>ECR-UOM-4041</c> — вони живуть рядковими
/// літералами). Звірку таблиці з тим, що кидає <c>src/</c>, робить
/// <c>ContractIntegrityTests</c>, і дублювати її тут нічого.
/// </remarks>
public sealed partial class ErrorTitleCatalogTests
{
    /// <summary>Каталог констант відносно кореня репозиторію.</summary>
    private const string CatalogFile = "src/Ecr.Domain/Errors/ErrorCodes.cs";

    /// <summary>Сід каталогу рядків відносно кореня репозиторію.</summary>
    private const string SeedFile = "src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql";

    /// <summary>Мова, якою заводяться рядки збірки (ФВ-14.9, <c>D-95</c>).</summary>
    /// <remarks>
    /// ⚠ <c>ru</c>/<c>kz</c> у сіді немає НАВМИСНО: переклади — дані реєстру,
    /// а незаведена мова підміняється мовою за замовчуванням (ФВ-14.9).
    /// Вимагати їх тут означало б вимагати релізу на кожну мову.
    /// </remarks>
    private const string DefaultLanguage = "en";

    /// <summary>
    /// Коди, заголовок яких свідомо НЕ заводиться.
    /// </summary>
    /// <remarks>
    /// ⛔ Порожній, і це не поблажливість, а факт: сенс кожного з 76 кодів
    /// читається або з таблиці §7 <c>02-contracts.md</c>, або з XML-коментаря
    /// в <c>ErrorCodes.cs</c>, тож вигадувати не довелося нічого. Список
    /// лишається як ФОРМА: код, для якого з обох джерел не зрозуміло, ЩО це
    /// за стан, називається тут поіменно і з причиною — мовчазне послаблення
    /// (регулярка ширше, ніж треба; «ну 500-ті не рахуємо») заборонене.
    ///
    /// ⚠ Перевірка йде в ДВА боки: код звідси, для якого заголовок з'явився,
    /// зобов'язаний зі списку зникнути — інакше виняток тихо переживе причину,
    /// якою його виправдали (той самий прийом, що
    /// <c>ContractIntegrityTests.ReservedCodes</c>).
    /// </remarks>
    private static readonly string[] TitlesNotSeeded = [];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-14.9a")]
    public void Кожен_код_помилки_має_заголовок_у_каталозі()
    {
        var codes = Codes();
        var titles = Titles();

        // ⛔ Без цього регулярка, яка перестала збігатися (каталог переїхав,
        // сід змінив форму рядка), дала б порожні множини і ЗЕЛЕНЕ.
        Assert.NotEmpty(codes);
        Assert.NotEmpty(titles);

        var missing = codes
            .Where(code => !titles.Contains(code))
            .Except(TitlesNotSeeded, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // ⛔ `Assert.True` з готовим текстом, а не `Assert.Empty`: xUnit друкує
        // колекцію обрізаною, і зникає першою саме та частина, де сказано, що
        // робити. Сторож, чиє повідомлення не дочитати, вимагає йти читати його
        // код.
        Assert.True(
            missing.Count == 0,
            $"Коди без заголовка в {SeedFile} — заголовком плашки поїде сам код:"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                missing.Select(code =>
                    $"  {code}: додай рядок (N'err.{code}', N'{DefaultLanguage}', N'<коротка називна фраза>', <0|1>) "
                    + $"у блок заголовків {SeedFile}. Заголовок — перший рядок плашки, "
                    + $"БЕЗ плейсхолдерів і без подробиці (її несе Detail). "
                    + $"Сенс коду — у таблиці §7 docs/build/02-contracts.md і в XML-коментарі {CatalogFile}. "
                    + $"Якщо сенс коду не встановлюється з жодного з двох — назви його в TitlesNotSeeded із причиною, "
                    + $"а не розширюй перевірку.")));

        // Другий бік: виняток, який заголовок отримав, зобов'язаний зникнути.
        var obsolete = TitlesNotSeeded
            .Where(titles.Contains)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            obsolete.Count == 0,
            "У TitlesNotSeeded названі коди, у яких заголовок УЖЕ є — прибери їх звідти: "
            + string.Join(", ", obsolete));

        // І третій: виняток на код, якого в каталозі констант більше немає.
        var unknown = TitlesNotSeeded
            .Where(code => !codes.Contains(code))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unknown.Count == 0,
            $"У TitlesNotSeeded названі коди, яких немає в {CatalogFile}: "
            + string.Join(", ", unknown));
    }

    /// <summary>
    /// Ключі рівно <c>err.&lt;код&gt;</c>, які сьогодні служать ОДНОЧАСНО
    /// заголовком і подробицею, тому містять плейсхолдер.
    /// </summary>
    /// <remarks>
    /// ⛔ Це визнаний дефект, а не дозвіл. Заголовок резолвиться БЕЗ підстановок
    /// (<c>LocalizedTitleAsync</c> бере текст як є, <c>Format</c> не кличе), тож
    /// користувач бачить першим рядком плашки сирий <c>{code}</c>, а другим —
    /// те саме речення вдруге, уже з підставленим значенням. Кожен із цих ключів
    /// названий у <c>Details["messageKey"]</c> конкретного кидка, і зняти
    /// дефект — це перейменувати ключ на суфіксовану форму
    /// (<c>err.&lt;код&gt;.&lt;що саме&gt;</c>, як уже зроблено для
    /// <c>ECR-TMPL-4227</c>, <c>ECR-DOC-0404</c>, <c>ECR-REQ-0422</c>) І
    /// поправити кидок у <c>src/</c>. Це окрема робота в коді, не правка сіду.
    ///
    /// ⚠ Сторож тримає перелік ЗАМІРОМ у два боки: новий заголовок із
    /// плейсхолдером червоніє одразу, а ключ, який плейсхолдер утратив,
    /// зобов'язаний із переліку зникнути.
    /// </remarks>
    private static readonly string[] TitlesDoublingAsDetail =
    [
        "err.ECR-CFG-0422",  // EcrCode.Create — {code}
        "err.ECR-PRJ-0409",  // UnitOfWork — {code}
        "err.ECR-REG-0409",  // UnitOfWork, UpsertRegistryEntryHandler — {code}, {id}
        "err.ECR-REG-4091",  // CreateRegistryHandler — {code}, {id}
        "err.ECR-SEC-0409",  // UserStore — {code}
        "err.ECR-UOM-4091",  // CreateUnitHandler — {code}, {id}
        "err.ECR-USR-0409",  // RoleAndUserHandlers — {userName}
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-14.9a")]
    public void Заголовок_не_містить_плейсхолдерів()
    {
        var rows = TitleRows();

        Assert.NotEmpty(rows);

        var withPlaceholder = rows
            .Where(row => Placeholder().IsMatch(row.Value))
            .Select(row => row.Key)
            .Except(TitlesDoublingAsDetail, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            withPlaceholder.Count == 0,
            $"Заголовки з плейсхолдером у {SeedFile}: {string.Join(", ", withPlaceholder)}. "
            + "LocalizedTitleAsync не робить підстановок — користувач побачить сирі фігурні дужки. "
            + "Перенеси речення з підстановками на суфіксований ключ (err.<код>.<що саме>), "
            + "а під err.<код> лиши коротку називну фразу.");

        var healed = TitlesDoublingAsDetail
            .Where(key => rows.Any(
                row => string.Equals(row.Key, key, StringComparison.Ordinal)
                       && !Placeholder().IsMatch(row.Value)))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            healed.Count == 0,
            "У TitlesDoublingAsDetail названі ключі, у яких плейсхолдера вже немає — прибери їх звідти: "
            + string.Join(", ", healed));
    }

    /// <summary>Коди з <c>ErrorCodes.cs</c>.</summary>
    /// <remarks>
    /// ⚠ Читається ТЕКСТ, а не рефлексія по типу: рефлексія не помітила б, що
    /// каталог переїхав чи зник, — а сторож із порожньою множиною зелений.
    /// </remarks>
    private static HashSet<string> Codes()
    {
        var catalog = SourceTree.Production().Single(
            f => string.Equals(f.Path, CatalogFile, StringComparison.Ordinal));

        return Constant().Matches(catalog.Text)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Коди, під які сід заводить заголовок (ключ рівно <c>err.&lt;код&gt;</c>).</summary>
    private static HashSet<string> Titles()
        => TitleRows()
            .Select(row => row.Key["err.".Length..])
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Рядки сіду з ключем рівно <c>err.&lt;код&gt;</c> мовою збірки.</summary>
    /// <remarks>
    /// ⛔ Суфіксовані ключі (<c>err.&lt;код&gt;.&lt;що саме&gt;</c>) сюди НЕ
    /// потрапляють, і це весь сенс перевірки: їх читає
    /// <c>LocalizedDetailAsync</c> для подробиці, а заголовка з них не вийде
    /// ніколи. Доки різницю не бачив сторож, десять таких ключів на
    /// <c>ECR-DOC-0404</c> виглядали як «код локалізовано», хоча заголовком
    /// плашки так і їхав сам код.
    /// </remarks>
    private static List<(string Key, string Value)> TitleRows()
    {
        var text = File.ReadAllText(Path.Combine(SourceTree.Root, SeedFile.Replace('/', Path.DirectorySeparatorChar)));

        return SeedRow().Matches(text)
            .Select(m => (Key: m.Groups[1].Value, Value: m.Groups[2].Value))
            .ToList();
    }

    [GeneratedRegex(@"public const string \w+\s*=\s*""(ECR-[A-Z]{3,4}-\d{4})""")]
    private static partial Regex Constant();

    /// <summary>
    /// Рядок сіду <c>(N'err.ECR-…', N'en', N'…', 0|1)</c> — БЕЗ суфікса в ключі.
    /// </summary>
    [GeneratedRegex(
        @"\(\s*N'(err\.ECR-[A-Z]{3,4}-\d{4})'\s*,\s*N'"
        + DefaultLanguage
        + @"'\s*,\s*N'((?:[^']|'')*)'\s*,\s*[01]\s*\)")]
    private static partial Regex SeedRow();

    /// <summary>Підстановка <c>{ім'я}</c> — той самий синтаксис, що в <c>UiStringResolver.Format</c>.</summary>
    [GeneratedRegex(@"\{[A-Za-z_]\w*\}")]
    private static partial Regex Placeholder();
}
