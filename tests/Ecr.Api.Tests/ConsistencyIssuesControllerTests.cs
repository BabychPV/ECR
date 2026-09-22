using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Consistency;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>GET /api/v1/consistency/issues</c> — знахідки нічної перевірки
/// узгодженості, видимі з продукту.
/// </summary>
/// <remarks>
/// ⛔ До цього ендпоінта рядків <c>aud.ConsistencyIssue</c> не читав НІХТО:
/// ні сервер, ні клієнт. Видимою була лише КІЛЬКІСТЬ за типом (лічильник
/// <c>ecr.consistency.issues</c> із міткою <c>kind</c>) — тобто «є 12 знахідок
/// <c>BROKEN_FK</c>» без жодного способу дізнатися, які саме рядки зачеплені.
///
/// ⛔ Тест НАСКРІЗНИЙ і саме тому цінний: знахідку пише справжня
/// <see cref="ConsistencyCheckJob"/> у справжню базу, а читає її справжній
/// HTTP-запит через справжній конвеєр автентифікації й перевірки прав. Мок
/// репозиторію довів би лише те, що обробник викликає порт — тобто нічого про
/// те, чи бачить адміністратор свої знахідки.
/// </remarks>
[Collection("SqlServer")]
public sealed class ConsistencyIssuesControllerTests(SqlServerFixture sql)
{
    private const string Password = "Api-Consistency-Probe-2026!";

    /// <summary>Право, під яким стоїть журнал знахідок.</summary>
    private const string ViewHealth = "System.ViewHealth";

    /// <summary>Право, під яким стоїть прогін перевірки на вимогу (<c>BE-30</c>).</summary>
    private const string RunJob = "System.RunJob";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.7")]
    public async Task Ендпоінт_віддає_знахідку_яку_щойно_записала_нічна_перевірка()
    {
        // ── Готуємо ДАНІ, на яких перевірка справді щось знайде ──────────
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowCount: 2, ct: CancellationToken.None).ConfigureAwait(true);

        // Осиротіла комірка: посилання на запис довідника, якого не існує.
        // Ідентифікатор від'ємний і унікальний для цього прогону — саме за
        // ним нижче впізнається ВЛАСНА знахідка серед чужих.
        var orphanEntryId = -1_900_000 - Random.Shared.Next(1, 90_000);
        await InsertOrphanCellAsync(doc, doc.RowIds[0], orphanEntryId).ConfigureAwait(true);

        // ── Прогін справжньої задачі, не підміна запису ──────────────────
        await RunConsistencyCheckAsync().ConfigureAwait(true);

