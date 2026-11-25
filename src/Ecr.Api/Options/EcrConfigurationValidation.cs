// src/Ecr.Api/Options/EcrConfigurationValidation.cs

using System.Globalization;
using Ecr.Domain.Enums;

namespace Ecr.Api.Options;

/// <summary>
/// Перевірка конфігурації на старті (<c>U19</c>): недійсне значення зупиняє
/// службу з ім'ям ключа, а не мовчки підміняється дефолтом.
/// </summary>
/// <remarks>
/// ⛔ Предмет. Частина ключів читається так, що друкарська помилка НЕ видна
/// ніде: <c>ReadInt</c> (<c>Database:*</c>, <c>Campaign:AtRiskDays</c>,
/// <c>Integration:CatalogTimeoutSeconds</c>) і <c>Cache:*SlidingMinutes</c>
/// повертають дефолт на <c>"60s"</c>; <c>Auth:StampCacheSeconds</c> — так само;
/// <c>Database:EditionMode = "Enterprse"</c> давав <c>Auto</c>, хоча в проді режим
/// мусить бути заданий явно; будь-що, крім <c>Migrate</c>, у
/// <c>Schema:StartupMode</c> означало <c>Validate</c>. Адміністратор змінював
/// значення — і нічого не відбувалося, без жодного рядка в журналі.
///
/// ⚠ Чому один перелік тут, а не <c>ValidateOnStart()</c> біля кожного читача.
/// Читачі розкидані по чотирьох збірках і більшість із них — не Options, а
/// точкові <c>configuration["…"]</c> на етапі реєстрації. Перетворити їх усі на
/// Options — це правка <c>Ecr.Infrastructure</c> заради форми, а не поведінки.
/// Перелік же звіряється з <c>appsettings.json</c> тестом
/// (<c>EcrConfigurationValidationTests</c>): числовий чи булевий ключ файлу поза
/// переліком червонить збірку.
///
/// ⚠ Порожнє значення — «не задано», а не помилка: так його й трактують читачі
/// (дефолт коду), і так його пише заповнювач інсталятора.
///
/// ⚠ Ключі, які читає <c>GetValue&lt;int&gt;</c> ще на етапі реєстрації
/// (<c>Auth:SlidingHours</c>, <c>Security:RateLimit:*</c>), на недійсному значенні
/// падають раніше за цю перевірку — з повідомленням біндера, де ключ теж названо.
/// У переліку вони лишаються: для них тут перевіряється ще й межа (нуль).
/// </remarks>
public static partial class EcrConfigurationValidation
{
    private const string RawDataPointHealthKey = Ecr.Api.Health.RawDataPointHealth.ThresholdConfigKey;

