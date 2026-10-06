using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Клас SSRF (<c>L3-05</c>, <c>D-241</c>, <c>D-245</c>, <c>D-279</c>): кожне вихідне з'єднання продукту —
/// HTTP, SMTP, SQL, сокет — створюється лише в ЗАРЕЄСТРОВАНОМУ місці, і кожне таке місце або йде
/// через політику адреси, або доведено ходить лише у власну базу ECR.
/// </summary>
/// <remarks>
/// ⛔ L3-05 був рівно таким місцем: <c>new SqlConnection(source.Endpoint)</c> у <c>SqlDataSource</c> без
/// жодної перевірки адреси, тоді як PI Web API і PiSqlClient уже мали свою. Політика існувала — її просто
/// не викликали. Тест на політику (<c>SqlDataSourceAddressPolicyTests</c>) такого не ловить: він перевіряє
/// політику там, де її викликають, і мовчить про місце, де її забули.
/// <para>
/// Тому сторож рахує по тексту джерел (без коментарів і рядків) кожну конструкцію, що відкриває
/// з'єднання назовні, і звіряє з реєстром нижче: нове місце, ще одна конструкція в наявному файлі,
/// зникла перевірка політики або власна база, якій підсунули чужий рядок з'єднання, — червоне.
/// </para>
/// <para>
/// ⚠ Межі, названі прямо: перевіряється <c>src/**</c> (не <c>tools/</c>); те, що рядок <c>connectionString</c>
/// власних сховищ справді прийшов із <c>EcrDbContext</c>, звірено при складанні реєстру, а не
/// перевіряється тут; PiSqlClient перед з'єднанням адресу не перевіряє — так вирішено в <c>D-245</c>
/// («існуючі джерела ретроспективно не перевіряються»), сторож тримає лише перевірку при збереженні.
/// </para>
/// </remarks>
public sealed class OutboundConnectionPolicyTests
{
    /// <summary>Типи, конструктор яких відкриває (або готує) з'єднання назовні.</summary>
    private static readonly string[] EgressTypes =
    [
        "SqlConnection", "OdbcConnection", "OleDbConnection", "SqlBulkCopy",
        "SmtpClient",
        "HttpClient", "HttpClientHandler", "SocketsHttpHandler", "WinHttpHandler",
        "TcpClient", "UdpClient", "Socket", "WebClient",
        "LdapConnection", "DirectoryEntry", "Ping",
    ];

    /// <summary>Вид конструкції → шаблон по рядку коду.</summary>
    private static readonly (string Kind, Regex Pattern)[] Constructs =
    [
        .. EgressTypes.Select(t => ($"new {t}", new Regex($@"\bnew\s+(?:[\w:]+\.)*{t}\b(?![\w.])"))),

        // `SqlConnection c = new(...)` — та сама конструкція без імені типу після `new`.
        ("target-typed new",
            new Regex($@"\b(?:{string.Join('|', EgressTypes)})\??\s+\w+\s*=\s*new\s*\(")),
        ("WebRequest.Create", new Regex(@"\b(?:Http|Ftp)?WebRequest\.Create(?:Http)?\b")),
        ("DbProviderFactories", new Regex(@"\bDbProviderFactories\b")),
        ("AddHttpClient", new Regex(@"\.AddHttpClient\b")),
        ("CreateClient", new Regex(@"\.CreateClient\s*\(")),

        // Фабрики, що повертають готовий клієнт/обробник: виклик деінде — те саме, що `new`.
        ("SmtpNotificationSender.BuildClient", new Regex(@"\bBuildClient\s*\(")),
        ("PiWebApiAuthentication.CreatePrimaryHandler", new Regex(@"\bCreatePrimaryHandler\b")),
        ("TeamsWebhookSender.CreateHandler", new Regex(@"\bCreateHandler\b")),
    ];

    /// <summary>Аргументи <c>new SqlConnection(...)</c>, що означають власну базу ECR.</summary>
    private static readonly Regex OwnDatabaseArgument = new(
        @"\bnew\s+(?:[\w:]+\.)*SqlConnection\s*\(\s*(?:db\.Database\.GetConnectionString\(\)|connectionString)\s*\)");

