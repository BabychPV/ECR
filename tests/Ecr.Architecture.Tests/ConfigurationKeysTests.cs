using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Конфігурація не бреше: кожен ключ `appsettings.json` має читача в коді, і
/// кожен ключ, який код читає, є у файлі (D14-06).
/// </summary>
/// <remarks>
/// ⛔ Предмет, знайдений `S-11`/`S-13`. Перейменування `Sql:` → `Database:`
/// зробили лише у ФАЙЛІ; обидва читачі лишилися на старому префіксі, і
/// `ReadInt(configuration, "Sql:BulkBatchSize", 5_000)` тихо повертав СВІЙ
/// дефолт. Пакетне завантаження йшло по 5 000 замість оголошених 50 000 — і
/// цього не видно НІЯК: застосунок працює, тести зелені, у логах нічого.
/// Сусідній `Sql:CommandTimeoutSeconds` іще підступніший: його дефолт (60)
/// ВИПАДКОВО дорівнює значенню у файлі, тож дефект став би видимим лише тоді,
/// коли адміністратор змінить таймаут і нічого не станеться.
///
/// Зворотний бік тієї самої вади — ключ, який код читає, а файл не оголошує
/// (`Auth:EnableNegotiate`, `Auth:StampCacheSeconds`): ручка існує, але
/// адміністратор дізнається про неї лише з вихідних текстів.
///
/// ⚠ Чому перевірка по ТЕКСТУ джерел, а не по поведінці. Ключ читається в
/// десятку різних місць і форм (`configuration["X"]`, `GetValue("X", …)`,
/// власний `ReadInt`), частина — на старті хоста, який у тесті не піднімеш
/// без бази. Текст — єдине спільне, що є в усіх цих форм.
///
/// ⚠ Крихкість сторожів по тексту в цьому проєкті вже двічі коштувала
/// червоного гейта, тому тут: (1) прив'язка до РЯДКА КЛЮЧА, а не до форми
/// виклику; (2) коментарі вирізаються — інакше сторож зеленів би на власному
/// поясненні (тут це саме ХИБНО-ЗЕЛЕНЕ, бо напрям «а» шукає згадку ключа);
/// (3) самоперевірка на зразках перед кожним твердженням — регулярка, що
/// перестала збігатися, інакше дала б порожній перелік і зелене.
///
/// ⛔ <c>SourceTree.CodeLines()</c> тут НЕ годиться, хоч його й вимагає
/// звичка: він замінює вміст рядкових літералів на порожній
/// (<c>StringLiteral().Replace(line, "\"\"")</c>) — тобто прибирає рівно те,
/// що ми шукаємо. Тому нижче власний прохід: вирізає коментарі, ЛИШАЄ
/// літерали.
/// </remarks>
public sealed partial class ConfigurationKeysTests
{
    /// <summary>Файл налаштувань, який перевіряємо.</summary>
    /// <remarks>
    /// ⚠ Лише базовий. `appsettings.Development.json` і
    /// `appsettings.Production.json` — накладки: перший лише ПЕРЕкриває
    /// оголошене тут, другий порожній за задумом (`10-installer.md`).
    /// </remarks>
    private const string AppSettingsPath = "src/Ecr.Api/appsettings.json";

    /// <summary>
    /// Ключі, які читає САМ ХОСТ, а не наш код.
    /// </summary>
    /// <remarks>
    /// ⚠ Без цього переліку напрям «а» дав би хибне спрацювання на трьох
    /// цілком робочих ключах: <c>AllowedHosts</c> розбирає
    /// <c>HostFilteringMiddleware</c>, <c>Logging:*</c> — фабрика
    /// журналювання, а <c>ConnectionStrings:Ecr</c> ми читаємо
    /// <c>GetConnectionString("Ecr")</c>, тобто повного рядка ключа в
    /// джерелах немає в принципі.
    /// </remarks>
    private static readonly string[] HostOwnedKeys =
    [
        "AllowedHosts",
        "ConnectionStrings:Ecr",
    ];

    /// <summary>Префікси ключів, які читає сам хост.</summary>
    private static readonly string[] HostOwnedPrefixes =
    [
        "Logging:",
    ];