    /// <summary>Цілі ключі та найменше допустиме значення.</summary>
    public static readonly IReadOnlyList<(string Key, int Min)> Integers =
    [
        ("Database:CommandTimeoutSeconds", 0),
        ("Database:RecalcCommandTimeoutSeconds", 30),
        ("Database:BulkBatchSize", 1),
        ("Database:SheetLockTimeoutSeconds", 0),
        ("Database:MaxPoolSize", 1),
        ("Cache:MetadataSlidingMinutes", 1),
        ("Cache:AccessProfileSlidingMinutes", 1),
        ("Auth:SlidingHours", 1),
        ("Auth:StampCacheSeconds", 0),

        // D-471: нижня межа виходу після бездіяльності; 0 вимкнув би межу, понад 5 — політика обмежує сама.
        (Infrastructure.Security.IdleTimeoutPolicy.MinimumMinutesKey, 1),
        (Startup.HttpsTransport.PortKey, 0),
        ("Security:RateLimit:LoginPermitPerMinute", 1),
        (Ecr.Api.Security.LoginRateLimiting.WindowsLoginPermitKey, 1), // PERF-07

        // U1-09: читається `GetValue<int>` на старті, але в переліку його не було — нуль дав би межу «жодного запиту».
        ("Security:RateLimit:ChangePasswordPermitPerMinute", 1),
        ("Security:RateLimit:SearchPermit", 1),
        ("Security:RateLimit:SearchWindowSeconds", 1),
        ("Security:RateLimit:CspReportPermitPerMinute", 1),
        ("Security:RateLimit:SmtpTestPermitPerMinute", 1),
        ("Security:RateLimit:SmtpTestSystemPermitPerHour", 1),
        ("Security:RateLimit:RecalculatePermitPerMinute", 1),
        ("Security:RateLimit:RecalculateUserPermitPerMinute", 1),
        ("Security:RateLimit:VerifyPermitPerMinute", 1),
        ("Security:RateLimit:ValidatePermitPerMinute", 1),
        (Ecr.Api.Security.TableDiscussionPostRateLimitPolicy.PermitKey, 1),
        (Ecr.Api.Security.DocumentExportRateLimitPolicy.PermitKey, 1),
        ("Security:RateLimit:HealthReadyPermitPerMinute", 1),
        (Ecr.Api.Security.LoginRateLimiting.AnonymousReadPermitKey, 1), // PSS-29
        (Ecr.Infrastructure.Security.LoginAttemptRetention.IntervalMinutesKey, 1), // PSS-24
        (Ecr.Api.Security.ImportPreviewRateLimitPolicy.PermitKey, 1),
        (Ecr.Api.Security.ImportPreviewRateLimitPolicy.QueueLimitKey, 0),
        ("Jobs:ShutdownTimeoutSeconds", 1),

        // D-430: частіше за 10 с повний запит карти лише навантажує SQL.
        (Ecr.Api.Startup.StaleMapWarmService.IntervalKey, Ecr.Api.Startup.StaleMapWarmService.MinIntervalSeconds),
        (Ecr.Api.Startup.StaleMapWarmService.RingIntervalKey, Ecr.Api.Startup.StaleMapWarmService.MinRingIntervalSeconds),
        (Ecr.Api.Startup.StaleMapWarmService.HotPeriodLimitKey, 1),
        (Infrastructure.Persistence.StaleMapQueryHintInterceptor.MaxDopKey, 0), // Верхня межа паралелізму запитів карти застарілості (OPTION MAXDOP): діє min(значення, MAXDOP бази чи сервера), вище за налаштування сервера НІКОЛИ не піднімає; 0 — без підказки. Стенду з малою пам'яттю SQL Server — 2.

        // AN-190: нуль версій — не «вимкнути прогрів» (для цього Startup:WarmEditPath), а прогрів, що нічого не гріє.
        (Infrastructure.Jobs.EditPathWarmer.MaxVersionsKey, 1),
        ("Jobs:QueueDepth:RefreshSeconds", 5),

        // AN-116: нуль задач Excel одночасно — не «без межі», а книги, яких не бере ніхто.
        (Infrastructure.Jobs.JobLaneMap.ExcelMaxConcurrencyKey, 1),
        (Infrastructure.Jobs.QuartzJobTypeLimiter.ThreadCountKey, 1),

        // F1 (WW-02): нуль дочірніх задач розкладу перерахунку — не «без межі», а документи, яких не бере ніхто.
        (Infrastructure.Jobs.QuartzJobTypeLimiter.RecalcFanOutMaxConcurrencyKey, 1),

        // ✎ PERF-05 (D-564c): нуль — не «без стелі», а перерахунки, яких не бере ніхто.
        (Infrastructure.Jobs.QuartzJobTypeLimiter.RecalcMaxConcurrentKey, 1),

        // ✎ PERF-04 (D-564d): місця воркера Api — від одного.
        (Infrastructure.Jobs.JobWorkerOptions.ApiMaxConcurrencyKey, 1),
        (Infrastructure.Jobs.JobWorkerOptions.InteractiveReservedConcurrencyKey, 1),

        // AN-13 D4: нуль хвилин — не «без стелі», а Failed на першому ж повторному відкладенні.
        (Infrastructure.Jobs.JobDeferral.MaxDeferralMinutesKey, 1),
        ("Audit:ExportMaxRows", 1),
        ("Localization:ImportMaxBytes", 1),
        ("Registries:ImportMaxBytes", 1),
        ("Registries:ExportMaxRows", 1),
        ("Campaign:AtRiskDays", 0),
        ("Integration:CatalogTimeoutSeconds", 1),
        ("Integration:RawDataExportMaxRows", 1), // D-463: нуль — кожен експорт «Даних з PI» відмовою, а не «без стелі».

        // D-462: 0 — без нарізки (інтервал цілком); від'ємне — помилка. Тайм-аут команди ODBC — від секунди.
        (Ecr.Adapters.PiAf.CollectionRunnerOptions.ChunkDaysKey, 0),
        (Ecr.Adapters.PiAf.PiSqlClientOptions.CommandTimeoutSecondsKey, 1),

        // D-464: тайм-аут читання подій — від секунди; стеля сторінок подій — від однієї (нуль = нічого не читати).
        (Ecr.Adapters.PiAf.PiSqlClientOptions.EventQueryTimeoutSecondsKey, 1),
        (Ecr.Infrastructure.Jobs.SourceEventSyncOptions.MaxEventPagesKey, 1),
        ("Logging:File:RetainedFiles", 1),
        ("Logging:File:FileSizeLimitMb", 1),

        // ФВ-9.8 (D-205): нуль чи від'ємне — не «без ліміту»; -1 у паралелізмі
        // зняв би межу зовсім.
        ("Calculations:MaxParallelism", 1),
        ("Calculations:MaxInputCellsPerBinding", 1),

        // ── PSS-21 (лейн L7 pss-script-security): бюджет рядка скрипта й межі прогонів/перевірок скрипта ──
        ("Calculations:MaxRowEvaluationSteps", 20000), // Менше за стелю одного оператора (20 000) обрізало б навіть один законний оператор.
        (Ecr.Api.Security.ScriptAuthoringRateLimitPolicy.PermitKey, 1),
        (Ecr.Api.Security.ScriptCheckRateLimitPolicy.PermitKey, 1),
        (Ecr.Api.Security.ScriptRunGate.ConcurrencyKey, 1),
        (Ecr.Api.Security.ScriptRunGate.QueueLimitKey, 0),
        (Ecr.Api.Security.ScriptRunGate.QueueWaitSecondsKey, 1),
        // ── кінець PSS-21 ──

        // ПРД-13: поріг сигналу «перерахунок довший за бюджет»; нуль зробив би сигналом кожну задачу.
        (Ecr.Infrastructure.Jobs.RecalculationBudgetOptions.WarnSecondsKey, 1),

        // AN-134: прибирання зрізів подання. Менше двох зрізів — не «економія», а втрата попереднього стану
        // для порівняння; нуль у розмірі пакета/стелі — прохід, що нічого не робить.
        (Ecr.Infrastructure.Jobs.SnapshotRetentionOptions.RetainSnapshotsKey, Ecr.Infrastructure.Jobs.SnapshotRetentionOptions.MinRetainSnapshots),
        (Ecr.Infrastructure.Jobs.SnapshotRetentionOptions.BatchSizeKey, 1),
        (Ecr.Infrastructure.Jobs.SnapshotRetentionOptions.PauseMillisecondsKey, 0),
        (Ecr.Infrastructure.Jobs.SnapshotRetentionOptions.MaxBatchesPerRunKey, 1),

        // Q-17: прибирання результатів неактуальних прогонів. Нуль у розмірі пакета/стелі - прохід, що нічого не робить;
        // поріг журналу поза 1-100 % - або завжди пауза, або ніколи.
        (Ecr.Infrastructure.Persistence.SupersededPurgeOptions.BatchSizeKey, 1),
        (Ecr.Infrastructure.Persistence.SupersededPurgeOptions.PauseMillisecondsKey, 0),
        (Ecr.Infrastructure.Persistence.SupersededPurgeOptions.MaxBatchesPerRunKey, 1),
        (Ecr.Infrastructure.Persistence.SupersededPurgeOptions.LogPauseThresholdPercentKey, 1),
        (Ecr.Infrastructure.Persistence.SupersededPurgeOptions.LogPauseSecondsKey, 0),
        (Ecr.Infrastructure.Persistence.SupersededPurgeOptions.MaxLogPausesKey, 0),
        (Ecr.Infrastructure.Persistence.SupersededPurgeOptions.CheckpointEveryBatchesKey, 1),

        // R2: поріг рядків ext.RawDataPoint у картці db; 0 вимикає перевірку, від'ємне — помилка.
        (RawDataPointHealthKey, 0),

        // D-476: частка кейсів FLERT, яку один синк може вимкнути; від'ємне — помилка (верхня межа 100 — нижче).
        (Ecr.Infrastructure.Jobs.FlertCompositionSyncOptions.MaxDeactivatePercentKey, 0),

        // U17: частіше за 5 с експорт лише навантажує сервер і колектор.
        (Ecr.Api.Observability.TelemetrySetup.ExportIntervalKey, Ecr.Api.Observability.TelemetrySetup.MinExportIntervalSeconds),
    ];