    private static readonly Site[] Registry =
    [
        // ── Адреса від користувача (Integration.Manage / адмін-налаштування) ──────────────────────
        new(
            "src/Ecr.Adapters.Sql/SqlDataSource.cs",
            "L3-05 / D-279: рядок з'єднання Sql-джерела — політика перед КОЖНИМ з'єднанням",
            Counts(("new SqlConnection", 1)),
            Before(@"\bawait\s+RequireAllowedAddressAsync\s*\(", "new SqlConnection"),
            Has(@"\bDataSourceEndpointPolicy\.CheckSqlClientConnectionString\s*\("),
            Has(@"\bDataSourceEndpointPolicy\.IsLinkLocal\b")),
        new(
            "src/Ecr.Adapters.PiAf/PiSqlClientDataSource.cs",
            "D-245: адреса PiSqlClient перевіряється при збереженні джерела (перед з'єднанням — ні, за рішенням)",
            Counts(("new OdbcConnection", 1)),
            Has(@"\bDataSourceEndpointPolicy\.CheckSqlServerAddress\s*\(",
                "src/Ecr.Application/Integration/DataSourceHandlers.cs")),
        new(
            "src/Ecr.Adapters.PiAf/PiWebApiAuthentication.cs",
            "D-241: кожен обробник PI Web API з'єднується через GuardedSocketConnect (IP перевіряється на сокеті)",
            Counts(("new SocketsHttpHandler", 2), ("PiWebApiAuthentication.CreatePrimaryHandler", 1)),
            Has(@"\bConnectCallback\s*=\s*GuardedSocketConnect\.ConnectAsync\b", min: 2)),
        new(
            "src/Ecr.Adapters.PiAf/GuardedSocketConnect.cs",
            "D-241: сам сокет PI Web API — після перевірки кожної розв'язаної адреси",
            Counts(("new Socket", 1)),
            Before(@"\bDataSourceEndpointPolicy\.IsBlocked\s*\(", "new Socket")),
        new(
            "src/Ecr.Adapters.PiAf/DependencyInjection.cs",
            "D-241: обидва клієнти PI Web API — лише з обробником CreatePrimaryHandler",
            Counts(("AddHttpClient", 2), ("PiWebApiAuthentication.CreatePrimaryHandler", 2)),
            Has(@"\.ConfigurePrimaryHttpMessageHandler\s*\(", min: 2)),
        new(
            "src/Ecr.Adapters.PiAf/PiWebApiDataSource.cs",
            "D-241: лише іменований клієнт Negotiate з тим самим захищеним обробником",
            Counts(("CreateClient", 1)),
            Has(@"\.CreateClient\(PiWebApiAuthentication\.NegotiateClientName\)")),
        new(
            "src/Ecr.Infrastructure/Notifications/TeamsWebhookSender.cs",
            "S9: адреса вебхука — WebhookUrlPolicy перед відправкою, без редиректів",
            Counts(("new SocketsHttpHandler", 1), ("TeamsWebhookSender.CreateHandler", 1), ("CreateClient", 1)),
            Before(@"\bwebhooks\.IsAllowed\s*\(", "CreateClient"),
            Has(@"\bAllowAutoRedirect\s*=\s*false\b")),
        new(
            "src/Ecr.Infrastructure/DependencyInjection.cs",
            "S9: клієнт вебхука — лише з обробником TeamsWebhookSender.CreateHandler",
            Counts(("AddHttpClient", 1), ("TeamsWebhookSender.CreateHandler", 1)),
            Has(@"\.ConfigurePrimaryHttpMessageHandler\(Notifications\.TeamsWebhookSender\.CreateHandler\)")),
        new(
            "src/Ecr.Infrastructure/Integration/SmtpNotificationSender.cs",
            "S2/ent6: хост і порт SMTP — ISmtpEndpointPolicy перед клієнтом; облікових даних служби немає",
            Counts(("new SmtpClient", 1), ("SmtpNotificationSender.BuildClient", 2)),
            Before(@"\b_endpointPolicy\.IsPortAllowed\s*\(", "SmtpNotificationSender.BuildClient"),
            Before(@"\b_endpointPolicy\.IsHostAllowedAsync\s*\(", "SmtpNotificationSender.BuildClient"),
            Has(@"\bUseDefaultCredentials\s*=\s*false\b")),

        // ── Власна база ECR (рядок з'єднання EcrDbContext / конфігурації) ──────────────────────────
        OwnDb("src/Ecr.Infrastructure/Security/SimulationService.cs", 4),
        OwnDb("src/Ecr.Infrastructure/Startup/SchemaValidator.cs", 1),
        OwnDb("src/Ecr.Infrastructure/Startup/SqlCapabilitiesProbe.cs", 1),       // StartupSequence: db.Database
        OwnDb("src/Ecr.Infrastructure/Jobs/SqlDistributedLock.cs", 1),           // QuartzJobAdapter, RegistrySyncJob: db.Database
        OwnDb("src/Ecr.Infrastructure/Localization/UiStringCatalogStore.cs", 10),
        OwnDb("src/Ecr.Infrastructure/Persistence/ConsistencyIssueReader.cs", 2),
        OwnDb("src/Ecr.Infrastructure/Persistence/AuditReader.cs", 8),
        OwnDb("src/Ecr.Infrastructure/Persistence/AuditWriter.cs", 1),
        OwnDb("src/Ecr.Infrastructure/Persistence/RuleCoverageReader.cs", 1),
        OwnDb("src/Ecr.Infrastructure/Integration/IntegrationCellPatcher.cs", 1),
        OwnDb("src/Ecr.Infrastructure/Jobs/SourceEventSyncJob.cs", 2),
        OwnDb("src/Ecr.Infrastructure/Persistence/BulkCellLoader.cs", 2,          // DependencyInjection: ConnectionStrings
            ("new SqlBulkCopy", 1)),
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожне_вихідне_зєднання_створюється_лише_в_зареєстрованому_місці()
    {
        var actual = Census();
        var expected = Registry
            .SelectMany(s => s.Counts.Select(c => (Key: (Path: s.Path, Kind: c.Key), Count: c.Value)))
            .ToDictionary(x => x.Key, x => x.Count);

        var problems = actual.Keys.Union(expected.Keys)
            .OrderBy(k => k.Path, StringComparer.Ordinal).ThenBy(k => k.Kind, StringComparer.Ordinal)
            .Select(k => (Key: k, Actual: actual.GetValueOrDefault(k), Expected: expected.GetValueOrDefault(k)))
            .Where(x => x.Actual != x.Expected)
            .Select(x => x.Expected == 0
                ? $"{x.Key.Path}: «{x.Key.Kind}» ×{x.Actual} — нове вихідне з'єднання поза реєстром. "
                  + "Проведи адресу через політику (DataSourceEndpointPolicy / GuardedSocketConnect / "
                  + "ISmtpEndpointPolicy / WebhookUrlPolicy) і внеси місце в Registry з її маркером."
                : $"{x.Key.Path}: «{x.Key.Kind}» ×{x.Actual}, у реєстрі ×{x.Expected}.")
            .ToList();

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожне_зареєстроване_місце_зберігає_свою_перевірку_політики()
    {
        var problems = new List<string>();

        foreach (var site in Registry)
        {
            foreach (var marker in site.Markers)
            {
                var path = marker.InFile ?? site.Path;
                var lines = Lines(path);
                var hits = lines.Where(l => marker.Pattern.IsMatch(l.Text)).Select(l => l.Line).ToList();

                if (hits.Count < marker.Min)
                {
                    problems.Add($"{site.Path}: у {path} немає «{marker.Pattern}» (знайдено {hits.Count}, "
                                 + $"потрібно ≥ {marker.Min}) — {site.Why}.");
                    continue;
                }

                if (marker.BeforeKind is { } kind)
                {
                    var pattern = Constructs.Single(c => c.Kind == kind).Pattern;
                    var first = lines.Where(l => pattern.IsMatch(l.Text)).Select(l => l.Line).DefaultIfEmpty(0).Min();

                    if (first > 0 && hits.Min() > first)
                    {
                        problems.Add($"{site.Path}:{first}: «{kind}» стоїть РАНІШЕ за перевірку "
                                     + $"«{marker.Pattern}» (рядок {hits.Min()}) — {site.Why}.");
                    }
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Зєднання_з_власною_базою_бере_лише_її_рядок_зєднання()
    {
        // ⛔ Інакше файл «власної бази» з тим самим числом конструкцій приховав би
        // `new SqlConnection(source.Endpoint)` — лічильник не змінився б.
        var newSql = Constructs.Single(c => c.Kind == "new SqlConnection").Pattern;

        var offenders = Registry
            .Where(s => s.OwnDatabase)
            .SelectMany(s =>
            {
                // Аргумент буває на наступному рядку: `new SqlConnection(` ⏎ `db.Database.GetConnectionString());`.
                var lines = Lines(s.Path);
                return lines.Select((l, i) => (s.Path, l.Line, l.Text,
                    Call: i + 1 < lines.Count ? l.Text + " " + lines[i + 1].Text : l.Text));
            })
            .Where(x => newSql.IsMatch(x.Text) && !OwnDatabaseArgument.IsMatch(x.Call))
            .Select(x => $"{x.Path}:{x.Line}  {x.Text.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "SqlConnection у сховищі власної бази з чужим рядком з'єднання (очікується "
            + "db.Database.GetConnectionString() або connectionString власної бази):"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Сторож_бачить_кожен_вид_конструкції()
    {
        // ⚠ Самоперевірка шаблонів: регулярка, що мовчки нічого не ловить, — зелений сторож без
        // сторожа. Кожен рядок нижче мусить дати рівно свій вид.
        (string Line, string Kind)[] samples =
        [
            ("await using var c = new SqlConnection(source.Endpoint);", "new SqlConnection"),
            ("var c = new Microsoft.Data.SqlClient.SqlConnection(x);", "new SqlConnection"),
            ("using var c = new OdbcConnection(s);", "new OdbcConnection"),
            ("var h = new SocketsHttpHandler", "new SocketsHttpHandler"),
            ("var c = new HttpClient();", "new HttpClient"),
            ("var h = new HttpClientHandler { UseDefaultCredentials = true };", "new HttpClientHandler"),
            ("var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);", "new Socket"),
            ("using var t = new TcpClient(host, port);", "new TcpClient"),
            ("var m = new SmtpClient(host, port)", "new SmtpClient"),
            ("SqlConnection c = new(endpoint);", "target-typed new"),
            ("var r = WebRequest.Create(url);", "WebRequest.Create"),
            ("var f = DbProviderFactories.GetFactory(name);", "DbProviderFactories"),
            ("services.AddHttpClient(\"\")", "AddHttpClient"),
            ("var c = factory.CreateClient();", "CreateClient"),
            ("var c = SmtpNotificationSender.BuildClient(h, 25, false, null, () => \"\");",
                "SmtpNotificationSender.BuildClient"),
        ];

        var misses = samples
            .Where(s => !Constructs.Single(c => c.Kind == s.Kind).Pattern.IsMatch(s.Line))
            .Select(s => $"«{s.Kind}» не ловить: {s.Line}")
            .ToList();

        // І навпаки: схожі, але не ті імена не мають рахуватися.
        string[] innocents =
        [
            "var h = new SocketsHttpHandlerOptions();",
            "IHttpClientFactory clients",
            "private SqlConnection connection;",
            "var b = new SqlConnectionStringBuilder(s);",
        ];

        misses.AddRange(innocents
            .Where(l => Constructs.Any(c => c.Pattern.IsMatch(l)))
            .Select(l => $"хибне спрацювання: {l}"));

        Assert.True(misses.Count == 0, string.Join(Environment.NewLine, misses));
    }

    private static Dictionary<(string Path, string Kind), int> Census()
    {
        var census = new Dictionary<(string Path, string Kind), int>();

        foreach (var file in SourceTree.Production())
        {
            foreach (var (_, text) in file.CodeLines())
            {
                foreach (var (kind, pattern) in Constructs)
                {
                    var count = pattern.Count(text);

                    if (count > 0)
                    {
                        census[(file.Path, kind)] = census.GetValueOrDefault((file.Path, kind)) + count;
                    }
                }
            }
        }

        return census;
    }

    private static List<(int Line, string Text)> Lines(string path)
    {
        var full = Path.Combine(SourceTree.Root, path.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"Реєстр сторожа називає файл, якого немає: {path}.");
        return new SourceFile(path, File.ReadAllText(full)).CodeLines().ToList();
    }

    private static Dictionary<string, int> Counts(params (string Kind, int Count)[] counts)
    {
        foreach (var (kind, _) in counts)
        {
            Assert.Contains(Constructs, c => c.Kind == kind);
        }

        return counts.ToDictionary(c => c.Kind, c => c.Count);
    }

    private static Marker Has(string pattern, string? inFile = null, int min = 1)
        => new(new Regex(pattern), inFile, min, BeforeKind: null);

    private static Marker Before(string pattern, string kind) => new(new Regex(pattern), null, 1, kind);

    private static Site OwnDb(string path, int sqlConnections, params (string Kind, int Count)[] more)
        => new(path, "власна база ECR", Counts([("new SqlConnection", sqlConnections), .. more]))
        {
            OwnDatabase = true,
        };

    /// <summary>Місце, де дозволено створювати вихідне з'єднання.</summary>
    /// <param name="Path">Файл.</param>
    /// <param name="Why">Чим захищене (рішення / знахідка).</param>
    /// <param name="Counts">Скільки конструкцій кожного виду в ньому.</param>
    /// <param name="Markers">Перевірки політики, що мусять у ньому лишатися.</param>
    private sealed record Site(string Path, string Why, Dictionary<string, int> Counts, params Marker[] Markers)
    {
        /// <summary>Лише власна база: рядок з'єднання — тільки її.</summary>
        public bool OwnDatabase { get; init; }
    }

    /// <summary>Перевірка політики, що мусить стояти в коді.</summary>
    /// <param name="Pattern">Шаблон рядка коду.</param>
    /// <param name="InFile">Інший файл, де вона стоїть (null — той самий).</param>
    /// <param name="Min">Мінімум входжень.</param>
    /// <param name="BeforeKind">Вид конструкції, РАНІШЕ за першу з якої вона мусить стояти.</param>
    private sealed record Marker(Regex Pattern, string? InFile, int Min, string? BeforeKind);
}