        // ── Читання через HTTP тим, хто має право ────────────────────────
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, ViewHealth).ConfigureAwait(true);

        var response = await client
            .GetAsync(new Uri(
                "/api/v1/consistency/issues?limit=100&openOnly=true&ruleCode=ORPHANED_CELL",
                UriKind.Relative))
            .ConfigureAwait(true);

        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {app.ErrorsText}");

        var page = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement;

        var items = page.GetProperty("items").EnumerateArray().ToList();

        // ⛔ Головне твердження: у відповіді є САМЕ ТА знахідка, яку щойно
        // записала задача. Не «перелік не порожній» — журнал спільний для
        // всієї колекції SqlServer, і непорожнім він буває від чужих рядків.
        var mine = items.SingleOrDefault(i => string.Equals(
            i.GetProperty("entityId").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            doc.RowIds[0].ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal));

        Assert.True(
            mine.ValueKind != JsonValueKind.Undefined,
            $"Знахідку про рядок {doc.RowIds[0]} записано в aud.ConsistencyIssue, "
            + $"але ендпоінт її не віддав. Повернуто елементів: {items.Count}.");

        // Текст знахідки доїжджає до клієнта цілим: саме він і відповідає на
        // питання «де саме», якого не давав лічильник.
        Assert.Contains(
            orphanEntryId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            mine.GetProperty("message").GetString() ?? string.Empty,
            StringComparison.Ordinal);

        Assert.Equal("ORPHANED_CELL", mine.GetProperty("ruleCode").GetString());
        Assert.Equal("doc.CellValue", mine.GetProperty("entityType").GetString());

        // ⚠ Знахідка ще не закрита — інакше фільтр `openOnly=true` віддав би
        // її помилково, і сам фільтр нічого не означав би.
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("resolvedAt").ValueKind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_журнал_відмовляє_а_не_віддає_порожній_перелік()
    {
        // ⛔ Різниця не косметична. Порожній перелік без права означав би, що
        // «знахідок немає» і «тобі не показують» виглядають ОДНАКОВО — а це
        // найгірша з відповідей про стан даних: адміністратор без права
        // повідомив би, що система здорова.
        using var app = new EcrApiFactory(sql);
        var address = new Uri("/api/v1/consistency/issues?limit=10", UriKind.Relative);

        using (var stranger = await SignedInAsync(app).ConfigureAwait(true))
        {
            var denied = await stranger.GetAsync(address).ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

            var body = await denied.Content.ReadAsStringAsync().ConfigureAwait(true);

            // Відмова називає ПРАВО, якого бракує: інакше єдина дія у
            // відповідь — писати в підтримку «щось не працює».
            Assert.Contains(ViewHealth, body, StringComparison.Ordinal);
        }

        // ⚠ Друга половина доказу: та сама адреса тому самому конвеєру, лише
        // з правом, віддає `200`. Без неї `403` міг би походити від чого
        // завгодно — від невірного маршруту до збою автентифікації.
        using var allowed = await SignedInAsync(app, ViewHealth).ConfigureAwait(true);
        var granted = await allowed.GetAsync(address).ConfigureAwait(true);

        Assert.True(granted.IsSuccessStatusCode, $"{granted.StatusCode}: {app.ErrorsText}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Розмір_сторінки_понад_максимум_відхиляється_а_не_обрізається_мовчки()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, ViewHealth).ConfigureAwait(true);

        var response = await client
            .GetAsync(new Uri("/api/v1/consistency/issues?limit=5000", UriKind.Relative))
            .ConfigureAwait(true);

        // ⚠ `422`, а не `400`: межа сторінки — правило (`ECR-REQ-0422`), а не
        // синтаксис запиту. Головне тут не сам код, а що запит ВІДХИЛЕНО:
        // мовчазне обрізання до максимуму віддало б клієнтові сторінку, якої
        // він не просив, і «наступний курсор» від неї вів би не туди.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Contains("ECR-REQ-0422", body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "BE-30")]
    public async Task Прогін_на_вимогу_вимагає_System_RunJob_і_лишає_слід_у_журналі_безпеки()
    {
        // ⛔ Єдине місце, де видно ОБИДВА справжні імені типу задачі. Ручний
        // прогін ставиться маркером, нічний розклад — конкретним класом із
        // `Ecr.Infrastructure`, якого прикладний шар не бачить і назвати
        // типом не може; він звіряє спільний суфікс. Без цієї звірки
        // перейменування класу зламало б перевірку «чи вже йде» МОВЧКИ:
        // обробник просто перестав би бачити прогін у черзі.
        Assert.True(RunConsistencyCheckHandler.IsConsistencyCheckCode(
            typeof(Ecr.Application.Ports.IConsistencyCheckJob).FullName));
        Assert.True(RunConsistencyCheckHandler.IsConsistencyCheckCode(typeof(ConsistencyCheckJob).FullName));

        // ⚠ Бази `EcrTest_*` переживають прогін навмисно, тож незавершений
        // рядок від ПОПЕРЕДНЬОГО запуску дав би тут `409` і читався б як
        // дефект. Прибирання стосується рівно рядків цього ендпоінта.
        await ClearInFlightChecksAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var address = new Uri("/api/v1/consistency/run", UriKind.Relative);
        var reason = $"BE-30 probe {Guid.NewGuid():N}";

        using (var stranger = await SignedInAsync(app, ViewHealth).ConfigureAwait(true))
        {
            // ⛔ Право ЧИТАТИ журнал знахідок прогону не відкриває: дивитися
            // на знахідки і запускати повний обхід усіх партицій — різні
            // повноваження, і друге сід видає іменованим особам окремо.
            var denied = await stranger
                .PostAsJsonAsync(address, new { reason }).ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

            Assert.Contains(
                RunJob,
                await denied.Content.ReadAsStringAsync().ConfigureAwait(true),
                StringComparison.Ordinal);
        }

        try
        {
            using var allowed = await SignedInAsync(app, RunJob).ConfigureAwait(true);
            var accepted = await allowed
                .PostAsJsonAsync(address, new { reason }).ConfigureAwait(true);

            Assert.True(
                accepted.StatusCode == HttpStatusCode.Accepted,
                $"{accepted.StatusCode}: {app.ErrorsText}");

            var jobId = JsonDocument
                .Parse(await accepted.Content.ReadAsStringAsync().ConfigureAwait(true))
                .RootElement.GetProperty("jobId").GetString();

            // ⚠ `202` — обіцянка, не результат: саме цим ідентифікатором
            // клієнт опитує `GET /api/v1/jobs/{jobId}`.
            Assert.False(string.IsNullOrWhiteSpace(jobId));

            // ⛔ Головне твердження: ПРИЧИНА доїхала до `aud.SecurityEvent`.
            // Прогін під небезпечним правом, про який журнал не знає «навіщо»,
            // відповідає лише на «хто» — а питають тут саме про перше.
            Assert.Equal(1, await SecurityEventsWithReasonAsync(reason).ConfigureAwait(true));
        }
        finally
        {
            await ClearInFlightChecksAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Прогін, поки попередній ще йде, — <c>409 ECR-JOB-0409</c>, а не 422.
    /// </summary>
    /// <remarks>
    /// ⛔ Відмову кидає <c>BusinessRuleException</c>, а загальний арм для нього
    /// — 422. Код і статус тут прибиті ЛІТЕРАЛАМИ: твердження проти константи
    /// обробника рухалося б разом із нею. Незавершений прогін ставиться рядком
    /// <c>itg.JobProgress</c> напряму — справжня задача могла б устигнути
    /// завершитися, і тест плавав би.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "BE-30")]
    public async Task Повторний_прогін_поки_йде_попередній_дає_409_ECR_JOB_0409()
    {
        await ClearInFlightChecksAsync().ConfigureAwait(true);

        var busyJobId = $"be30-{Guid.NewGuid():N}";
        await using (var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            db.JobProgresses.Add(new JobProgress(
                busyJobId, "Ecr.Tests.Probe.ConsistencyCheckJob", DateTime.UtcNow, null));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = await SignedInAsync(app, RunJob).ConfigureAwait(true);

            var refused = await client.PostAsJsonAsync(
                new Uri("/api/v1/consistency/run", UriKind.Relative),
                new { reason = "BE-30 second run" }).ConfigureAwait(true);

            Assert.True(
                refused.StatusCode == HttpStatusCode.Conflict,
                $"{refused.StatusCode}: {await refused.Content.ReadAsStringAsync().ConfigureAwait(true)}");

            var problem = JsonDocument
                .Parse(await refused.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;

            Assert.Equal("ECR-JOB-0409", problem.GetProperty("errorCode").GetString());
            Assert.Equal("err.ECR-JOB-0409.consistencyCheckRunning", problem.GetProperty("messageKey").GetString());
        }
        finally
        {
            await ClearInFlightChecksAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Прибирає незавершені рядки прогону перевірки з <c>itg.JobProgress</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Фільтр вузький: такі рядки створює РІВНО цей ендпоінт і нічний
    /// розклад, якого в тестовому застосунку немає. Прибирати ширше означало б
    /// нишком ламати сусідні тести черги.
    /// </remarks>
    private async Task ClearInFlightChecksAsync()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE itg.JobProgress
            WHERE State IN (N'Queued', N'Running')
              AND JobCode LIKE N'%ConsistencyCheckJob';
            """;

        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Скільки подій безпеки прогону несуть саме цю причину.</summary>
    private async Task<int> SecurityEventsWithReasonAsync(string reason)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM aud.SecurityEvent
            WHERE EventType = @type AND DetailsJson LIKE @reason;
            """;
        command.Parameters.AddWithValue("@type", RunConsistencyCheckHandler.EventType);
        command.Parameters.AddWithValue("@reason", $"%{reason}%");

        return (int)(await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false))!;
    }

    /// <summary>Проганяє справжню перевірку узгодженості на тій самій базі.</summary>
    /// <remarks>
    /// ⚠ Підмінені лише <see cref="IOrphanScanner"/> й лічильник: перший
    /// перераховує похідну ознаку по ВСІЙ базі (довго й до цього тесту не
    /// стосується), другий пише в OpenTelemetry. Сам запис знахідок —
    /// справжній код задачі.
    /// </remarks>
    private async Task RunConsistencyCheckAsync()
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

        // ⚠ Підмінений сканер віддає порожній підсумок (`default` структури) —
        // це рівно «прохід нічого не зробив», і саме цього тут і треба.
        var job = new ConsistencyCheckJob(
            db,
            Substitute.For<IOrphanScanner>(),
            new TestClock(new DateTime(2026, 9, 18, 3, 0, 0, DateTimeKind.Utc)),
            Substitute.For<IConsistencyMetrics>());

        await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Вставляє комірку, що посилається на неіснуючий запис довідника.
    /// </summary>
    /// <remarks>
    /// ⛔ Той самий прийом, що в <c>ConsistencyCheckJobWriteTests</c>:
    /// <c>FK_CellValue_Entry</c> забороняє це посилання звичайним записом — і
    /// правильно. <c>NOCHECK</c> чесно відтворює дані, які потрапили в базу
    /// ПОЗА звичайним шляхом (до міграції, ручне втручання DBA), а не обхід
    /// перевірки, яку мав пройти звичайний запис. <c>finally</c> гарантує
    /// повернення обмеження навіть при падінні самої вставки.
    /// </remarks>
    private async Task InsertOrphanCellAsync(TestDocument doc, long rowId, long registryEntryId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);

        await using (var disable = connection.CreateCommand())
        {
            disable.CommandText = "ALTER TABLE doc.CellValue NOCHECK CONSTRAINT FK_CellValue_Entry;";
            await disable.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT doc.CellValue
                    (PeriodKey, TableRowId, TableDefId, ColumnDefId, ValueRegistryEntryId, IsCalculated, IsEmpty)
                VALUES (@p, @r, @t, @c, @reg, 0, 0);
                """;
            command.Parameters.AddWithValue("@p", doc.PeriodKey.Value);
            command.Parameters.AddWithValue("@r", rowId);
            command.Parameters.AddWithValue("@t", doc.TableDefId);
            command.Parameters.AddWithValue("@c", doc.ColumnDefIds[0]);
            command.Parameters.AddWithValue("@reg", registryEntryId);
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            // ⚠ `WITH NOCHECK` при повторному вмиканні — обов'язково: звичайне
            // `CHECK CONSTRAINT` перевалідувало б ВСЮ таблицю і впало б на
            // щойно вставленому навмисно зламаному рядку.
            await using var enable = connection.CreateCommand();
            enable.CommandText = "ALTER TABLE doc.CellValue WITH NOCHECK CHECK CONSTRAINT FK_CellValue_Entry;";
            await enable.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Клієнт із чинним сеансом локального користувача.</summary>
    /// <param name="app">Фабрика застосунку.</param>
    /// <param name="permissions">
    /// Функціональні права, які треба видати. Порожньо — користувач без прав.
    /// </param>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
    {
        var name = $"cons_{Guid.NewGuid():N}"[..20];

        var options = new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options;

        await using (var db = new EcrDbContext(options))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));

            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            if (permissions.Length > 0)
            {
                var role = new Role(
                    Ecr.Domain.ValueObjects.EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                    new Ecr.Domain.ValueObjects.LocalizedText(
                        new Dictionary<string, string> { ["en"] = "Consistency test" }));
                db.Roles.Add(role);
                await db.SaveChangesAsync().ConfigureAwait(false);

                foreach (var permission in permissions)
                {
                    db.RolePermissions.Add(new RolePermission(role.Id, permission));
                }

                db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
        }

        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }
}