    /// <summary>Булеві ключі: лише <c>true</c> або <c>false</c>.</summary>
    public static readonly IReadOnlyList<string> Booleans =
    [
        "Auth:RequireHttps",
        "Auth:EnableNegotiate",

        // PSS-24: вимикач прибирання `sec.LoginAttempt`; опечатка не має мовчки лишати прибирання ввімкненим.
        Ecr.Infrastructure.Security.LoginAttemptRetention.EnabledKey,

        // PT-4 (пентест 2026-11): NTLM у Negotiate лише за явним true (типово false).
        Ecr.Api.Auth.AuthenticationSetup.AllowNtlmKey,

        // PT-5: секрет у заголовку джерела PI Web API по http:// лише за явним true (типово false).
        Ecr.Infrastructure.Integration.EndpointNetwork.AllowInsecureHttpKey,

        // U1-09: читається `GetValue<bool>`; опечатка («ye») інакше падала б із повідомленням біндера, а не
        // з переліком недійсних ключів разом з рештою.
        Ecr.Api.Auth.AuthenticationSetup.AllowUnprotectedKeysKey,
        "Security:RateLimit:TrustForwardedFor",
        "Security:Csp:ReportOnly",
        "Security:Csp:Enforce",
        "Health:WarmupOnStart",
        Infrastructure.Jobs.EditPathWarmer.EnabledKey,
        Infrastructure.Jobs.FirstEditWarmer.EnabledKey,

        // Q-17: читається `GetValue<bool>` у DependencyInjection (Infrastructure).
        "Collection:MonthColumnRouting",

        // P-1124-1: опечатка («ture») мовчки лишила б формульний авторинг вимкненим на стенді збирання — краще стоп.
        Ecr.Application.Calculations.FormulaEngineAuthoring.EnabledKey,

        // ✎ B179 L2, P-0118-31/33: ключів HSE400 «перерахунок залежних Land» і «склад із документа» більше немає —
        // вирішує лише біт зв'язки. Залишкова змінна середовища не перевіряється й не читається (старт не падає).

        // D-462: видалення сирих точок, яких PI більше не віддає (повністю перечитаний шматок вікна).
        Ecr.Adapters.PiAf.CollectionRunnerOptions.DeleteMissingPointsKey,

        // ✎ Аудит 31.10 (G5): видаляти зниклі точки й за відповіддю запасної адреси (типово false).
        Ecr.Adapters.PiAf.CollectionRunnerOptions.DeleteMissingOnSecondaryKey,

        // RPT-STABLE Part B: перенос версії «з місяця» (типово вимкнено).
        Ecr.Application.Documents.VersionMigration.VersionMigrationOptions.FromMonthEnabledKey,
        Ecr.Api.Startup.StaleMapWarmService.EnabledKey,

        // ✎ PSS-15 (D-562): нічний перерахунок увімкнений за замовчуванням; «flase» інакше мовчки лишав би його ввімкненим
        // (читач вимикає лише явне false), а «ture» у пропуску незмінних — так само ввімкненим.
        Ecr.Api.Startup.NightlyRecalculationScheduling.EnabledKey,
        Ecr.Api.Startup.NightlyRecalculationScheduling.SkipUnchangedKey,
        "Logging:File:Json",
        Ecr.Api.Observability.TelemetrySetup.EnabledKey,
        Ecr.Infrastructure.Jobs.SnapshotRetentionOptions.EnabledKey,
        Ecr.Infrastructure.Jobs.SnapshotRetentionOptions.DeleteEnabledKey,
        Ecr.Infrastructure.Persistence.SupersededPurgeOptions.EnabledKey,
        Ecr.Infrastructure.Persistence.SupersededPurgeOptions.DeleteEnabledKey,
        Ecr.Infrastructure.Persistence.SupersededPurgeOptions.IncludeTraceKey,
        Ecr.Infrastructure.Persistence.SupersededPurgeOptions.RebuildAfterKey,

        // D-476: синк складу газу FLERT; «ture» інакше мовчки лишав би синк вимкненим.
        Ecr.Infrastructure.Jobs.FlertCompositionSyncOptions.EnabledKey,

        // FLERT F6: після синку зі змінами — перерахунок залежних документів відкритих періодів (типово вимкнено).
        Ecr.Infrastructure.Jobs.FlertSyncDependentsTrigger.EnabledKey,
    ];