    /// <summary>
    /// Ключі у файлі без читача — навмисно і на визначений строк.
    /// </summary>
    /// <remarks>
    /// ⛔ Перелік має бути ПОРОЖНІМ у здоровому стані. Кожен рядок — борг із
    /// причиною й датою, а не спосіб замовчати знахідку.
    ///
    /// • <c>Telemetry:ServiceName</c>, <c>Telemetry:OtlpEndpoint</c> —
    ///   2026-09-19, чекають на `D14-09`: експортера OTLP ще немає (`S-12`),
    ///   а `deploy-ecr.ps1:37,150` уже наводить `OtlpEndpoint` як головний
    ///   приклад для адміністратора. Видалити ключі означало б зламати
    ///   інструкцію розгортання, підключити — завести пакет
    ///   `OpenTelemetry.Extensions.Hosting`, тобто foundation-PR через
    ///   `Directory.Packages.props`. Знімається разом із `D14-09`.
    /// </remarks>
    private static readonly string[] KeysWithoutReaderByDesign =
    [
        "Telemetry:ServiceName",
        "Telemetry:OtlpEndpoint",
    ];

    /// <summary>
    /// Ключі, які код читає, а файл навмисно НЕ оголошує.
    /// </summary>
    /// <remarks>
    /// • <c>Bootstrap:Password</c> і <c>Smtp:Host</c> — секрети й значення
    ///   майданчика. `D-11` і `04-environment.md` §6 прямо забороняють їм
    ///   бути у файлі: вони заводяться лише змінними оточення служби
    ///   (<c>ECR_Smtp__Host</c>). Порожній ключ «щоб було видно» тут гірший за
    ///   відсутній: `DependencyInjection` розрізняє налаштовану пошту від
    ///   ненналаштованої саме порожнечею (`D-124`).
    /// </remarks>
    private static readonly string[] KeysAbsentFromFileByDesign =
    [
        "Bootstrap:Password",
        "Smtp:Host",
    ];

