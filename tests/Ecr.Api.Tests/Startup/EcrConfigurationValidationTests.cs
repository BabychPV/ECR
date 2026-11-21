// tests/Ecr.Api.Tests/Startup/EcrConfigurationValidationTests.cs

using System.Text.Json;
using Ecr.Api.Options;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ecr.Api.Tests.Startup;

/// <summary>
/// Недійсна конфігурація зупиняє старт із назвою ключа (<c>U19</c>) і не
/// замінюється дефолтом мовчки; непорожній OTLP-ендпоінт дає попередження (<c>U17</c>).
/// </summary>
[Collection("SqlServer")]
public sealed class EcrConfigurationValidationTests(SqlServerFixture sql)
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "U19")]
    [InlineData("Database:CommandTimeoutSeconds", "60s")]
    [InlineData("Database:BulkBatchSize", "0")]
    [InlineData("Cache:MetadataSlidingMinutes", "-5")]
    [InlineData("Auth:StampCacheSeconds", "5 сек")]
    [InlineData("Jobs:ShutdownTimeoutSeconds", "0")]
    [InlineData("Auth:RequireHttps", "yes")]
    [InlineData("Jobs:NightlyRecalculation:Enabled", "1")]
    [InlineData("Jobs:NightlyRecalculation:Enabled", "flase")]            // PSS-15: увімкнений за замовчуванням — опечатка не лишає його мовчки ввімкненим
    [InlineData("Jobs:NightlyRecalculation:SkipUnchanged", "ture")]       // PSS-15 (D-562)
    [InlineData("Database:EditionMode", "Enterprse")]
    [InlineData("Database:EditionMode", "2")]
    [InlineData("Schema:StartupMode", "Migarte")]
    [InlineData("Jobs:Queue:Mode", "Databse")]
    [InlineData("Jobs:Recalculation:Executor", "Workers")]
    [InlineData("Security:RateLimit:ChangePasswordPermitPerMinute", "0")]     // U1-09: ключа не було в переліку
    [InlineData("Security:RateLimit:ChangePasswordPermitPerMinute", "5/хв")]
    [InlineData("Auth:DataProtection:AllowUnprotectedKeys", "yes")]
    [InlineData("PiSqlClient:EventQueryTimeoutSeconds", "0")]            // D-464
    [InlineData("PiSqlClient:EventQueryTimeoutSeconds", "5m")]           // D-464
    [InlineData("Collection:MaxEventPages", "0")]                        // D-464
    [InlineData("PiSqlClient:EventQueryChunk", "Mnth")]                  // D-464
    [InlineData("Auth:Negotiate:AllowNtlm", "yes")]                      // PT-4
    [InlineData("PiWebApi:AllowInsecureHttp", "1")]                      // PT-5
    public void Недійсне_значення_називає_ключ(string key, string value)
    {
        var problems = EcrConfigurationValidation.Validate(Config((key, value)));

        var problem = Assert.Single(problems);
        Assert.StartsWith(key + " = «" + value + "»", problem, StringComparison.Ordinal);
        Assert.Contains("ECR_" + key.Replace(":", "__", StringComparison.Ordinal), problem, StringComparison.Ordinal);
    }

    /// <remarks>
    /// ⛔ ФВ-9.8 (D-205): нуль чи від'ємний ліміт перерахунку не «вимикає» межу, а
    /// зупиняє старт. <c>-1</c> у <c>MaxDegreeOfParallelism</c> означав би «без межі»
    /// — рівно те, від чого ліміт існує. Одиниця — найменше допустиме, і вона проходить.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-9.8")]
    [InlineData("Calculations:MaxParallelism", "0")]
    [InlineData("Calculations:MaxParallelism", "-1")]
    [InlineData("Calculations:MaxParallelism", "4 потоки")]
    [InlineData("Calculations:MaxInputCellsPerBinding", "0")]
    [InlineData("Calculations:MaxInputCellsPerBinding", "300k")]
    [InlineData("Calculations:FullYearWarnSeconds", "0")]
    [InlineData("Calculations:FullYearWarnSeconds", "-600")]
    [InlineData("Calculations:FullYearWarnSeconds", "10 хв")]
    [InlineData("Health:RawDataPointWarnRows", "-1")]
    [InlineData("Health:RawDataPointWarnRows", "20 млн")]
    public void Недійсний_ліміт_перерахунку_зупиняє_старт(string key, string value)
    {
        var problem = Assert.Single(EcrConfigurationValidation.Validate(Config((key, value))));
        Assert.StartsWith(key + " = «" + value + "»", problem, StringComparison.Ordinal);

        Assert.Empty(EcrConfigurationValidation.Validate(Config((key, "1"))));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ent6-S4")]
    public void Скалярне_Smtp_AllowedPorts_без_індексу_зупиняє_старт()
    {
        // ⛔ Мутація: прибрати перевірку scalar у Validate → ECR_Smtp__AllowedPorts=2526 мовчки ігнорується.
        var problem = Assert.Single(EcrConfigurationValidation.Validate(Config(("Smtp:AllowedPorts", "2526"))));
        Assert.StartsWith("Smtp:AllowedPorts = «2526»", problem, StringComparison.Ordinal);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ent6-S4")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("smtp")]
    [InlineData("587.5")]
    public void Недійсний_елемент_Smtp_AllowedPorts_називає_ключ_з_індексом(string value)
    {
        // ⛔ Мутація: прибрати цикл по Smtp:AllowedPorts у Validate → нечисловий порт мовчки ігнорується читачем.
        var problem = Assert.Single(EcrConfigurationValidation.Validate(
            Config(("Smtp:AllowedPorts:0", "8025"), ("Smtp:AllowedPorts:1", value))));

        Assert.StartsWith("Smtp:AllowedPorts:1 = «" + value + "»", problem, StringComparison.Ordinal);
        Assert.Empty(EcrConfigurationValidation.Validate(
            Config(("Smtp:AllowedPorts:0", "1"), ("Smtp:AllowedPorts:1", "65535"), ("Smtp:AllowedPorts:2", " "))));
    }
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "U19")]
    public void Файли_налаштувань_самі_проходять_перевірку_а_порожнє_значення_це_не_задано()
    {
        // ⛔ Без цього тесту перевірка, що червоніє на ВСЬОМУ, пройшла б теорію вище —
        // і зупинила б кожну службу на першому ж старті.
        var root = RepositoryRoot();
        var shipped = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root, "src", "Ecr.Api", "appsettings.json"), optional: false)
            .Build();
        var development = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root, "src", "Ecr.Api", "appsettings.json"), optional: false)
            .AddJsonFile(Path.Combine(root, "src", "Ecr.Api", "appsettings.Development.json"), optional: false)
            .Build();

        Assert.Empty(EcrConfigurationValidation.Validate(shipped));
        Assert.Empty(EcrConfigurationValidation.Validate(development));
        Assert.Empty(EcrConfigurationValidation.Validate(Config(("Database:EditionMode", ""), ("Auth:SlidingHours", "  "))));
        Assert.Empty(EcrConfigurationValidation.Validate(Config(("Database:EditionMode", "enterprise"), ("Schema:StartupMode", "migrate"))));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "U19")]
    public void Кожен_числовий_і_булевий_ключ_appsettings_перевіряється()
    {
        // ⚠ Перелік у перевірці — ручний; цей тест не дає йому відстати від файлу:
        // новий числовий ключ без правила означав би рівно ту тиху підміну дефолтом,
        // яку перевірка прибрала.
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Ecr.Api", "appsettings.json")));

        var numbers = new List<string>();
        var booleans = new List<string>();
        Walk(document.RootElement, prefix: null);

        var checkedIntegers = EcrConfigurationValidation.Integers.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var checkedBooleans = EcrConfigurationValidation.Booleans.ToHashSet(StringComparer.Ordinal);

        Assert.True(numbers.Count >= 15, $"Розбір файлу зламався: числових ключів лише {numbers.Count}.");
        var withoutRule = numbers.Where(k => !checkedIntegers.Contains(k))
            .Concat(booleans.Where(k => !checkedBooleans.Contains(k)))
            .ToList();
        Assert.True(
            withoutRule.Count == 0,
            "Ключі appsettings.json без правила в EcrConfigurationValidation: " + string.Join(", ", withoutRule));

        void Walk(JsonElement element, string? prefix)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        Walk(property.Value, prefix is null ? property.Name : $"{prefix}:{property.Name}");
                    }

                    break;
                case JsonValueKind.Number:
                    numbers.Add(prefix!);
                    break;
                case JsonValueKind.True or JsonValueKind.False:
                    booleans.Add(prefix!);
                    break;
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "U17")]
    public void OtlpEndpoint_при_вимкненому_експорті_попереджає_що_ігнорується()
    {
        Assert.Empty(EcrConfigurationValidation.Warnings(Config(("Telemetry:OtlpEndpoint", ""))));

        var warning = Assert.Single(EcrConfigurationValidation.Warnings(
            Config(("Telemetry:OtlpEndpoint", "http://collector:4317"))));
        Assert.Contains("Telemetry:OtlpEndpoint", warning, StringComparison.Ordinal);
        Assert.Contains("Telemetry:Enabled", warning, StringComparison.Ordinal);
        Assert.Contains("ігнорується", warning, StringComparison.Ordinal);

        // Увімкнено й адреса є — попереджати нема про що.
        Assert.Empty(EcrConfigurationValidation.Warnings(
            Config(("Telemetry:Enabled", "true"), ("Telemetry:OtlpEndpoint", "http://collector:4317"))));
    }

    /// <summary>
    /// PERF-12: Quartz у Production — попередження старту (явно чи за типовим значенням); Database і не-Production — без нього.
    /// Мутації (прогнано): прибрати <c>isProduction</c> з умови → «Development» дає попередження; прибрати блок → Production
    /// без попередження; змінити типовий режим на Database в <c>ReadMode</c> → кейс «не задано» без попередження.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "PERF-12")]
    [InlineData(true, "Quartz", true)]
    [InlineData(true, null, true)]
    [InlineData(true, "Database", false)]
    [InlineData(false, "Quartz", false)]
    [InlineData(false, null, false)]
    public void Quartz_у_Production_попереджає_на_старті(bool production, string? mode, bool expectWarning)
    {
        var warnings = EcrConfigurationValidation.Warnings(Config(("Jobs:Queue:Mode", mode ?? "")), production)
            .Where(w => w.Contains("PERF-12", StringComparison.Ordinal))
            .ToList();

        if (expectWarning)
        {
            Assert.Equal(EcrConfigurationValidation.QuartzInProductionWarning, Assert.Single(warnings));
        }
        else
        {
            Assert.Empty(warnings);
        }
    }

    /// <summary>
    /// PERF-12: Database + Worker без служби (немає / вимкнена) — попередження старту; зі службою чи в іншому режимі — ні.
    /// Мутація (прогнано): прибрати <c>Disabled</c> зі списку станів → кейс «Disabled» без попередження.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "PERF-12")]
    [InlineData("Database", "Worker", Ecr.Api.Health.WorkerServiceState.Missing, true)]
    [InlineData("Database", "Worker", Ecr.Api.Health.WorkerServiceState.Disabled, true)]
    [InlineData("Database", "Worker", Ecr.Api.Health.WorkerServiceState.Installed, false)]
    [InlineData("Database", "Worker", Ecr.Api.Health.WorkerServiceState.Unknown, false)]
    [InlineData("Quartz", "InProcess", Ecr.Api.Health.WorkerServiceState.Missing, false)]
    [InlineData("Database", "InProcess", Ecr.Api.Health.WorkerServiceState.Missing, false)]
    public void Воркер_без_служби_попереджає_на_старті(
        string mode, string executor, Ecr.Api.Health.WorkerServiceState service, bool expectWarning)
    {
        var warning = EcrConfigurationValidation.WorkerServiceWarning(
            Config(("Jobs:Queue:Mode", mode), ("Jobs:Recalculation:Executor", executor)), service);

        Assert.Equal(expectWarning, warning is not null);
    }

    /// <remarks>
    /// ✎ L1b / D2 (стенд Land 21.11; рішення координатора 21.11): межа дітей розкладу перерахунку проти місць виконавця, що
    /// бере лейн перерахунку. ПОПЕРЕДЖЕННЯ — лише коли резерву останнього місця немає (Quartz — потоки пулу) і межа ≥ місць.
    /// Черга в базі з InProcess резервує останнє місце сам (JobWorker), тож типова конфігурація (межа 4 = 4 місця) — без
    /// попередження, лише рядок Information з фактичною кількістю дітей. Executor = Worker — пул попереджає сам (Ecr.Worker).
    /// Мутації: повернути попередження для зарезервованого випадку — червоні рядки «InProcess 4/6»; прибрати попередження
    /// Quartz — червоний рядок «Quartz 4 з 4».
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "L1b-D2")]
    [InlineData("Database", "InProcess", "4", null, null, "не більше 3")]
    [InlineData("Database", "InProcess", "6", null, null, "не більше 3")]
    [InlineData("Database", "InProcess", "3", null, null, null)]
    [InlineData("Database", "Worker", "40", null, null, null)]
    [InlineData("Quartz", "InProcess", "4", "4", "Знизьте межу до 3", null)]
    [InlineData("Quartz", "InProcess", "4", null, null, null)]
    public void Межа_дітей_розкладу_проти_місць_виконавця_попереджає_лише_без_резерву(
        string mode, string executor, string cap, string? threads, string? expectedWarning, string? expectedNote)
    {
        var config = Config(
            ("Jobs:Queue:Mode", mode),
            ("Jobs:Recalculation:Executor", executor),
            ("Jobs:Recalculation:FanOutMaxConcurrency", cap),
            ("Jobs:Quartz:ThreadCount", threads ?? ""));

        static List<string> FanOut(IEnumerable<string> lines)
            => [.. lines.Where(w => w.Contains("Jobs:Recalculation:FanOutMaxConcurrency", StringComparison.Ordinal))];

        var warnings = FanOut(EcrConfigurationValidation.Warnings(config));
        var notes = FanOut(EcrConfigurationValidation.Notes(config));
        if (expectedWarning is null)
        {
            Assert.Empty(warnings);
        }
        else
        {
            Assert.Contains(expectedWarning, Assert.Single(warnings), StringComparison.Ordinal);
        }

        if (expectedNote is null)
        {
            Assert.Empty(notes);
        }
        else
        {
            Assert.Contains(expectedNote, Assert.Single(notes), StringComparison.Ordinal);
        }

        Assert.Empty(EcrConfigurationValidation.Validate(config));
    }

    /// <remarks>
    /// PSS-28: у Production порожній або `*` в `AllowedHosts` — попередження (DNS rebinding на анонімні ендпоінти);
    /// задані імена, чи не Production — мовчання. Старт не зупиняється. Мутація: прибрати умову `isProduction` —
    /// червоніє перший рядок; не розпізнавати `*` серед кількох імен — червоніє `a;*`.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "PSS-28")]
    [InlineData("*", true)]
    [InlineData("", true)]
    [InlineData("a.example;*", true)]
    [InlineData("ecr.example.kz;localhost", false)]
    public void AllowedHosts_зірочка_чи_порожньо_у_Production_попереджає(string hosts, bool warns)
    {
        var config = Config(("AllowedHosts", hosts));

        var production = EcrConfigurationValidation.Warnings(config, isProduction: true);
        Assert.Equal(warns, production.Any(w => w.Contains("AllowedHosts", StringComparison.Ordinal)));
        if (warns)
        {
            var text = production.Single(w => w.Contains("AllowedHosts", StringComparison.Ordinal));
            Assert.Contains("-AllowedHosts", text, StringComparison.Ordinal);
            Assert.Contains("ECR_AllowedHosts", text, StringComparison.Ordinal);
        }

        // Не Production (Development, стенди, тести) — без попередження.
        Assert.DoesNotContain(
            EcrConfigurationValidation.Warnings(config, isProduction: false),
            w => w.Contains("AllowedHosts", StringComparison.Ordinal));

        // Старт не зупиняється: це не помилка конфігурації.
        Assert.Empty(EcrConfigurationValidation.Validate(config));
    }

    /// <remarks>
    /// ⛔ H1 (рев'ю PSS-28): HostFiltering порівнює записи <c>AllowedHosts</c> БЕЗ порту, тож запис із портом замикав би
    /// всіх, крім localhost, а розгортання виглядало б зеленим. Старт із таким значенням (чи з недійсним форматом) —
    /// зупиняється з назвою ключа. Мутація: знову приймати порт у <c>AllowedHostsProblem</c> — червоніють рядки з портом.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "PSS-28")]
    [InlineData("*", false)]
    [InlineData("ecr.example.kz", false)]
    [InlineData("ecr.example.kz;ecr;localhost", false)]
    [InlineData("*.example.kz;localhost", false)]
    [InlineData("[::1];localhost", false)]
    [InlineData("ecr.example.kz:8443", true)]
    [InlineData("ecr.example.kz;localhost:5000", true)]
    [InlineData("[::1]:8443", true)]
    [InlineData("https://ecr.example.kz", true)]
    [InlineData("ecr.example.kz/app", true)]
    [InlineData("ec r", true)]
    public void AllowedHosts_із_портом_чи_недійсним_форматом_зупиняє_старт(string hosts, bool rejected)
    {
        var problems = EcrConfigurationValidation.Validate(Config(("AllowedHosts", hosts)));

        Assert.Equal(rejected, problems.Any(p => p.Contains("AllowedHosts", StringComparison.Ordinal)));
        if (rejected)
        {
            Assert.Contains("ECR_AllowedHosts", Assert.Single(problems), StringComparison.Ordinal);
        }
    }

    /// <remarks>
    /// ⛔ U17: увімкнений експорт без адреси не має тихо слати на localhost:4317
    /// (дефолт бібліотеки) — старт зупиняється з поясненням, що задати.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "U17")]
    [InlineData("")]
    [InlineData("   ")]
    public void Увімкнений_експорт_без_адреси_зупиняє_старт(string endpoint)
    {
        var problem = Assert.Single(EcrConfigurationValidation.Validate(
            Config(("Telemetry:Enabled", "true"), ("Telemetry:OtlpEndpoint", endpoint))));

        Assert.StartsWith("Telemetry:OtlpEndpoint = «»", problem, StringComparison.Ordinal);
        Assert.Contains("обов'язковий", problem, StringComparison.Ordinal);
        Assert.Contains("Telemetry:Enabled = true", problem, StringComparison.Ordinal);
        Assert.Contains("ECR_Telemetry__OtlpEndpoint", problem, StringComparison.Ordinal);

        // Вимкнено — адреса не потрібна.
        Assert.Empty(EcrConfigurationValidation.Validate(
            Config(("Telemetry:Enabled", "false"), ("Telemetry:OtlpEndpoint", endpoint))));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [InlineData("Documents:StaleMapWarm:RingIntervalSeconds", "59")]
    [InlineData("Documents:StaleMapWarm:RingIntervalSeconds", "10m")]
    [InlineData("Documents:StaleMapWarm:HotPeriodLimit", "0")]
    [InlineData("Documents:StaleMapWarm:HotPeriodLimit", "two")]
    [InlineData("Startup:WarmEditPath", "yes")]                 // AN-190
    [InlineData("Startup:WarmEditPathMaxVersions", "0")]        // AN-190
    [InlineData("Startup:WarmEditPathMaxVersions", "20 версій")] // AN-190
    public void Недійсний_ключ_прогріву_карти_називає_ключ(string key, string value)
    {
        var problem = Assert.Single(EcrConfigurationValidation.Validate(Config((key, value))));
        Assert.StartsWith(key + " = «" + value + "»", problem, StringComparison.Ordinal);
    }
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "U17")]
    [InlineData("Telemetry:Enabled", "yes")]
    [InlineData("Telemetry:ExportIntervalSeconds", "4")]
    [InlineData("Telemetry:ExportIntervalSeconds", "1m")]
    [InlineData("Telemetry:OtlpProtocol", "Http")]
    [InlineData("Telemetry:OtlpProtocol", "1")]
    public void Недійсний_ключ_телеметрії_називає_ключ(string key, string value)
    {
        var problem = Assert.Single(EcrConfigurationValidation.Validate(Config((key, value))));
        Assert.StartsWith(key + " = «" + value + "»", problem, StringComparison.Ordinal);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "U17")]
    [InlineData("collector:4317")]
    [InlineData("ftp://collector:4317")]
    public void Увімкнений_експорт_з_недійсною_адресою_зупиняє_старт(string endpoint)
    {
        var problem = Assert.Single(EcrConfigurationValidation.Validate(
            Config(("Telemetry:Enabled", "true"), ("Telemetry:OtlpEndpoint", endpoint))));
        Assert.StartsWith("Telemetry:OtlpEndpoint = «" + endpoint + "»", problem, StringComparison.Ordinal);

        Assert.Empty(EcrConfigurationValidation.Validate(
            Config(("Telemetry:Enabled", "true"), ("Telemetry:OtlpEndpoint", "https://collector:4317"), ("Telemetry:ExportIntervalSeconds", "5"))));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "U19")]
    public void Застосунок_з_недійсним_ключем_не_стартує_і_пише_причину_в_журнал()
    {
        // ⛔ Саме справжній Program.cs: юніт-тести вище доводять правила, а цей —
        // що перевірку взагалі викликають на старті.
        var app = new EcrApiFactory(sql);
        var broken = app.WithWebHostBuilder(b => b.UseSetting("Database:EditionMode", "Enterprse"));
        Exception? failure;
        try
        {
            failure = Record.Exception(() => broken.CreateClient().Dispose());
        }
        finally
        {
            // Хост, що не стартував, може кинути й на звільненні — не предмет тесту.
            _ = Record.Exception(broken.Dispose);
            _ = Record.Exception(app.Dispose);
        }

        Assert.True(failure is not null, "Застосунок стартував із недійсним Database:EditionMode.");
        var message = Flatten(failure!);
        Assert.Contains("Database:EditionMode = «Enterprse»", message, StringComparison.Ordinal);
        Assert.Contains(app.ServerErrors, l => l.Contains("Database:EditionMode = «Enterprse»", StringComparison.Ordinal));

        // ⚠ Зупинка — ДО бази: інакше «fail-fast» означав би хвилину спроб з'єднання.
        Assert.DoesNotContain(app.ServerLog, l => l.Contains("Старт: база доступна", StringComparison.Ordinal));
    }

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static string Flatten(Exception root)
    {
        var parts = new List<string>();
        var stack = new Stack<Exception>([root]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            parts.Add(current.Message);

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    stack.Push(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                stack.Push(inner);
            }
        }

        return string.Join(Environment.NewLine, parts);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException($"Ecr.sln не знайдено від {AppContext.BaseDirectory}.");
    }
}