    /// <summary>Ключі з переліком допустимих значень (без урахування регістру).</summary>
    public static readonly IReadOnlyList<(string Key, string[] Allowed)> Choices =
    [
        ("Schema:StartupMode", ["Validate", "Migrate"]),
        ("Database:EditionMode", Enum.GetNames<SqlEditionMode>()),

        // MI-02 (F1c): «Databse» інакше мовчки лишав би Quartz.
        (Infrastructure.Jobs.DbBackgroundJobScheduler.ModeKey, Enum.GetNames<Infrastructure.Jobs.JobQueueMode>()),
        (Infrastructure.Jobs.JobLaneMap.ExecutorKey, Enum.GetNames<Infrastructure.Jobs.RecalculationExecutor>()),
        (Ecr.Api.Observability.TelemetrySetup.ProtocolKey, Enum.GetNames<OpenTelemetry.Exporter.OtlpExportProtocol>()),

        // D-464: «Month» — помісячне читання вікна подій; «Mnth» інакше мовчки лишав би одне читання на рік.
        (Ecr.Infrastructure.Jobs.SourceEventSyncOptions.EventQueryChunkKey, Enum.GetNames<Ecr.Infrastructure.Jobs.SourceEventQueryChunk>()),
    ];

    /// <summary>Ключ додаткових портів SMTP (ent6 S4).</summary>
    public const string SmtpAllowedPortsKey = "Smtp:AllowedPorts";

    /// <summary>Ключ адреси OTLP-колектора.</summary>
    public const string OtlpEndpointKey = "Telemetry:OtlpEndpoint";

    /// <summary>Ключ адреси приймача звітів CSP.</summary>
    public const string CspReportUriKey = "Security:Csp:ReportUri";

    /// <summary>Ключ примусового режиму CSP.</summary>
    public const string CspEnforceKey = "Security:Csp:Enforce";

    /// <summary>Недійсні значення — по рядку на ключ; порожньо, якщо все гаразд.</summary>
    /// <param name="configuration">Зведена конфігурація застосунку.</param>
    public static IReadOnlyList<string> Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var problems = new List<string>();