    /// <summary>Зразки, на яких перевіряється, що читач джерел ще живий.</summary>
    /// <remarks>
    /// Узяті дослівно з коду: рядок 45 <c>AuthenticationSetup.cs</c>, рядок 90
    /// <c>DependencyInjection.cs</c> (до виправлення), рядок 46
    /// <c>AuthenticationSetup.cs</c>.
    /// </remarks>
    private static readonly (string Sample, string Key)[] ReadSamples =
    [
        (@"var cookieName = configuration[""Auth:CookieName""] ?? ""ecr.auth"";", "Auth:CookieName"),
        (@"connectionString, ReadInt(configuration, ""Sql:BulkBatchSize"", 5_000)));", "Sql:BulkBatchSize"),
        (@"var requireHttps = configuration.GetValue(""Auth:RequireHttps"", defaultValue: true);", "Auth:RequireHttps"),
        (@"var section = configuration.GetSection(""Jobs"");", "Jobs"),
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait(TestCategories.Check, TestCategories.Static)]
    [Trait("Requirement", "D14-06")]
    public void Кожен_ключ_appsettings_має_читача_в_коді()
    {
        var keys = LeafKeys();

        // ⛔ Самоперевірка ПЕРШОЮ: розбір, що перестав знаходити файл або
        // зламався на вкладеності, дав би порожній перелік — і тест зеленів би
        // саме тоді, коли зламався.
        Assert.True(
            keys.Count >= 10,
            $"У {AppSettingsPath} знайдено лише {keys.Count} листових ключів — "
            + "розбір JSON зламався, а не файл спорожнів.");
        // ⚠ Зразки самоперевірки — про СТРУКТУРУ розбору (плаский ключ і
        // триярусний), а не про конкретну ручку: інакше законне перейменування
        // ключа валило б самоперевірку замість того, щоб дати зрозумілий
        // перелік «без читача».
        Assert.Contains("AllowedHosts", keys);
        Assert.Contains("Logging:LogLevel:Default", keys);

        var sources = SourceText();
        Assert.Contains("\"Auth:CookieName\"", sources);

        var orphans = keys
            .Where(k => !IsHostOwned(k))
            .Where(k => !KeysWithoutReaderByDesign.Contains(k, StringComparer.Ordinal))
            .Where(k => !sources.Contains($"\"{k}\"", StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        // ⛔ `Assert.True` з готовим текстом, а не `Assert.Empty`: xUnit друкує
        // колекцію обрізаною, і зникає першою саме та частина, де сказано, що
        // робити.
        Assert.True(
            orphans.Count == 0,
            $"Ключі {AppSettingsPath} без жодного читача в src/**/*.cs:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, orphans.Select(k => "  " + k))
            + Environment.NewLine
            + "Налаштування, яке ніхто не читає, мовчки не діє — адміністратор "
            + "змінює значення і нічого не стається. Або підключи ключ (рядок "
            + "\"<ключ>\" має зустрітися в коді), або прибери його з файлу, або "
            + "— якщо це свідомий борг — внеси в KeysWithoutReaderByDesign "
            + "разом із причиною й датою.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait(TestCategories.Check, TestCategories.Static)]
    [Trait("Requirement", "D14-06")]
    public void Кожен_ключ_який_читає_код_є_в_appsettings()
    {
        // ⛔ Самоперевірка ПЕРШОЮ, і по кожній формі виклику окремо: регулярка,
        // що перестала бачити свою форму, дала б порожній перелік — тобто
        // зелене рівно тоді, коли перевірка померла.
        foreach (var (sample, expected) in ReadSamples)
        {
            var found = KeysIn(sample).ToList();
            Assert.True(
                found.Contains(expected, StringComparer.Ordinal),
                $"Читач джерел більше не бачить власного зразка: «{sample}» → "
                + $"очікувався ключ «{expected}», знайдено [{string.Join(", ", found)}]. "
                + "Полагодь ConfigurationRead() або онови ReadSamples.");
        }

        var declared = LeafKeys();
        var missing = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var file in SourceTree.Production())
        {
            foreach (var line in CodeKeepingLiterals(file))
            {
                foreach (var key in KeysIn(line))
                {
                    // ⚠ `GetSection("Database")` читає СЕКЦІЮ, а не листок:
                    // такий ключ присутній у файлі тоді, коли з нього
                    // починається хоч один оголошений листовий ключ. Без цієї
                    // гілки перший же перехід на прив'язку секції дав би
                    // хибно-червоне.
                    if (declared.Contains(key)
                        || declared.Any(d => d.StartsWith(key + ":", StringComparison.Ordinal))
                        || KeysAbsentFromFileByDesign.Contains(key, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    if (!missing.TryGetValue(key, out var where))
                    {
                        missing[key] = where = new SortedSet<string>(StringComparer.Ordinal);
                    }

                    where.Add(file.Path);
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "Код читає ключі, яких немає в " + AppSettingsPath + ":"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                missing.Select(p => $"  {p.Key} ← {string.Join(", ", p.Value)}"))
            + Environment.NewLine
            + "Ручка, якої немає у файлі, для адміністратора не існує: про неї "
            + "можна дізнатися лише з вихідних текстів. Або оголоси ключ у "
            + "файлі з поточним дефолтом, або — якщо це секрет чи значення "
            + "майданчика — внеси в KeysAbsentFromFileByDesign із причиною.");
    }

    /// <summary>
    /// Ключі, чий запасний дефолт у коді СВІДОМО інший, ніж значення у файлі.
    /// </summary>
    /// <remarks>
    /// • <c>Notifications:WebhookAllowedHostSuffixes</c> — перелік дозволених
    ///   хостів: без файла код має закриватися (порожній перелік = жодного
    ///   вебхука), а не відкривати доступ до трьох доменів Microsoft.
    /// </remarks>
    private static readonly string[] FallbackDiffersByDesign =
    [
        "Notifications:WebhookAllowedHostSuffixes",
    ];

    /// <summary>Зразки форм «ключ + запасний дефолт», дослівно з коду.</summary>
    private static readonly (string Sample, string Key, string Fallback)[] FallbackSamples =
    [
        (@"var cookieName = configuration[""Auth:CookieName""] ?? ""ecr.auth"";", "Auth:CookieName", "\"ecr.auth\""),
        (@"var slidingHours = configuration.GetValue(""Auth:SlidingHours"", defaultValue: 8);", "Auth:SlidingHours", "8"),
        (@"connectionString, ReadInt(configuration, ""Database:BulkBatchSize"", 50_000)));", "Database:BulkBatchSize", "50_000"),
        (@"Minutes(configuration, ""Cache:MetadataSlidingMinutes"", DefaultMetadataMinutes),", "Cache:MetadataSlidingMinutes", "DefaultMetadataMinutes"),
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait(TestCategories.Check, TestCategories.Static)]
    [Trait("Requirement", "D14-06")]
    public void Запасний_дефолт_у_коді_дорівнює_значенню_у_appsettings()
    {
        foreach (var (sample, key, fallback) in FallbackSamples)
        {
            var found = FallbackRead().Matches(sample).Select(m => (m.Groups["key"].Value, m.Groups["def"].Value.Trim())).ToList();
            Assert.True(
                found.Contains((key, fallback)),
                $"Читач дефолтів не бачить зразка «{sample}» → ({key}, {fallback}); знайдено [{string.Join(", ", found)}].");
        }

        var file = LeafValues();
        var sources = SourceText();
        var constants = Constants(sources);
        var compared = new SortedSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (Match match in FallbackRead().Matches(sources))
        {
            var key = match.Groups["key"].Value;
            if (!file.TryGetValue(key, out var declared)
                || FallbackDiffersByDesign.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            var fallback = Resolve(match.Groups["def"].Value.Trim(), constants);
            if (fallback is null)
            {
                problems.Add($"  {key}: дефолт «{match.Groups["def"].Value.Trim()}» не зводиться до літерала — винеси його в const із літералом.");
                continue;
            }

            compared.Add(key);
            if (!string.Equals(fallback, declared, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"  {key}: у файлі «{declared}», у коді «{fallback}»");
            }
        }

        // ⛔ Самоперевірка охоплення: регулярка, що осліпла, дала б порожнє
        // зелене. 12 — нижня межа наявних читачів з дефолтом на момент заведення.
        Assert.True(compared.Count >= 12, $"Звірено лише {compared.Count} ключів: [{string.Join(", ", compared)}] — читач дефолтів зламався.");
        Assert.Contains("Database:BulkBatchSize", compared);
        Assert.Contains("Auth:CookieName", compared);

        Assert.True(
            problems.Count == 0,
            "Запасний дефолт у коді розходиться з " + AppSettingsPath + ":"
            + Environment.NewLine + string.Join(Environment.NewLine, problems) + Environment.NewLine
            + "Два джерела правди означають, що поведінка без файла (або з ключем, "
            + "стертим адміністратором) тихо інша за задокументовану. Вирівняй "
            + "дефолт під файл, або прибери ключ із файлу, або — свідомо — внеси "
            + "в FallbackDiffersByDesign із причиною.");
    }

    /// <summary>Дефолт як текст: літерал, добуток цілих або ім'я константи.</summary>
    private static string? Resolve(string expression, IReadOnlyDictionary<string, string> constants)
    {
        var text = expression.Replace("_", string.Empty, StringComparison.Ordinal);
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            return text[1..^1];
        }

        if (text is "string.Empty" or "String.Empty")
        {
            return string.Empty;
        }

        var factors = text.Split('*', StringSplitOptions.TrimEntries);
        if (factors.All(f => long.TryParse(f, System.Globalization.CultureInfo.InvariantCulture, out _)))
        {
            return factors.Aggregate(1L, (acc, f) => acc * long.Parse(f, System.Globalization.CultureInfo.InvariantCulture))
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (text is "true" or "false")
        {
            return text;
        }

        var name = expression[(expression.LastIndexOf('.') + 1)..];
        return constants.TryGetValue(name, out var value) && value != expression
            ? Resolve(value, constants)
            : null;
    }

    /// <summary>Константи <c>const T Name = вираз;</c> з усіх джерел, за коротким ім'ям.</summary>
    private static Dictionary<string, string> Constants(string sources)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in ConstDeclaration().Matches(sources))
        {
            result.TryAdd(m.Groups["name"].Value, m.Groups["value"].Value.Trim());
        }

        return result;
    }

    /// <summary>Листові значення <c>appsettings.json</c>: рядок як є, решта — сирим JSON.</summary>
    private static Dictionary<string, string> LeafValues()
    {
        var path = Path.Combine(SourceTree.Root, AppSettingsPath.Replace('/', Path.DirectorySeparatorChar));
        using var document = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(document.RootElement, null);
        return values;

        void Walk(JsonElement element, string? prefix)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                if (prefix is not null)
                {
                    values[prefix] = element.ValueKind == JsonValueKind.String
                        ? element.GetString()!
                        : element.GetRawText();
                }

                return;
            }

            foreach (var property in element.EnumerateObject())
            {
                Walk(property.Value, prefix is null ? property.Name : $"{prefix}:{property.Name}");
            }
        }
    }

    /// <summary>Читання ключа літералом разом із запасним дефолтом.</summary>
    /// <remarks>
    /// Три форми: <c>configuration["K"] ?? X</c>, <c>GetValue("K", [defaultValue:] X)</c>,
    /// <c>Helper(configuration, "K", X)</c> (<c>ReadInt</c>, <c>Minutes</c>).
    /// </remarks>
    [GeneratedRegex(
        @"onfiguration\[\s*""(?<key>[^""]+)""\s*\]\s*\?\?\s*(?<def>""[^""]*""|[\w.]+)"
        + @"|(?<![A-Za-z])GetValue(?:<[^>]+>)?\(\s*""(?<key>[^""]+)""\s*,\s*(?:defaultValue:\s*)?(?<def>[^()]+?)\s*\)"
        + @"|(?<![A-Za-z])[A-Z]\w*\(\s*\w*onfiguration\s*,\s*""(?<key>[^""]+)""\s*,\s*(?<def>[^()]+?)\s*\)")]
    private static partial Regex FallbackRead();

    [GeneratedRegex(@"\bconst\s+(?:int|long|string|bool)\s+(?<name>\w+)\s*=\s*(?<value>[^;]+);")]
    private static partial Regex ConstDeclaration();

    /// <summary>Листові ключі <c>appsettings.json</c>, шляхом через двокрапку.</summary>
    private static HashSet<string> LeafKeys()
    {
        var path = Path.Combine(SourceTree.Root, AppSettingsPath.Replace('/', Path.DirectorySeparatorChar));
        using var document = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        var keys = new HashSet<string>(StringComparer.Ordinal);
        Collect(document.RootElement, prefix: null, keys);
        return keys;
    }

    private static void Collect(JsonElement element, string? prefix, HashSet<string> keys)
    {
        // ⚠ Листом вважається все, що не об'єкт: масив у конфігурації ASP.NET
        // адресується індексами (`X:0`), і жодного такого ключа тут немає —
        // якщо з'явиться, він має бути видимий цілим, а не розібраний на
        // елементи.
        if (element.ValueKind != JsonValueKind.Object)
        {
            if (prefix is not null)
            {
                keys.Add(prefix);
            }

            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            var name = prefix is null ? property.Name : $"{prefix}:{property.Name}";
            Collect(property.Value, name, keys);
        }
    }

    private static bool IsHostOwned(string key)
        => HostOwnedKeys.Contains(key, StringComparer.Ordinal)
           || HostOwnedPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal));

    /// <summary>Усі джерела <c>src/**/*.cs</c> одним текстом, без коментарів.</summary>
    private static string SourceText()
    {
        var builder = new StringBuilder();

        foreach (var file in SourceTree.Production())
        {
            foreach (var line in CodeKeepingLiterals(file))
            {
                builder.Append(line).Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>Ключі конфігурації, прочитані в одному рядку коду.</summary>
    private static IEnumerable<string> KeysIn(string line)
        => ConfigurationRead().Matches(line)
            .Select(m => m.Groups["key"].Value)
            .Where(k => k.Length > 0);

    /// <summary>
    /// Рядки файлу без коментарів, але З рядковими літералами.
    /// </summary>
    /// <remarks>
    /// ⚠ Багаторядковий літерал (<c>"""</c>) пропускається цілком: у цьому
    /// проєкті в ньому лежить SQL, а не ключі конфігурації. Вербатимних
    /// (<c>@"</c>) у <c>src/**/*.cs</c> немає жодного — перевірено
    /// <c>git grep</c> при заведенні сторожа; з'являться — їх зміст так само
    /// не потрапить у пошук, тобто помилка піде в бік хибно-ЧЕРВОНОГО.
    /// </remarks>
    private static IEnumerable<string> CodeKeepingLiterals(SourceFile file)
    {
        var inBlockComment = false;
        var inRawString = false;

        foreach (var raw in file.Text.Split('\n'))
        {
            var builder = new StringBuilder(raw.Length);
            var i = 0;

            while (i < raw.Length)
            {
                if (inRawString)
                {
                    var close = raw.IndexOf("\"\"\"", i, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        break;
                    }

                    inRawString = false;
                    i = close + 3;
                    continue;
                }

                if (inBlockComment)
                {
                    var close = raw.IndexOf("*/", i, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        break;
                    }

                    inBlockComment = false;
                    i = close + 2;
                    continue;
                }

                var c = raw[i];

                if (c == '"' && i + 2 < raw.Length && raw[i + 1] == '"' && raw[i + 2] == '"')
                {
                    inRawString = true;
                    i += 3;
                    continue;
                }

                if (c == '"')
                {
                    // Звичайний літерал: копіюємо як є разом із екранами —
                    // саме його вміст і шукає сторож.
                    builder.Append(c);
                    i++;

                    while (i < raw.Length)
                    {
                        builder.Append(raw[i]);

                        if (raw[i] == '\\' && i + 1 < raw.Length)
                        {
                            builder.Append(raw[i + 1]);
                            i += 2;
                            continue;
                        }

                        if (raw[i] == '"')
                        {
                            i++;
                            break;
                        }

                        i++;
                    }

                    continue;
                }

                if (c == '\'')
                {
                    // Символьний літерал: '"' зсунув би стан лапок.
                    i++;
                    while (i < raw.Length)
                    {
                        if (raw[i] == '\\' && i + 1 < raw.Length)
                        {
                            i += 2;
                            continue;
                        }

                        if (raw[i] == '\'')
                        {
                            i++;
                            break;
                        }

                        i++;
                    }

                    continue;
                }

                if (c == '/' && i + 1 < raw.Length && raw[i + 1] == '/')
                {
                    break;
                }

                if (c == '/' && i + 1 < raw.Length && raw[i + 1] == '*')
                {
                    inBlockComment = true;
                    i += 2;
                    continue;
                }

                builder.Append(c);
                i++;
            }

            yield return builder.ToString();
        }
    }

    /// <summary>
    /// Читання ключа конфігурації рядковим літералом.
    /// </summary>
    /// <remarks>
    /// ⚠ Індексатор (<c>configuration["X"]</c>) ловиться за хвостом
    /// <c>onfiguration[</c> — так одним виразом покриті і локальна змінна
    /// <c>configuration</c>, і властивість <c>app.Configuration</c>.
    ///
    /// ⚠ <c>(?&lt;![A-Za-z])GetValue</c> — щоб не ловити
    /// <c>TryGetValue</c>: словників у цьому коді на два порядки більше, ніж
    /// звернень до конфігурації.
    ///
    /// ⛔ Ключі, що приходять КОНСТАНТОЮ (<c>configuration[EnabledKey]</c>) або
    /// складаються рядком (<c>$"Smtp:{key}"</c>), сюди навмисно не входять:
    /// щоб їх упізнати, довелося б вважати ключем будь-який літерал із
    /// двокрапкою — а це `Sql:CatalogQuery` з `SqlDataSource` (параметр
    /// джерела, не конфігурація) і десяток подібних, тобто хибно-червоне на
    /// рівному місці. Напрям «а» такі ключі однаково перевіряє: сам літерал
    /// у джерелах є.
    /// </remarks>
    [GeneratedRegex(
        @"(?:onfiguration\[\s*""(?<key>[^""]+)""\s*\]"
        + @"|(?<![A-Za-z])Get(?:Value|Section)(?:<[^>]+>)?\(\s*""(?<key>[^""]+)"""
        + @"|(?<![A-Za-z])ReadInt\(\s*\w+\s*,\s*""(?<key>[^""]+)"")")]
    private static partial Regex ConfigurationRead();
}