        foreach (var (key, min) in Integers)
        {
            if (Value(configuration, key) is not { } text)
            {
                continue;
            }

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                problems.Add(Describe(key, text, "очікується ціле число"));
            }
            else if (number < min)
            {
                problems.Add(Describe(
                    key, text, string.Create(CultureInfo.InvariantCulture, $"очікується ціле число не менше {min}")));
            }
        }

        foreach (var key in Booleans)
        {
            if (Value(configuration, key) is { } text && !bool.TryParse(text, out _))
            {
                problems.Add(Describe(key, text, "очікується true або false"));
            }
        }

        // ⛔ PSS-28: `AllowedHosts` — імена БЕЗ порту. HostFiltering ASP.NET Core порівнює налаштовані записи лише з іменем
        // вузла (порт із заголовка Host відкидається), тож запис `ecr.example.kz:8443` не збігається з жодним запитом:
        // усі, крім localhost, отримують 400, а розгортання виглядає зеленим. Службу з таким значенням не запускаємо.
        if (Value(configuration, AllowedHostsKey) is { } hosts && AllowedHostsProblem(hosts) is { } hostsProblem)
        {
            problems.Add(Describe(AllowedHostsKey, hosts, hostsProblem));
        }

        // PSS-01 (лан L3): нічне вікно прибирання витіснених результатів — `HH:mm-HH:mm`, може переходити через північ.
        // Недійсне значення задача мовчки замінила б дефолтом 01:00-05:00 (і лише писала б попередження), тож службу з ним не запускаємо.
        // Той самий розбір, що й у задачі, — `SupersededPurgeOptions.ParseNightWindow`.
        if (Value(configuration, Ecr.Infrastructure.Persistence.SupersededPurgeOptions.NightWindowKey) is { } nightWindow
            && Ecr.Infrastructure.Persistence.SupersededPurgeOptions.ParseNightWindow(nightWindow) is null)
        {
            problems.Add(Describe(
                Ecr.Infrastructure.Persistence.SupersededPurgeOptions.NightWindowKey, nightWindow,
                "очікується вікно HH:mm-HH:mm (початок і кінець різні, може переходити через північ), напр. 01:00-05:00"));
        }

        foreach (var (key, allowed) in Choices)
        {
            if (Value(configuration, key) is { } text
                && !allowed.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(Describe(key, text, "очікується одне з: " + string.Join(", ", allowed)));
            }
        }

        // ⛔ ent6 S4: `Smtp:AllowedPorts` — додаткові порти SMTP; нечисловий чи поза 1–65535 елемент інакше мовчки
        // ігнорувався б читачем, і адміністратор не зрозумів би, чому пошта не йде.
        // P3-3: скаляр (ECR_Smtp__AllowedPorts=2526 без __0) читач мовчки ігнорував би — це помилка, а не «порожньо».
        if (Value(configuration, SmtpAllowedPortsKey) is { } scalar)
        {
            problems.Add(Describe(
                SmtpAllowedPortsKey, scalar, "очікується список портів: ECR_Smtp__AllowedPorts__0, ECR_Smtp__AllowedPorts__1, …"));
        }

        foreach (var child in configuration.GetSection(SmtpAllowedPortsKey).GetChildren())
        {
            var text = child.Value?.Trim();

            if (!string.IsNullOrEmpty(text)
                && (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                    || port is < 1 or > 65535))
            {
                problems.Add(Describe(
                    $"{SmtpAllowedPortsKey}:{child.Key}", text, "очікується порт — ціле число від 1 до 65535"));
            }
        }

        // S14: значення дописується в заголовок політики — `;`, пробіл чи керівний
        // символ розщепили б його (див. CspSettings.IsValidReportUri).
        if (Value(configuration, CspReportUriKey) is { } reportUri
            && !Ecr.Api.Security.CspSettings.IsValidReportUri(reportUri))
        {
            problems.Add(Describe(
                CspReportUriKey, reportUri,
                "очікується відносний шлях (/api/v1/csp-report) або абсолютна адреса http(s) без пробілів, ; , і лапок; порожнє значення вимикає звітування"));
        }

        // S1-01: адреса довіреного проксі з помилкою інакше мовчки випадала б із переліку, і
        // запити від цього проксі рахувалися б за адресою сокета — одна межа на всіх.
        var proxiesKey = Ecr.Api.Security.ForwardedClientAddress.KnownProxiesKey;
        if (Value(configuration, proxiesKey) is { } proxies)
        {
            Ecr.Api.Security.ForwardedClientAddress.ParseProxies(proxies, out var invalidProxies);
            if (invalidProxies.Count > 0)
            {
                problems.Add(Describe(
                    proxiesKey, proxies,
                    "очікуються IP-адреси довірених проксі через кому, напр. 10.0.0.5, 10.0.0.6; не IP: "
                    + string.Join(", ", invalidProxies)));
            }
        }

        // D-212 PR-7: невідомий пояс AF інакше зупиняв би кожен синк довідника з датами вже вночі,
        // а не старт служби з ім'ям ключа.
        var zoneKey = Application.Integration.RegistrySync.RegistrySyncValidity.TimeZoneKey;
        if (Value(configuration, zoneKey) is { } zone
            && !Application.Integration.RegistrySync.RegistrySyncValidity.TryResolveTimeZone(zone, out _))
        {
            problems.Add(Describe(zoneKey, zone, "очікується ідентифікатор часового поясу Windows чи IANA, напр. FLE Standard Time або Europe/Kyiv"));
        }

        // D-476: частка понад 100 % — не «без межі», а описка; синк інакше мовчки обрізав би її до 100.
        var flertPercentKey = Ecr.Infrastructure.Jobs.FlertCompositionSyncOptions.MaxDeactivatePercentKey;
        if (Value(configuration, flertPercentKey) is { } flertPercent
            && int.TryParse(flertPercent, NumberStyles.Integer, CultureInfo.InvariantCulture, out var flertPercentValue)
            && flertPercentValue > 100)
        {
            problems.Add(Describe(flertPercentKey, flertPercent, "очікується ціле число від 0 до 100 (відсотків)"));
        }

        // ⛔ U17: увімкнений експорт без адреси колектора — не «експорт кудись за
        // замовчуванням» (localhost:4317 бібліотеки), а помилка адміністратора.
        if (Ecr.Api.Observability.TelemetrySetup.IsEnabled(configuration))
        {
            var endpoint = Value(configuration, OtlpEndpointKey);
            if (endpoint is null)
            {
                problems.Add(
                    $"{OtlpEndpointKey} = «»: обов'язковий, коли {Ecr.Api.Observability.TelemetrySetup.EnabledKey} = true — "
                    + "адреса OTLP-колектора (gRPC), напр. http://collector:4317; або вимкніть експорт "
                    + $"(змінна оточення ECR_{OtlpEndpointKey.Replace(":", "__", StringComparison.Ordinal)}).");
            }
            else if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                     || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                problems.Add(Describe(OtlpEndpointKey, endpoint, "очікується абсолютна адреса http:// або https://, напр. http://collector:4317"));
            }
        }

        return problems;
    }

    /// <summary>
    /// PERF-12: текст попередження про Quartz у Production (журнал старту; перевірка <c>worker</c> на /health/ready
    /// повторює суть локалізованим рядком <c>health.worker.quartzInProduction</c>).
    /// </summary>
    public const string QuartzInProductionWarning =
        "Production працює з Jobs:Queue:Mode = Quartz: перерахунки та інші задачі виконуються всередині процесу Api разом із "
        + "HTTP-запитами, діляться 16 потоками, губляться при перезапуску, а ланцюжок місяців, межа розгалуження й виключення "
        + "документів при захопленні задачі не діють (PERF-12). Для продакшену задайте Jobs:Queue:Mode = Database і "
        + "Jobs:Recalculation:Executor = Worker та встановіть службу EcrWorker (`deploy-ecr.ps1` робить це типово; runbook §10); "
        + "Quartz лишайте лише для розробки й малих інсталяцій.";

    /// <summary>
    /// PERF-12: попередження старту, коли перерахунок віддано пулу <c>EcrWorker</c> (<c>Database</c> + <c>Worker</c>), а служби
    /// на сервері немає чи її вимкнено; <c>null</c> — усе гаразд або режим інший.
    /// </summary>
    /// <param name="configuration">Зведена конфігурація.</param>
    /// <param name="service">Стан служби за реєстром.</param>
    public static string? WorkerServiceWarning(IConfiguration configuration, Ecr.Api.Health.WorkerServiceState service)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (Infrastructure.Jobs.DbBackgroundJobScheduler.ReadMode(configuration) != Infrastructure.Jobs.JobQueueMode.Database
            || Infrastructure.Jobs.JobLaneMap.ReadExecutor(configuration) != Infrastructure.Jobs.RecalculationExecutor.Worker
            || service is not (Ecr.Api.Health.WorkerServiceState.Missing or Ecr.Api.Health.WorkerServiceState.Disabled))
        {
            return null;
        }

        return $"Перерахунок віддано службі EcrWorker (Jobs:Queue:Mode = Database, Jobs:Recalculation:Executor = Worker), але служба "
            + (service == Ecr.Api.Health.WorkerServiceState.Missing ? "не встановлена" : "вимкнена")
            + " на цьому сервері: задачі перерахунку стоятимуть у черзі без виконавця (PERF-12). Встановіть/увімкніть EcrWorker "
            + "(runbook п. 10, 10.2) або перемкніть Api на Quartz/InProcess (п. 10.1).";
    }

    /// <summary>Попередження, які старт не зупиняють.</summary>
    /// <param name="configuration">Зведена конфігурація застосунку.</param>
    /// <remarks>
    /// ⚠ <c>U17</c>: адреса колектора задана, а експорт вимкнено — значення нічого
    /// не робить. Адміністратор, що задав колектор, має дізнатися про це з журналу
    /// старту, а не з порожнього дашборда.
    /// </remarks>
    /// <param name="isProduction">Середовище Production: лише в ньому попереджає про <c>AllowedHosts = *</c> (PSS-28).</param>
    public static IReadOnlyList<string> Warnings(IConfiguration configuration, bool isProduction = false)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var warnings = new List<string>();

        // ⚠ PSS-28: `AllowedHosts` порожній або `*` — Kestrel приймає будь-який заголовок Host, а CSRF-перевірка
        // однакового хоста довіряє X-Forwarded-Host: DNS rebinding дотягується до анонімних ендпоінтів. Значення
        // `*` лишається типовим (помилкове ім'я замкнуло б користувачів), тож лише попередження, лише в Production.
        if (isProduction && IsAnyHostAllowed(Value(configuration, AllowedHostsKey)))
        {
            warnings.Add(
                "AllowedHosts = * (будь-який заголовок Host приймається): задайте імена вузла через "
                + "`deploy-ecr.ps1 -AllowedHosts` або змінну ECR_AllowedHosts, щоб закрити DNS rebinding на "
                + "анонімні ендпоінти (PSS-28).");
        }

        // ⚠ PERF-12 (AUDIT-PERF1000-2026-11-21, рішення людини п. 4): у Production типовий режим — черга в базі + служба
        // EcrWorker (deploy-ecr.ps1 / MSI пишуть Database + Worker самі). Quartz у процесі Api — лише для розробки й малих
        // інсталяцій: немає ланцюжка місяців, межі розгалуження й виключення документів при захопленні, 16 потоків ділять усі
        // задачі, а задачі губляться при перезапуску. Те саме — жовта примітка перевірки `worker` на /health/ready.
        if (isProduction && Infrastructure.Jobs.DbBackgroundJobScheduler.ReadMode(configuration) == Infrastructure.Jobs.JobQueueMode.Quartz)
        {
            warnings.Add(QuartzInProductionWarning);
        }

        if (Value(configuration, OtlpEndpointKey) is { } endpoint
            && !Ecr.Api.Observability.TelemetrySetup.IsEnabled(configuration))
        {
            warnings.Add(
                $"{OtlpEndpointKey} = «{endpoint}», але {Ecr.Api.Observability.TelemetrySetup.EnabledKey} ≠ true — "
                + "експорт метрик OTLP вимкнено, значення ігнорується (runbook §3.4).");
        }

        // ⚠ S1-01: без переліку довірених проксі довіряється найправіший запис X-Forwarded-For —
        // правильно за ОДНИМ проксі, що дописує заголовок, але запит повз проксі (прямо на
        // порт Kestrel) сам обирає собі адресу.
        var trustKey = Ecr.Api.Security.ForwardedClientAddress.TrustForwardedForKey;
        if (Value(configuration, trustKey) is { } trust
            && bool.TryParse(trust, out var trusted) && trusted
            && Value(configuration, Ecr.Api.Security.ForwardedClientAddress.KnownProxiesKey) is null)
        {
            warnings.Add(
                $"{trustKey} = true без {Ecr.Api.Security.ForwardedClientAddress.KnownProxiesKey}: адресою клієнта "
                + "вважається найправіший запис X-Forwarded-For. Закрийте порт застосунку для всіх, крім проксі, "
                + "або перелічіть адреси проксі (runbook §2.1).");
        }

        // ✎ L1b / D2: межа дітей розкладу перерахунку не менша за місця виконавця, що бере лейн перерахунку в цьому процесі, —
        // попередження лише там, де виконавець не тримає останнє місце для не-дітей (Quartz — потоки пулу). Черга в базі з
        // InProcess резервує його сам (JobWorker), тож там — рядок Information (Notes), а не попередження на кожному старті
        // типової конфігурації. Пул воркерів (Executor = Worker) попереджає сам (Ecr.Worker).
        if (FanOutCapacity(configuration).Warning is { } fanOutWarning)
        {
            warnings.Add(fanOutWarning);
        }

        // ⚠ S14: примусова CSP не проганялася браузерним набором (e2e-stand.ps1) —
        // вмикати її можна лише після прогону і спостереження за ecr.csp.violations.
        if (Value(configuration, CspEnforceKey) is { } enforce
            && bool.TryParse(enforce, out var enforced) && enforced)
        {
            warnings.Add(
                $"{CspEnforceKey} = true: повна Content-Security-Policy застосовується примусово. "
                + "Переконайтеся, що e2e-набір пройшов під нею, а ecr.csp.violations порожній (runbook §3.4).");
        }

        return warnings;
    }

    /// <summary>
    /// Рядки старту рівня Information: не проблема, але адміністратору варто знати фактичне значення (✎ L1b / D2 — скільки
    /// дітей розкладу перерахунку виконується насправді, коли межа не менша за місця воркера Api).
    /// </summary>
    /// <param name="configuration">Зведена конфігурація застосунку.</param>
    public static IReadOnlyList<string> Notes(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var notes = new List<string>();
        if (FanOutCapacity(configuration).Information is { } fanOut)
        {
            notes.Add(fanOut);
        }

        // ✎ PERF-05: межа дітей розкладу проти глобальної стелі важких перерахунків.
        if (Infrastructure.Jobs.QuartzJobTypeLimiter.GlobalCapNote(
                Infrastructure.Jobs.QuartzJobTypeLimiter.ReadRecalcFanOutMaxConcurrency(configuration),
                Infrastructure.Jobs.QuartzJobTypeLimiter.ReadRecalcMaxConcurrent(configuration)) is { } global)
        {
            notes.Add(global);
        }

        return notes;
    }

    /// <summary>✎ L1b / D2: межа дітей розкладу проти місць виконавця перерахунку цього процесу (режим і виконавець Api).</summary>
    private static (string? Warning, string? Information) FanOutCapacity(IConfiguration configuration)
    {
        var cap = Infrastructure.Jobs.QuartzJobTypeLimiter.ReadRecalcFanOutMaxConcurrency(configuration);
        if (Infrastructure.Jobs.DbBackgroundJobScheduler.ReadMode(configuration) == Infrastructure.Jobs.JobQueueMode.Database)
        {
            return Infrastructure.Jobs.JobLaneMap.ReadExecutor(configuration) == Infrastructure.Jobs.RecalculationExecutor.InProcess
                ? Infrastructure.Jobs.QuartzJobTypeLimiter.FanOutCapacity(
                    cap, Infrastructure.Jobs.JobWorkerOptions.ReadApiMaxConcurrency(configuration), "місця воркера Api", lastSlotReserved: true)
                : (null, null);
        }

        return Infrastructure.Jobs.QuartzJobTypeLimiter.FanOutCapacity(
            cap, Infrastructure.Jobs.QuartzJobTypeLimiter.ReadThreadCount(configuration),
            Infrastructure.Jobs.QuartzJobTypeLimiter.ThreadCountKey, lastSlotReserved: false);
    }

    /// <summary>Перевіряє конфігурацію; на недійсній — пише Critical і зупиняє старт.</summary>
    /// <param name="app">Зібраний застосунок.</param>
    /// <exception cref="InvalidOperationException">Є хоч один недійсний ключ.</exception>
    public static void ValidateEcrConfiguration(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ecr.Startup");

        foreach (var warning in Warnings(app.Configuration, app.Environment.IsProduction()))
        {
            LogConfigurationWarning(logger, warning);
        }

        foreach (var note in Notes(app.Configuration))
        {
            LogConfigurationNote(logger, note);
        }

        // ⚠ PERF-12: Database + Worker без служби на старті — задачі перерахунку стали б у чергу без виконавця; кажемо одразу,
        // не чекаючи першої проби /health/ready.
        using var scope = app.Services.CreateScope();
        var probe = scope.ServiceProvider.GetService<Ecr.Api.Health.IRecalculationWorkerProbe>();
        if (probe is not null && WorkerServiceWarning(app.Configuration, probe.ReadServiceState()) is { } workerWarning)
        {
            LogConfigurationWarning(logger, workerWarning);
        }

        var problems = Validate(app.Configuration);
        if (problems.Count == 0)
        {
            return;
        }

        var message = "Недійсна конфігурація — служба не стартує:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, problems.Select(p => "  " + p))
            + Environment.NewLine
            + "Значення задаються в %ProgramData%\\ECR\\config\\appsettings.Production.json "
            + "або змінними оточення служби ECR_<Секція>__<Ключ>.";

        LogConfigurationInvalid(logger, message);
        throw new InvalidOperationException(message);
    }

    /// <summary>Ключ конфігурації ASP.NET Core: перелік дозволених заголовків Host через <c>;</c>.</summary>
    public const string AllowedHostsKey = "AllowedHosts";

    private static readonly System.Text.RegularExpressions.Regex AllowedHostName = new(
        @"^(?:(?:\*\.)?[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*|\[[0-9A-Fa-f:.]+\])\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Причина, з якої <c>AllowedHosts</c> недійсний, або <c>null</c>. Записи — через <c>;</c>: <c>*</c>, ім'я вузла
    /// (провідний <c>*.</c> допускається) чи IPv6 у дужках; БЕЗ порту й схеми.
    /// </summary>
    /// <param name="allowedHosts">Значення ключа.</param>
    public static string? AllowedHostsProblem(string allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);

        foreach (var entry in allowedHosts.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry == "*" || AllowedHostName.IsMatch(entry))
            {
                continue;
            }

            var hasPort = entry.StartsWith('[') ? entry.Contains("]:", StringComparison.Ordinal)
                : entry.Contains(':', StringComparison.Ordinal) && !entry.Contains("://", StringComparison.Ordinal);

            return hasPort
                ? $"запис «{entry}» містить порт: HostFiltering ASP.NET Core порівнює імена БЕЗ порту, тому такий запис не збігається "
                  + "з жодним запитом і замкне всіх користувачів; вкажіть ім'я без порту (наприклад ecr.example.kz)"
                : $"запис «{entry}» не ім'я вузла (очікується * або ім'я без схеми, шляху й порту; провідний *. допускається)";
        }

        return null;
    }

    /// <summary>Порожній перелік чи елемент <c>*</c> — дозволено будь-який Host.</summary>
    /// <param name="allowedHosts">Значення <c>AllowedHosts</c>; <c>null</c> — не задано.</param>
    public static bool IsAnyHostAllowed(string? allowedHosts)
        => allowedHosts is null
           || allowedHosts.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
               .Any(entry => entry == "*");

    /// <summary>Значення ключа або <c>null</c>, якщо його не задано чи воно порожнє.</summary>
    private static string? Value(IConfiguration configuration, string key)
        => configuration[key] is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static string Describe(string key, string value, string expected)
        => $"{key} = «{value}»: {expected} (змінна оточення ECR_{key.Replace(":", "__", StringComparison.Ordinal)}).";

    [LoggerMessage(Level = LogLevel.Critical, Message = "{Message}")]
    private static partial void LogConfigurationInvalid(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Конфігурація: {Warning}")]
    private static partial void LogConfigurationWarning(ILogger logger, string warning);

    [LoggerMessage(Level = LogLevel.Information, Message = "Конфігурація: {Note}")]
    private static partial void LogConfigurationNote(ILogger logger, string note);
}
