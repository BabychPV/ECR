using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;

namespace Ecr.DataGen;

/// <summary>
/// Гейт продуктивності через HTTP — навантаження на піднятий <c>Ecr.Api</c>.
/// </summary>
/// <remarks>
/// Рядок <c>MS-01</c> директиви №14 (частина 3, §3.8); рядок <b>F</b> §2.2
/// («вимірювання міряє не те»).
///
/// ⛔ Навіщо другий режим, коли є <see cref="GateBenchmark"/>. Той б'є
/// <c>new NormalizedCellStore(db)</c> напряму (<c>GateBenchmark.cs:150,796</c>)
/// — повз права, аудит, «дотик» документа й чергу задач. Його 63 RPS — це
/// <b>оптимістична стеля сховища</b>, а не число продукту, і рішення
/// <c>D-21</c> (гібридне зберігання) на ньому ухвалювати не можна: воно
/// відповідає на питання «чи тримає <c>doc.CellValue</c>», тоді як вузьке
/// місце — конвеєр навколо неї.
///
/// ⛔ Три конкретні речі, яких старий режим НЕ БАЧИТЬ, а цей бачить:
/// <list type="number">
/// <item><description>
/// <c>GateBenchmark.cs:471</c> пише сталий <c>1m</c> завжди по 100 комірок —
/// рівно один план запиту. Тут числа <b>різні</b> й розмір батчу <b>різний</b>,
/// тож вада <c>WR-01</c> («параметри без типів → план на кожну форму»)
/// проявляється і рахується в <c>merge_plans_*</c>.
/// </description></item>
/// <item><description>
/// <c>:475</c> будує <c>CellChangeSet</c> без очікуваних версій, тобто
/// <c>ClaimRowsAsync</c> не виконується зовсім. Тут кожен <c>PATCH</c> несе
/// <c>baseVersion</c>, як справжній клієнт.
/// </description></item>
/// <item><description>
/// <b>≥ 2 оператори на ОДИН документ.</b> Без цього дефект <c>DAT-01</c>
/// (фальшивий <c>409</c> на рівні документа: <c>DocumentStore.cs:263-270</c> +
/// <c>DocumentConfiguration.cs:165</c>) не проявляється ВЗАГАЛІ — один оператор
/// сам із собою не конкурує за <c>RowVersion</c> документа.
/// </description></item>
/// </list>
///
/// ⚠ Критерії — з <c>tz/08</c> §8.2 (p95/p99 операцій), а не RPS сховища:
/// зріз 400/800 мс, <c>PATCH</c> 100 комірок 250/500 мс.
///
/// ⚠ <c>Q-145</c>: генератор, застосунок і SQL Server на тих самих ядрах
/// спотворюють замір. Клас це не лікує — він це <b>називає</b>:
/// <c>stand_saturated</c> і нотатка в кожному звіті, де адреса локальна.
/// </remarks>
public sealed class HttpLoadBenchmark
{
    /// <summary>Бюджет читання зрізу, p95 (<c>tz/08</c> §8.2).</summary>
    private const double SliceP95BudgetMs = 400;

    /// <summary>Бюджет читання зрізу, p99.</summary>
    private const double SliceP99BudgetMs = 800;

    /// <summary>Бюджет <c>PATCH</c> 100 комірок, p95.</summary>
    private const double PatchP95BudgetMs = 250;

    /// <summary>Бюджет <c>PATCH</c> 100 комірок, p99.</summary>
    private const double PatchP99BudgetMs = 500;

    /// <summary>Скільки комірок у «100 комірок» із бюджету.</summary>
    private const int ProbeCells = 100;

    /// <summary>Скільки повторів у тихому зондуванні звернень на операцію.</summary>
    private const int ProbeIterations = 20;

    /// <summary>Робоча роль навантаження.</summary>
    private const string RoleCode = "GateLoadOperator";

    /// <summary>Разовий пароль операторів навантаження.</summary>
    private const string InitialPassword = "Gate-Init-2026!";

    /// <summary>Робочий пароль операторів навантаження.</summary>
    private const string WorkPassword = "Gate-Work-2026!";

    /// <summary>Робочий пароль bootstrap після зміни разового.</summary>
    private const string BootstrapWorkPassword = "Gate-Bootstrap-Work-2026!";

    /// <summary>
    /// Різні розміри батчу <c>PATCH</c> — саме вони роблять ваду <c>WR-01</c> видимою.
    /// </summary>
    /// <remarks>
    /// ⛔ Сталий розмір дав би сталий текст запиту, тобто один план у кеші — і
    /// звіт стверджував би, що з кешем планів усе гаразд. <c>WR-01</c> каже
    /// протилежне: текст залежить від розміру чанка, а типи параметрів
    /// <c>Decimal</c>/<c>DateTime2</c> виводяться зі ЗНАЧЕННЯ, тож сигнатура
    /// змінюється ще й від самих чисел.
    /// </remarks>
    private static readonly int[] BatchSizes = [1, 5, 25, 100];

    /// <summary>Ролі, які дістає кожен оператор навантаження.</summary>
    private static readonly string[] RoleCodes = [RoleCode];

    /// <summary>
    /// Права ролі навантаження — рівно ті, без яких замір не пройде.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>Security.ManageRoles</c> потрібен не для заміру, а для гранта:
    /// bootstrap видати ресурсний грант не може (<c>D-121</c> — у нього рівно
    /// два права), тому грант видає перший оператор сам собі
    /// (<c>smoke.ps1:226-229</c>).
    /// </remarks>
    private static readonly string[] RolePermissions =
    [
        "Document.View", "Document.Create", "Project.Manage",
        "Calculation.View", "Security.ManageRoles", "System.ViewHealth",
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Адреса піднятого застосунку.</summary>
    public required Uri BaseAddress { get; init; }

    /// <summary>Рядок підключення до тієї самої бази — для лічильників СУБД.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>Разовий пароль bootstrap (той, що віддали застосунку при першому старті).</summary>
    public required string BootstrapPassword { get; init; }

    /// <summary>Скільки секунд тримати навантаження.</summary>
    public int LoadSeconds { get; init; } = 120;

    /// <summary>Цільовий темп операцій за секунду.</summary>
    public int TargetRps { get; init; } = 25;

    /// <summary>Частка записів у суміші, %.</summary>
    public int WritePercent { get; init; } = 20;

    /// <summary>
    /// Скільки операторів б'є в ОДИН документ.
    /// </summary>
    /// <remarks>
    /// ⛔ Менше двох — і замір втрачає сенс: <c>DAT-01</c> не проявиться, а
    /// звіт скаже «фальшивих 409 нуль», хоча механізм на місці. Тому значення
    /// нижче 2 піднімається до 2 з нотаткою.
    /// </remarks>
    public int Operators { get; init; } = 3;

    /// <summary>
    /// Скільки одночасних робітників обслуговують чергу; <c>0</c> — за темпом.
    /// </summary>
    /// <remarks>
    /// ⚠ Існує заради КОНТРОЛЬНОГО прогону. <c>--workers 1</c> серіалізує все:
    /// два <c>PATCH</c> ніколи не накладаються в часі, і фальшиві <c>409</c>
    /// мусять зникнути повністю. Якщо вони лишаються — конфлікти породжує
    /// генератор (застарілий кеш версій), а не <c>DAT-01</c>, і звіт про
    /// дефект був би наклепом на продукт.
    /// </remarks>
    public int Workers { get; init; }

    /// <summary>Виконує повний прогін: підготовка → зондування → навантаження.</summary>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Виміряні числа, порушення бюджету §8.2 і застереження.</returns>
    public async Task<GateResult> RunAsync(CancellationToken ct)
    {
        var measurements = new Dictionary<string, double>(StringComparer.Ordinal);
        var failures = new List<string>();
        var notes = new List<string>();

        var operators = Math.Max(2, Operators);
        if (Operators < 2)
        {
            notes.Add("Операторів піднято до 2: з одним DAT-01 (фальшивий 409 на рівні "
                + "документа) не проявляється, і нуль конфліктів нічого не доводить.");
        }

        if (!await AliveAsync(ct).ConfigureAwait(false))
        {
            return new GateResult(false, measurements, [], [], Blocked: Fmt($"""
                Застосунок за адресою {BaseAddress} не відповідає на /health/live.
                    Замір не запускався. Що робити:
                      $env:ECR_ConnectionStrings__Ecr = "<той самий рядок>"
                      $env:ECR_Bootstrap__Password    = "<разовий пароль>"
                      $env:ECR_Auth__RequireHttps     = "false"    ⛔ по HTTP без цього cookie не повертається
                      $env:ASPNETCORE_URLS            = "{BaseAddress.GetLeftPart(UriPartial.Authority)}"
                      dotnet run --project src\Ecr.Api --no-launch-profile
                """));
        }

        NameStand(measurements, notes);

        Session[] sessions;
        try
        {
            sessions = await ProvisionAsync(operators, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new GateResult(false, measurements, [], notes, Blocked: Fmt($"""
                Не вдалося підготувати операторів: {ex.Message}
                    Найімовірніша причина — разовий пароль bootstrap не той, що віддали застосунку
                    при першому старті (ECR_Bootstrap__Password). Паролі пізніших стартів ігноруються.
                """));
        }

        try
        {
            var target = await DiscoverAsync(sessions[0], ct).ConfigureAwait(false);
            if (target.Blocked is not null)
            {
                return new GateResult(false, measurements, [], notes, Blocked: target.Blocked);
            }

            measurements["document_id"] = target.DocumentId;
            measurements["period_key"] = target.PeriodKey;
            measurements["tables_under_load"] = target.Tables.Count;
            measurements["operators"] = operators;
            measurements["slice_cells"] = target.Tables[0].Cells;

            await ProbeAsync(sessions[0], target, measurements, notes, ct).ConfigureAwait(false);
            var load = await LoadAsync(sessions, target, ct).ConfigureAwait(false);

            Publish(load, measurements);
            Judge(load, measurements, failures, notes);

            return new GateResult(failures.Count == 0, measurements, failures, notes);
        }
        finally
        {
            foreach (var session in sessions)
            {
                session.Dispose();
            }
        }
    }

    /// <summary>
    /// Називає стенд і зізнається, чи він насичений.
    /// </summary>
    /// <param name="measurements">Куди покласти числа.</param>
    /// <param name="notes">Куди покласти зізнання.</param>
    /// <remarks>
    /// ⛔ <c>Q-145</c>. Генератор (десятки потоків), <c>Ecr.Api</c> і SQL Server
    /// на тих самих ядрах змагаються за процесор: замір показує межу стенда, а
    /// не межу системи. Мовчазне число в такому прогоні гірше за відсутнє —
    /// його понесуть у рішення <c>D-21</c>.
    /// </remarks>
    private void NameStand(Dictionary<string, double> measurements, List<string> notes)
    {
        measurements["generator_cores"] = Environment.ProcessorCount;

        var host = BaseAddress.Host;
        var local = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(host, "::1", StringComparison.Ordinal);

        measurements["stand_saturated"] = local ? 1 : 0;

        if (local)
        {
            notes.Add(Fmt($"""
                ⛔ СТЕНД НАСИЧЕНИЙ, числа занижені. Генератор, Ecr.Api і SQL Server — на одній
                    машині ({Environment.MachineName}, {Environment.ProcessorCount} ядер). Це саме той випадок,
                    про який Q-145: замір міряє межу стенда, а не системи. Числа нижче —
                    ВЕРХНЯ МЕЖА затримки і НИЖНЯ межа темпу; переносити їх на обладнання
                    замовника не можна, порівнювати «до/після» на цьому ж стенді — можна.
                """));
        }
    }

    private async Task<bool> AliveAsync(CancellationToken ct)
    {
        using var client = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), ct)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    // ── Підготовка ───────────────────────────────────────────────────────────

    /// <summary>
    /// Створює роль, операторів і ресурсний грант; вертає готові сеанси.
    /// </summary>
    /// <param name="count">Скільки операторів.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Сеанси з дійсними cookie.</returns>
    /// <remarks>
    /// ⚠ Повторний прогін НЕ падає: усе створення робиться «створити або
    /// пропустити», а вхід спершу пробується робочим паролем. Замір, який
    /// працює лише на свіжій базі, не проганяють двічі — а «до/після» без
    /// двох прогонів не буває.
    ///
    /// ⛔ Порядок кроків узятий зі <c>smoke.ps1:172-234</c> і має рівно три
    /// нетривіальні місця, кожне з яких уже коштувало окремого дефекту:
    /// вхід ОДРАЗУ після зміни пароля (<c>A7-21</c>), грант обов'язковий навіть
    /// власникові всіх прав (<c>A7-22</c>), і перевидання сеансу після гранта,
    /// бо той прокручує штамп безпеки (<c>A7-23</c>).
    /// </remarks>
    private async Task<Session[]> ProvisionAsync(int count, CancellationToken ct)
    {
        var names = Enumerable.Range(1, count).Select(i => Fmt($"gate-op{i}")).ToArray();
        var sessions = new List<Session>();

        var ready = true;
        foreach (var name in names)
        {
            var session = NewSession(name);
            if (await session.TryLoginAsync(name, WorkPassword, ct).ConfigureAwait(false))
            {
                sessions.Add(session);
            }
            else
            {
                session.Dispose();
                ready = false;
                break;
            }
        }

        if (ready && sessions.Count == count)
        {
            return [.. sessions];
        }

        foreach (var session in sessions)
        {
            session.Dispose();
        }

        sessions.Clear();

        using (var admin = NewSession("bootstrap"))
        {
            if (await admin.TryLoginAsync("bootstrap", BootstrapPassword, ct).ConfigureAwait(false))
            {
                await admin.PostAsync(
                    "/api/v1/auth/change-password",
                    new { currentPassword = BootstrapPassword, newPassword = BootstrapWorkPassword },
                    ct).ConfigureAwait(false);
            }

            if (!await admin.TryLoginAsync("bootstrap", BootstrapWorkPassword, ct).ConfigureAwait(false))
            {
                throw new HttpRequestException(
                    "вхід bootstrap не вдався ні разовим паролем, ні робочим " + BootstrapWorkPassword);
            }

            await admin.PostAsync("/api/v1/roles", new
            {
                code = RoleCode,
                nameL10n = new Dictionary<string, string> { ["en"] = "Gate load operator" },
                permissionCodes = RolePermissions,
            }, ct, tolerate: HttpStatusCode.Conflict).ConfigureAwait(false);

            foreach (var name in names)
            {
                await admin.PostAsync("/api/v1/users", new
                {
                    userName = name,
                    provider = "Local",
                    sid = (string?)null,
                    displayName = name,
                    initialPassword = InitialPassword,
                    roleCodes = RoleCodes,
                }, ct, tolerate: HttpStatusCode.Conflict).ConfigureAwait(false);
            }
        }

        foreach (var name in names)
        {
            var session = NewSession(name);
            if (await session.TryLoginAsync(name, InitialPassword, ct).ConfigureAwait(false))
            {
                await session.PostAsync(
                    "/api/v1/auth/change-password",
                    new { currentPassword = InitialPassword, newPassword = WorkPassword },
                    ct).ConfigureAwait(false);
            }

            if (!await session.TryLoginAsync(name, WorkPassword, ct).ConfigureAwait(false))
            {
                session.Dispose();
                throw new HttpRequestException(Fmt($"оператор {name} не входить ні разовим, ні робочим паролем"));
            }

            sessions.Add(session);
        }

        await GrantAsync(sessions[0], ct).ConfigureAwait(false);

        // ⚠ Грант прокрутив штамп безпеки — усі наявні cookie недійсні.
        foreach (var session in sessions)
        {
            if (!await session.TryLoginAsync(session.Name, WorkPassword, ct).ConfigureAwait(false))
            {
                throw new HttpRequestException(Fmt($"оператор {session.Name} не перевидав сеанс після гранта"));
            }
        }

        return [.. sessions];
    }

    /// <summary>
    /// Видає ролі ресурсний грант на кожен проєкт у базі.
    /// </summary>
    /// <param name="session">Сеанс оператора з <c>Security.ManageRoles</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Ідентифікатори проєктів беруться з БАЗИ, а не з
    /// <c>GET /api/v1/projects</c>: до гранта той перелік порожній для всіх
    /// (<c>A7-22</c>), тобто по HTTP дізнатися, на що видавати грант,
    /// неможливо. <c>smoke.ps1:228</c> обходить це сталою <c>resourceId = 1</c>
    /// — на базі генератора з кількома проєктами вона мовчки промахнулася б.
    /// </remarks>
    private async Task GrantAsync(Session session, CancellationToken ct)
    {
        var roles = await session.GetAsync<List<RoleDto>>("/api/v1/roles", ct).ConfigureAwait(false)
            ?? throw new HttpRequestException("перелік ролей порожній");

        var roleId = roles.Find(r => string.Equals(r.Code, RoleCode, StringComparison.Ordinal))?.Id
            ?? throw new HttpRequestException("роль " + RoleCode + " не створилася");

        var projects = await ProjectIdsAsync(ct).ConfigureAwait(false);
        if (projects.Count == 0)
        {
            throw new HttpRequestException("у базі немає жодного проєкту: спершу запустіть генератор");
        }

        await session.PutAsync(Fmt($"/api/v1/roles/{roleId}/grants"), new
        {
            grants = projects.Select(id => new
            {
                resourceKind = "Project",
                resourceId = id,
                level = "Manage",
                isDeny = false,
            }).ToArray(),
        }, ct).ConfigureAwait(false);
    }

    private async Task<List<int>> ProjectIdsAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @t sysname = (SELECT TOP (1) QUOTENAME(s.name) + '.' + QUOTENAME(t.name)
                                  FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
                                  WHERE t.name = 'Project');
            EXEC ('SELECT Id FROM ' + @t + ' ORDER BY Id');
            """;

        var ids = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids;
    }

    private Session NewSession(string name) => new(name, BaseAddress);

    // ── Добір цілі ───────────────────────────────────────────────────────────

    /// <summary>
    /// Знаходить документ у ВІДКРИТОМУ періоді й кілька його таблиць.
    /// </summary>
    /// <param name="session">Будь-який готовий сеанс.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ціль або причина, чому заміру не буде.</returns>
    /// <remarks>
    /// ⛔ Період мусить бути ВІДКРИТИЙ: у закритому кожен <c>PATCH</c> дає
    /// <c>403</c>, і прогін показав би чудову затримку на суцільних відмовах.
    ///
    /// ⚠ Таблиць беремо кілька і РІЗНИХ. Оператори б'ють у різні таблиці одного
    /// документа — саме та конфігурація, у якій <c>DAT-01</c> дає фальшивий
    /// <c>409</c>: конкуренції за комірки немає, а <c>RowVersion</c> документа
    /// один на всіх.
    /// </remarks>
    private static async Task<Target> DiscoverAsync(Session session, CancellationToken ct)
    {
        var projects = await session.GetAsync<PagedDto<ProjectDto>>("/api/v1/projects", ct).ConfigureAwait(false);
        if (projects is null || projects.Items.Count == 0)
        {
            return Target.Stop("Перелік проєктів порожній навіть після гранта — грант не діє (A7-22).");
        }

        foreach (var project in projects.Items)
        {
            var calendar = await session
                .GetAsync<CalendarDto>(Fmt($"/api/v1/projects/{project.Id}/periods"), ct)
                .ConfigureAwait(false);

            var open = calendar?.Periods
                .Where(p => string.Equals(p.State, "Open", StringComparison.Ordinal))
                .OrderByDescending(p => p.PeriodKey)
                .ToList() ?? [];

            foreach (var period in open)
            {
                var found = await TryPeriodAsync(session, period.PeriodKey, ct).ConfigureAwait(false);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return Target.Stop(
            "Жодного документа з таблицями у ВІДКРИТОМУ періоді. У закритому періоді кожен PATCH "
            + "дає 403, і замір показав би затримку відмов, а не роботи.");
    }

    private static async Task<Target?> TryPeriodAsync(Session session, int periodKey, CancellationToken ct)
    {
        var documents = await session
            .GetAsync<PagedDto<DocumentDto>>(Fmt($"/api/v1/documents?periodKey={periodKey}"), ct)
            .ConfigureAwait(false);

        foreach (var document in documents?.Items ?? [])
        {
            var tables = await session
                .GetAsync<List<DocumentTableDto>>(
                    Fmt($"/api/v1/documents/{document.Id}/tables?periodKey={periodKey}"), ct)
                .ConfigureAwait(false) ?? [];

            var usable = new List<TableUnderLoad>();
            foreach (var table in tables.Take(24))
            {
                var slice = await session
                    .GetAsync<SliceDto>(
                        Fmt($"/api/v1/documents/{document.Id}/tables/{table.TableInstanceId}"), ct)
                    .ConfigureAwait(false);

                var writable = slice?.Columns
                    .Where(c => !c.IsReadOnly && IsNumeric(c.DataType))
                    .Select(c => c.Code)
                    .ToArray() ?? [];

                if (slice is null || slice.Rows.Count == 0 || writable.Length == 0)
                {
                    continue;
                }

                usable.Add(new TableUnderLoad(
                    table.TableInstanceId,
                    writable,
                    slice.Rows.Count * slice.Columns.Count,
                    [.. slice.Rows.Select(r => r.RowKey)],
                    new ConcurrentDictionary<string, string>(
                        slice.Rows.ToDictionary(r => r.RowKey, r => r.RowVersion, StringComparer.Ordinal),
                        StringComparer.Ordinal)));
            }

            if (usable.Count >= 2)
            {
                // Найближчі до типового зрізу §8.2 (~5 000 комірок) — саме на
                // ньому записаний бюджет 400/800 мс.
                var chosen = usable
                    .OrderBy(t => Math.Abs(t.Cells - 5000))
                    .Take(4)
                    .ToList();

                return new Target(document.Id, periodKey, chosen, null);
            }
        }

        return null;
    }

    private static bool IsNumeric(string? dataType)
        => dataType is "Decimal" or "Int" or "Number" or "Integer";

    // ── Тихе зондування: звернень на операцію ────────────────────────────────

    /// <summary>
    /// Рахує звернення до СУБД на одну операцію — послідовно, без навантаження.
    /// </summary>
    /// <param name="session">Сеанс одного оператора.</param>
    /// <param name="target">Ціль.</param>
    /// <param name="measurements">Куди покласти числа.</param>
    /// <param name="notes">Куди покласти застереження.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Рахує СЕРВЕР, а не ми: <c>Batch Requests/sec</c> із
    /// <c>sys.dm_os_performance_counters</c> — накопичувальний лічильник
    /// round-trip'ів. Це єдиний спосіб дістати число «звернень на операцію»
    /// ззовні процесу застосунку; усередині процесу те саме дає
    /// <c>Ecr.TestKit.SqlClientCommandCounter</c> (і ще з розкладкою за
    /// категоріями, чого лічильник СУБД не вміє).
    ///
    /// ⚠ Власна вартість заміру ВІДНІМАЄТЬСЯ: читання лічильників саме по собі
    /// є зверненням. Без калібрування кожне число було б завищене рівно на
    /// два, і «≤ 10 звернень» із <c>WR-04</c> перевірялося б проти зсунутої
    /// шкали.
    ///
    /// ⛔ Прогін мусить бути ТИХИЙ. Лічильник — на весь інстанс, тому будь-яка
    /// чужа робота по цій СУБД (інший стенд, інший агент) додається до нашого
    /// числа. Тому зондування йде ДО навантаження й послідовно.
    /// </remarks>
    private async Task ProbeAsync(
        Session session,
        Target target,
        Dictionary<string, double> measurements,
        List<string> notes,
        CancellationToken ct)
    {
        var table = target.Tables[0];

        await ClearPlanCacheAsync(ct).ConfigureAwait(false);

        // Розігрів: перший виклик несе побудову моделі EF, JIT і холодні кеші.
        await session.GetAsync<SliceDto>(SliceUrl(target, table), ct).ConfigureAwait(false);
        await PatchAsync(session, target, table, ProbeCells, new Random(1), ct).ConfigureAwait(false);

        var calibrateFrom = await CountersAsync(ct).ConfigureAwait(false);
        var calibrateTo = await CountersAsync(ct).ConfigureAwait(false);
        var overhead = calibrateTo.Batches - calibrateFrom.Batches;
        measurements["probe_counter_overhead_batches"] = overhead;

        // ⛔ ФОН вимірюється, а не припускається нулем. Лічильник
        // `Batch Requests` — на ВЕСЬ інстанс SQL Server: будь-який інший стенд,
        // інтеграційний набір сусіднього worktree або службова задача самого
        // застосунку (черга, PeriodStateJob, IDistributedCache) додаються до
        // нашого числа. Прогін «звернень на PATCH» без цієї поправки
        // приписував би продукту роботу, якої він не робив.
        var (noise, waited) = await WaitForQuietAsync(ct).ConfigureAwait(false);
        measurements["probe_background_batches_per_sec"] = noise;
        measurements["probe_waited_for_quiet_sec"] = waited;

        var readClock = Stopwatch.StartNew();
        var readFrom = await CountersAsync(ct).ConfigureAwait(false);
        for (var i = 0; i < ProbeIterations; i++)
        {
            await session.GetAsync<SliceDto>(SliceUrl(target, table), ct).ConfigureAwait(false);
        }

        var readTo = await CountersAsync(ct).ConfigureAwait(false);
        var readSeconds = readClock.Elapsed.TotalSeconds;

        var writeClock = Stopwatch.StartNew();
        var writeFrom = readTo;
        var random = new Random(20260918);
        for (var i = 0; i < ProbeIterations; i++)
        {
            await PatchAsync(session, target, table, ProbeCells, random, ct).ConfigureAwait(false);
        }

        var writeTo = await CountersAsync(ct).ConfigureAwait(false);
        var writeSeconds = writeClock.Elapsed.TotalSeconds;

        measurements["probe_get_batches_per_op"] =
            ((readTo.Batches - readFrom.Batches) - overhead - (noise * readSeconds)) / ProbeIterations;
        measurements["probe_patch_batches_per_op"] =
            ((writeTo.Batches - writeFrom.Batches) - overhead - (noise * writeSeconds)) / ProbeIterations;
        measurements["probe_patch_transactions_per_op"] =
            (writeTo.Transactions - writeFrom.Transactions) / (double)ProbeIterations;
        measurements["probe_compilations_total"] =
            (readTo.Compilations - readFrom.Compilations) + (writeTo.Compilations - writeFrom.Compilations);

        measurements["merge_plans_after_probe"] = await MergePlansAsync(ct).ConfigureAwait(false);

        if (measurements["probe_patch_batches_per_op"] <= 0)
        {
            notes.Add("Звернень на PATCH вийшло ≤ 0 — фон перевищив сигнал. "
                + "По цій СУБД працює хтось іще; числа звернень НЕДІЙСНІ.");
        }

        if (noise > QuietBatchesPerSecond)
        {
            notes.Add(Fmt($"""
                ⛔ Фон СУБД — {noise:F1} звернень/с, і він ВІДНЯТИЙ від чисел зондування. Лічильник
                    Batch Requests спільний на інстанс, тому по цій базі паралельно працює щось іще
                    (інший стенд, інтеграційний набір, службові задачі). Поправка лінійна, отже числа
                    «звернень на операцію» нижче мають похибку порядку фону; для точного числа
                    потрібен тихий інстанс або лічильник усередині процесу
                    (Ecr.TestKit.SqlClientCommandCounter).
                """));
        }
    }

    /// <summary>
    /// Чекає, поки інстанс СУБД затихне, — але не безкінечно.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Досягнутий фон і скільки секунд на це пішло.</returns>
    /// <remarks>
    /// ⛔ Це не косметика. На цій машині паралельно працюють інші worktree, і
    /// їхні інтеграційні набори дають сплески до 750 звернень/с — на порядок
    /// більше за корисний сигнал. Прогін під таким фоном давав від'ємні
    /// «звернення на операцію»: поправка з'їдала сигнал цілком.
    ///
    /// ⚠ Якщо тиша не настала за <see cref="QuietWaitSeconds"/>, зондування
    /// все одно йде — і числа все одно позначаються недійсними. Чекати вічно
    /// означало б, що замір не можна зняти на спільній машині ніколи.
    /// </remarks>
    private async Task<(double Noise, double WaitedSeconds)> WaitForQuietAsync(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var noise = await BackgroundRateAsync(ct).ConfigureAwait(false);

        while (noise > QuietBatchesPerSecond && clock.Elapsed.TotalSeconds < QuietWaitSeconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            noise = await BackgroundRateAsync(ct).ConfigureAwait(false);
        }

        return (noise, clock.Elapsed.TotalSeconds);
    }

    /// <summary>Фон, за якого зондування ще має сенс, звернень/с.</summary>
    private const double QuietBatchesPerSecond = 25;

    /// <summary>Скільки щонайбільше чекати на тишу, секунд.</summary>
    private const double QuietWaitSeconds = 300;

    /// <summary>
    /// Скільки звернень до СУБД відбувається без нашої участі.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Звернень за секунду фону.</returns>
    /// <remarks>
    /// ⛔ Береться НАЙМЕНШЕ з трьох вікон, а не середнє. Напрям помилки тут
    /// важливіший за її величину: завищений фон віднімається від сигналу й
    /// робить продукт КРАЩИМ, ніж він є, — тобто саме та брехня, заради якої
    /// весь `MS-01` і затіяно. Занижений фон робить продукт гіршим, і це
    /// безпечний бік.
    /// </remarks>
    private async Task<double> BackgroundRateAsync(CancellationToken ct)
    {
        var lowest = double.MaxValue;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var from = await CountersAsync(ct).ConfigureAwait(false);
            var clock = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            var to = await CountersAsync(ct).ConfigureAwait(false);

            // Власне читання лічильників із цього вікна не рахується фоном.
            var batches = Math.Max(0, to.Batches - from.Batches - 1);
            lowest = Math.Min(lowest, batches / clock.Elapsed.TotalSeconds);
        }

        return lowest;
    }

    private static string SliceUrl(Target target, TableUnderLoad table)
        => Fmt($"/api/v1/documents/{target.DocumentId}/tables/{table.InstanceId}");

    /// <summary>
    /// Один <c>PATCH</c> із РІЗНИМИ числами і заданим розміром батчу.
    /// </summary>
    /// <param name="session">Сеанс оператора.</param>
    /// <param name="target">Ціль.</param>
    /// <param name="table">Таблиця під навантаженням.</param>
    /// <param name="cells">Скільки комірок у батчі.</param>
    /// <param name="random">Джерело чисел.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="partition">Номер робітника — його частка рядків.</param>
    /// <param name="partitions">Скільки всього робітників ділять рядки.</param>
    /// <returns>Код відповіді й чи це конфлікт версій.</returns>
    /// <remarks>
    /// ⛔ Числа генеруються з РІЗНИМ масштабом (0…6 знаків). Саме масштаб і
    /// точність <c>SqlClient</c> виводить зі значення, коли параметр оголошено
    /// без <c>Precision</c>/<c>Scale</c> (<c>WR-01</c>,
    /// <c>NormalizedCellStore.cs:588-593</c>) — отже сигнатура запиту, а з нею
    /// і план, змінюється від самих даних. Сталий <c>1m</c> старого режиму цю
    /// ваду ховав повністю.
    ///
    /// ⚠ <c>baseVersion</c> — обов'язковий: без нього <c>ClaimRowsAsync</c> не
    /// виконується (див. <c>GateBenchmark.cs:475</c>), а це перша дія
    /// транзакції запису й одна з тих, чию вартість ми й міряємо.
    /// </remarks>
    private static async Task<PatchOutcome> PatchAsync(
        Session session,
        Target target,
        TableUnderLoad table,
        int cells,
        Random random,
        CancellationToken ct,
        int partition = 0,
        int partitions = 1)
    {
        // ⛔ РОЗДІЛЬНІ рядки на робітника — це не оптимізація, це умова
        // доказовості. Якщо два робітники можуть узяти той самий рядок, то
        // `409` пояснюється звичайним конфліктом версії РЯДКА
        // (`ClaimRowsAsync`), і твердження «це DAT-01» стає безпідставним.
        // За поділу перетину немає за побудовою, отже конфлікт може прийти
        // лише з рівня документа.
        var keys = partitions <= 1
            ? table.RowKeys
            : [.. table.RowKeys.Where((_, i) => i % partitions == partition)];

        if (keys.Length == 0)
        {
            return new PatchOutcome(HttpStatusCode.NoContent, false);
        }

        var perRow = Math.Max(1, Math.Min(table.Columns.Length, cells));
        var rowCount = Math.Max(1, Math.Min(keys.Length, cells / perRow));
        var first = random.Next(keys.Length);

        var rows = new List<object>(rowCount);
        var touched = new List<string>(rowCount);

        for (var r = 0; r < rowCount; r++)
        {
            var key = keys[(first + r) % keys.Length];
            if (!table.Versions.TryGetValue(key, out var version))
            {
                continue;
            }

            touched.Add(key);
            rows.Add(new
            {
                rowKey = key,
                baseVersion = version,
                cells = Enumerable.Range(0, perRow).Select(c => new
                {
                    columnCode = table.Columns[c % table.Columns.Length],
                    value = NextNumber(random),
                }).ToArray(),
            });
        }

        if (rows.Count == 0)
        {
            return new PatchOutcome(HttpStatusCode.NoContent, false);
        }

        var (status, body) = await session.PatchAsync(
            Fmt($"/api/v1/documents/{target.DocumentId}/cells"),
            new
            {
                tableInstanceId = table.InstanceId,
                periodKey = target.PeriodKey,
                origin = "Manual",
                rows,
            },
            ct).ConfigureAwait(false);

        if (status == HttpStatusCode.OK && body?.RowVersions is { } fresh)
        {
            foreach (var (key, version) in fresh)
            {
                table.Versions[key] = version;
            }
        }
        else if (status == HttpStatusCode.Conflict)
        {
            // ⚠ Версії стали несвіжими — перечитати зріз дешевше, ніж мати
            // кожен наступний PATCH цього рядка теж конфліктним і рахувати
            // один дефект багато разів.
            //
            // ⛔ Оновлюються ЛИШЕ рядки цього батчу. Перезапис усього словника
            // клав би прочитану зі зрізу (уже застарілу) версію поверх
            // свіжішої, яку щойно отримав інший робітник, — і той дістав би
            // СПРАВЖНІЙ конфлікт рядка з вини генератора. Замір рахував би
            // власну помилку як дефект продукту.
            await RefreshAsync(session, target, table, touched, ct).ConfigureAwait(false);
        }

        return new PatchOutcome(status, status == HttpStatusCode.Conflict);
    }

    /// <summary>Число з випадковим масштабом — воно й розганяє кеш планів.</summary>
    private static decimal NextNumber(Random random)
        => Math.Round((decimal)(random.NextDouble() * 1_000_000), random.Next(0, 7));

    private static async Task RefreshAsync(
        Session session,
        Target target,
        TableUnderLoad table,
        IReadOnlyCollection<string> only,
        CancellationToken ct)
    {
        var mine = new HashSet<string>(only, StringComparer.Ordinal);
        var slice = await session.GetAsync<SliceDto>(SliceUrl(target, table), ct).ConfigureAwait(false);

        foreach (var row in slice?.Rows ?? [])
        {
            if (mine.Contains(row.RowKey))
            {
                table.Versions[row.RowKey] = row.RowVersion;
            }
        }
    }

    // ── Навантаження ─────────────────────────────────────────────────────────

    /// <summary>
    /// Суміш 80/20 у ОДИН документ кількома операторами, відкрита модель.
    /// </summary>
    /// <param name="sessions">Сеанси операторів.</param>
    /// <param name="target">Ціль.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Зібрані вибірки й лічильники СУБД за вікно.</returns>
    /// <remarks>
    /// ⛔ Модель відкрита, як і в старому режимі: запити ставляться за
    /// розкладом незалежно від того, впорався сервер із попередніми чи ні, і
    /// затримка міряється від ЗАПЛАНОВАНОГО часу. Закрита модель («N потоків
    /// шлють запит за запитом») ніколи не показує зриву.
    ///
    /// ⚠ Робітників помітно більше за цільовий темп: при p95 ~400 мс і 25 RPS
    /// одночасних запитів потрібно щонайменше 10, а на зриві — більше.
    /// </remarks>
    private async Task<LoadOutcome> LoadAsync(Session[] sessions, Target target, CancellationToken ct)
    {
        var reads = new ConcurrentBag<Sample>();
        var writes = new ConcurrentBag<Sample>();
        var statuses = new ConcurrentDictionary<int, int>();
        var conflicts = 0;

        var before = await CountersAsync(ct).ConfigureAwait(false);
        var channel = Channel.CreateUnbounded<Call>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

        using var drain = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var clock = Stopwatch.StartNew();
        var workerCount = Workers > 0 ? Workers : Math.Max(8, TargetRps);

        var workers = Enumerable.Range(0, workerCount)
            .Select(index => Task.Run(
                async () =>
                {
                    // ⛔ Оператор і таблиця прив'язані до РОБІТНИКА, а не до
                    // запиту: інакше той самий рядок правили б різні оператори
                    // впереміш, і 409 означав би справжню конкуренцію за
                    // комірку, а не дефект DAT-01, який ми ловимо.
                    var session = sessions[index % sessions.Length];
                    var table = target.Tables[index % target.Tables.Count];
                    var random = new Random(20260918 + index);

                    try
                    {
                        await foreach (var call in channel.Reader.ReadAllAsync(drain.Token).ConfigureAwait(false))
                        {
                            var startedAt = clock.Elapsed;
                            var status = HttpStatusCode.OK;

                            try
                            {
                                if (call.IsWrite)
                                {
                                    var outcome = await PatchAsync(
                                        session, target, table,
                                        BatchSizes[random.Next(BatchSizes.Length)], random, drain.Token,
                                        partition: index, partitions: workerCount)
                                        .ConfigureAwait(false);

                                    status = outcome.Status;
                                    if (outcome.IsConflict)
                                    {
                                        Interlocked.Increment(ref conflicts);
                                    }
                                }
                                else
                                {
                                    status = await session
                                        .ReadSliceAsync(SliceUrl(target, table), drain.Token)
                                        .ConfigureAwait(false);
                                }
                            }
                            catch (HttpRequestException)
                            {
                                status = HttpStatusCode.ServiceUnavailable;
                            }
                            catch (TaskCanceledException) when (drain.IsCancellationRequested)
                            {
                                break;
                            }

                            var finishedAt = clock.Elapsed;
                            statuses.AddOrUpdate((int)status, 1, (_, n) => n + 1);
                            (call.IsWrite ? writes : reads).Add(new Sample(
                                Math.Max(0, (finishedAt - call.Due).TotalMilliseconds),
                                (finishedAt - startedAt).TotalMilliseconds,
                                finishedAt));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Догін обірвано за часом — очікуваний кінець вікна.
                    }
                },
                CancellationToken.None))
            .ToArray();

        var scheduled = await ProduceAsync(channel.Writer, ct).ConfigureAwait(false);

        var all = Task.WhenAll(workers);
        var grace = TimeSpan.FromSeconds(Math.Min(LoadSeconds, 60));
        if (await Task.WhenAny(all, Task.Delay(grace, ct)).ConfigureAwait(false) != all)
        {
            await drain.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await all.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Очікуваний кінець догону.
        }

        var after = await CountersAsync(ct).ConfigureAwait(false);

        return new LoadOutcome(
            [.. reads], [.. writes], statuses, conflicts, scheduled, before, after,
            await CpuAsync(ct).ConfigureAwait(false),
            await MergePlansAsync(ct).ConfigureAwait(false),
            workerCount);
    }

    private async Task<int> ProduceAsync(ChannelWriter<Call> writer, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var interval = TimeSpan.FromSeconds(1.0 / TargetRps);
        var window = TimeSpan.FromSeconds(LoadSeconds);
        var index = 0;

        while (!ct.IsCancellationRequested)
        {
            var due = interval * index;
            if (due >= window)
            {
                break;
            }

            var wait = due - started.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            var isWrite = WritePercent > 0 && index % (100 / WritePercent) == 0;
            await writer.WriteAsync(new Call(due, isWrite), ct).ConfigureAwait(false);
            index++;
        }

        writer.Complete();
        return index;
    }

    // ── Числа і вирок ────────────────────────────────────────────────────────

    private void Publish(LoadOutcome load, Dictionary<string, double> measurements)
    {
        var completed = load.Reads.Length + load.Writes.Length;
        var elapsed = completed == 0
            ? LoadSeconds
            : Math.Max(LoadSeconds, load.Reads.Concat(load.Writes).Max(s => s.FinishedAt.TotalSeconds));

        measurements["load_seconds"] = LoadSeconds;
        measurements["target_rps"] = TargetRps;
        measurements["workers"] = load.WorkerCount;
        measurements["achieved_rps"] = completed / elapsed;
        measurements["scheduled"] = load.Scheduled;
        measurements["unserved"] = load.Scheduled - completed;

        measurements["get_slice_p95_ms"] = Percentile(load.Reads, 0.95, s => s.ServiceMs);
        measurements["get_slice_p99_ms"] = Percentile(load.Reads, 0.99, s => s.ServiceMs);
        measurements["patch_p95_ms"] = Percentile(load.Writes, 0.95, s => s.ServiceMs);
        measurements["patch_p99_ms"] = Percentile(load.Writes, 0.99, s => s.ServiceMs);
        measurements["get_slice_queued_p95_ms"] = Percentile(load.Reads, 0.95, s => s.LatencyMs);
        measurements["patch_queued_p95_ms"] = Percentile(load.Writes, 0.95, s => s.LatencyMs);

        measurements["conflicts_409"] = load.Conflicts;
        foreach (var (code, count) in load.Statuses)
        {
            measurements[Fmt($"http_{code}")] = count;
        }

        var batches = load.After.Batches - load.Before.Batches;
        measurements["db_batches_under_load"] = batches;
        measurements["db_batches_per_operation"] = completed == 0 ? 0 : batches / (double)completed;
        measurements["db_compilations_under_load"] = load.After.Compilations - load.Before.Compilations;
        measurements["db_recompilations_under_load"] = load.After.Recompilations - load.Before.Recompilations;
        measurements["lock_escalations"] = load.After.Escalations - load.Before.Escalations;
        measurements["merge_plans_after_load"] = load.MergePlans;
        measurements["sql_cpu_percent"] = load.Cpu.Sql;
        measurements["machine_cpu_percent"] = load.Cpu.Machine;
    }

    /// <summary>
    /// Звіряє виміряне з <c>tz/08</c> §8.2 і називає те, чого старий режим не бачив.
    /// </summary>
    /// <param name="load">Результат навантаження.</param>
    /// <param name="measurements">Виміряне.</param>
    /// <param name="failures">Порушення — дають ненульовий код виходу.</param>
    /// <param name="notes">Застереження — друкуються, коду не дають.</param>
    /// <remarks>
    /// ⛔ Фальшиві <c>409</c> — ОКРЕМИЙ критерій, а не нотатка. Це
    /// підтвердження дефекту <c>DAT-01</c> живим заміром, і воно мусить бути
    /// червоним доти, доки <c>TouchAsync</c> не перестане ходити відстежуваною
    /// сутністю. Рівно цього старий режим показати не міг: один потік, одна
    /// таблиця, жодного документа в ланцюзі.
    /// </remarks>
    private void Judge(
        LoadOutcome load,
        Dictionary<string, double> measurements,
        List<string> failures,
        List<string> notes)
    {
        Check(failures, measurements, "get_slice_p95_ms", SliceP95BudgetMs, "Зріз p95");
        Check(failures, measurements, "get_slice_p99_ms", SliceP99BudgetMs, "Зріз p99");
        Check(failures, measurements, "patch_p95_ms", PatchP95BudgetMs, "PATCH p95");
        Check(failures, measurements, "patch_p99_ms", PatchP99BudgetMs, "PATCH p99");
        Check(failures, measurements, "unserved", 0, "Не обслужено запитів вікна");

        if (load.Conflicts > 0)
        {
            failures.Add(Fmt($"""
                ФАЛЬШИВИХ 409 за вікно: {load.Conflicts} із {load.Writes.Length} записів.
                    Кожен робітник правив ВЛАСНУ, ні з ким не спільну підмножину рядків (поділ за
                    індексом у RowKeys), тому конфлікт версії РЯДКА виключений за побудовою — а отже
                    це DAT-01: DocumentStore.TouchAsync (:263-270) зберігає doc.Document відстежуваною
                    сутністю з IsRowVersion (DocumentConfiguration.cs:165), і другий одночасний PATCH
                    у той самий документ відкочується цілком. Старий режим гейта цього не бачив
                    узагалі: один потік, одне сховище, документ у ланцюзі відсутній.
                """));
        }
        else if (load.Writes.Length > 0)
        {
            notes.Add(Fmt($"""
                Фальшивих 409 не спостерігалося ({load.Writes.Length} записів, робітників
                    {load.WorkerCount}). Це НЕ спростування DAT-01. За одного робітника два PATCH
                    не накладаються в часі ЗА ПОБУДОВОЮ, і нуль конфліктів тут — навпаки, доказ
                    того, що конфлікти в паралельному прогоні породжує не генератор.
                    Для висновку про продукт підніміть --workers і --load-rps.
                """));
        }

        var errors = load.Statuses
            .Where(s => s.Key >= 400 && s.Key != (int)HttpStatusCode.Conflict)
            .Sum(s => s.Value);

        if (errors > 0)
        {
            failures.Add(Fmt($"Відповідей 4xx/5xx (крім 409): {errors} — замір ішов частково по відмовах."));
        }

        if (measurements.TryGetValue("merge_plans_after_load", out var plans) && plans > 1)
        {
            // ✎ 2026-09-19: текст переписано під стан ПІСЛЯ `WR-01`. Доти він
            // казав «параметри оголошені без Precision/Scale» — після
            // виправлення це неправда, і примітка гейта почала б звинувачувати
            // вже полагоджене. Примітка, яка бреше, гірша за відсутню: саме
            // цей клас дефекту шукав `W4` §9.
            notes.Add(Fmt($"""
                Планів «MERGE doc.CellValue» у кеші: {plans:F0}.
                    Після WR-01 (типізовані параметри) лишається по одному плану на РОЗМІР БАТЧУ,
                    тобто ~4 при батчах 1/5/25/100 — це очікувано, а не дефект.
                    Помітно більше означає, що сигнатура знову залежить від самих значень:
                    перевір Precision/Scale і довжини рядкових параметрів у NormalizedCellStore/AuditWriter.
                    До одного плану число зводить WR-02 (TVP).
                """));
        }

        if (measurements["stand_saturated"] > 0 && measurements["machine_cpu_percent"] > 70)
        {
            notes.Add(Fmt($"""
                ⛔ Процесор стенда зайнятий на {measurements["machine_cpu_percent"]:F0} % (SQL Server —
                    {measurements["sql_cpu_percent"]:F0} %). Затримки нижче включають чергу до процесора,
                    а не лише роботу системи. Порушення бюджету §8.2 у такому прогоні НЕ є доказом
                    того, що продукт не вкладається в бюджет (Q-145).
                """));
        }
    }

    private static void Check(
        List<string> failures, Dictionary<string, double> measurements, string key, double budget, string what)
    {
        if (measurements.TryGetValue(key, out var value) && value > budget)
        {
            failures.Add(Fmt($"{what}: {value:F0} проти межі {budget:F0}"));
        }
    }

    private static double Percentile(Sample[] samples, double q, Func<Sample, double> pick)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        var sorted = samples.Select(pick).OrderBy(v => v).ToArray();
        return sorted[(int)Math.Floor(q * (sorted.Length - 1))];
    }

    // ── Лічильники СУБД ──────────────────────────────────────────────────────

    /// <summary>Накопичувальні лічильники інстансу — одним зверненням.</summary>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Знімок лічильників.</returns>
    /// <remarks>
    /// ⚠ Одним запитом навмисно: кожне звернення саме потрапляє в
    /// <c>Batch Requests</c>, і три окремі запити зробили б калібрування
    /// втричі більшим за корисний сигнал на малих числах.
    /// </remarks>
    private async Task<Counters> CountersAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT ISNULL(SUM(cntr_value), 0) FROM sys.dm_os_performance_counters
                WHERE counter_name = 'Batch Requests/sec'),
              (SELECT ISNULL(SUM(cntr_value), 0) FROM sys.dm_os_performance_counters
                WHERE counter_name = 'SQL Compilations/sec'),
              (SELECT ISNULL(SUM(cntr_value), 0) FROM sys.dm_os_performance_counters
                WHERE counter_name = 'SQL Re-Compilations/sec'),
              (SELECT ISNULL(SUM(cntr_value), 0) FROM sys.dm_os_performance_counters
                WHERE counter_name LIKE 'Table Lock Escalations%'),
              (SELECT ISNULL(SUM(cntr_value), 0) FROM sys.dm_os_performance_counters
                WHERE counter_name = 'Transactions/sec' AND instance_name = DB_NAME());
            """;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new Counters(0, 0, 0, 0, 0);
        }

        return new Counters(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
    }

    /// <summary>Скільки планів <c>MERGE doc.CellValue</c> лежить у кеші.</summary>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Кількість планів.</returns>
    /// <remarks>
    /// ⛔ Це і є замір <c>WR-01</c> із §3.3: «сотні планів → одиниці». Число
    /// має сенс лише в парі «до/після» на одному стенді — кеш спільний на
    /// інстанс і живе довше за прогін.
    /// </remarks>
    private async Task<double> MergePlansAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;

        // ⚠ Фільтр за базою обов'язковий: кеш планів спільний на інстанс, і без
        // `t.dbid = DB_ID()` у число потрапили б плани сусідніх стендів
        // (EcrDev, EcrTest_*) — тобто чужа робота читалася б як наша вада.
        command.CommandText = """
            SELECT CAST(COUNT(*) AS float)
            FROM sys.dm_exec_cached_plans AS cp
            CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) AS t
            WHERE t.text LIKE '%MERGE doc.CellValue%' AND t.dbid = DB_ID();
            """;

        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Чистить кеш планів ЦІЄЇ бази, щоб рахунок планів щось означав.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Саме <c>DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE</c>, а не
    /// <c>DBCC FREEPROCCACHE</c>: другий чистить кеш ВСЬОГО інстансу й наосліп
    /// сповільнив би чужі стенди й інтеграційні набори на цій же машині.
    ///
    /// ⚠ Наслідок для чисел: перші запити прогону платять за компіляцію. Тому
    /// зондування починається з розігріву, а <c>probe_compilations_total</c>
    /// друкується окремо — щоб було видно, скільки компіляцій припало на замір,
    /// а не ховалося в затримці.
    /// </remarks>
    private async Task ClearPlanCacheAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Завантаження процесора за останні хвилини вікна.</summary>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Відсотки: SQL Server і машина загалом.</returns>
    private async Task<CpuLoad> CpuAsync(CancellationToken ct)
    {
        var minutes = Math.Max(1, (LoadSeconds / 60) + 1);

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = Fmt($"""
            SELECT AVG(CAST(x.SqlCpu AS float)), AVG(CAST(100 - x.Idle AS float))
            FROM (
              SELECT TOP ({minutes})
                     r.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'int') AS SqlCpu,
                     r.value('(./Record/SchedulerMonitorEvent/SystemHealth/SystemIdle)[1]', 'int')         AS Idle,
                     rb.timestamp AS ts
              FROM sys.dm_os_ring_buffers AS rb
              CROSS APPLY (SELECT CONVERT(xml, rb.record)) AS c(r)
              WHERE rb.ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR'
                AND rb.record LIKE '%<SystemHealth>%'
              ORDER BY rb.timestamp DESC) AS x;
            """);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0))
        {
            return new CpuLoad(0, 0);
        }

        return new CpuLoad(reader.GetDouble(0), reader.GetDouble(1));
    }

    private static string Fmt(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    // ── Внутрішні типи ───────────────────────────────────────────────────────

    /// <summary>Один оператор: власні cookie, власний пул з'єднань.</summary>
    private sealed class Session : IDisposable
    {
        private readonly HttpClient _client;
        private readonly HttpClientHandler _handler;
        private readonly CookieContainer _cookies = new();

        public string Name { get; }

        public Session(string name, Uri baseAddress)
        {
            Name = name;
            _handler = new HttpClientHandler
            {
                CookieContainer = _cookies,
                UseCookies = true,
                MaxConnectionsPerServer = 64,
            };

            _client = new HttpClient(_handler)
            {
                BaseAddress = baseAddress,
                Timeout = TimeSpan.FromSeconds(120),
            };
        }

        /// <summary>Вхід; <c>false</c> — відмова, не виняток.</summary>
        /// <param name="userName">Ім'я користувача.</param>
        /// <param name="password">Пароль.</param>
        /// <param name="ct">Токен скасування.</param>
        /// <returns>Чи вдався вхід.</returns>
        /// <remarks>
        /// ⚠ Cookie попереднього сеансу спершу викидається: вхід із чинною
        /// cookie іншого користувача лишив би стару, і наступні запити пішли б
        /// від НЕ ТОГО оператора — а весь сенс заміру в тому, що операторів
        /// кілька.
        ///
        /// ⛔ Саме позначення чинних cookie простроченими, а НЕ заміна
        /// <c>CookieContainer</c>: після першого ж запиту
        /// <c>HttpClientHandler</c> кидає «This instance has already started
        /// one or more requests», і перевидання сеансу після гранта падало б
        /// щоразу.
        /// </remarks>
        public async Task<bool> TryLoginAsync(string userName, string password, CancellationToken ct)
        {
            foreach (Cookie cookie in _cookies.GetAllCookies())
            {
                cookie.Expired = true;
            }


            using var response = await _client
                .PostAsJsonAsync("/api/v1/login/local", new { userName, password }, Json, ct)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }

        public async Task<T?> GetAsync<T>(string url, CancellationToken ct)
        {
            using var response = await _client.GetAsync(new Uri(url, UriKind.Relative), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(Fmt($"GET {url} → {(int)response.StatusCode}"));
            }

            return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Читання зрізу під навантаженням: ПОВЕРТАЄ код, тіло вичитує й викидає.
        /// </summary>
        /// <param name="url">Адреса зрізу.</param>
        /// <param name="ct">Токен скасування.</param>
        /// <returns>Код відповіді.</returns>
        /// <remarks>
        /// ⛔ Тіло вичитується ПОВНІСТЮ, хоч і не розбирається. Зріз 500×60 —
        /// це до 3 МБ нестисненого JSON (<c>RD-01</c>), і замір, який
        /// відпускає відповідь, не дочитавши, міряв би час до ЗАГОЛОВКІВ, а
        /// не час до даних — тобто приховав би рівно ту вартість, через яку
        /// <c>RD-01</c> і з'явився.
        ///
        /// ⚠ Розбір у DTO навмисно НЕ робиться: це вартість генератора, а не
        /// сервера, і на 30 000 комірок вона співмірна з самим запитом.
        /// </remarks>
        public async Task<HttpStatusCode> ReadSliceAsync(string url, CancellationToken ct)
        {
            using var response = await _client
                .GetAsync(new Uri(url, UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            while (await stream.ReadAsync(buffer, ct).ConfigureAwait(false) > 0)
            {
                // Вичитуємо до кінця — саме це й міряється.
            }

            return response.StatusCode;
        }

        public async Task PostAsync(
            string url, object body, CancellationToken ct, HttpStatusCode? tolerate = null)
        {
            using var response = await _client.PostAsJsonAsync(url, body, Json, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode || response.StatusCode == tolerate)
            {
                return;
            }

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(Fmt($"POST {url} → {(int)response.StatusCode}: {text}"));
        }

        public async Task PutAsync(string url, object body, CancellationToken ct)
        {
            using var response = await _client.PutAsJsonAsync(url, body, Json, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpRequestException(Fmt($"PUT {url} → {(int)response.StatusCode}: {text}"));
            }
        }

        /// <summary>
        /// <c>PATCH</c>, що ПОВЕРТАЄ код, а не кидає.
        /// </summary>
        /// <param name="url">Адреса.</param>
        /// <param name="body">Тіло.</param>
        /// <param name="ct">Токен скасування.</param>
        /// <returns>Код відповіді й розібране тіло на <c>200</c>.</returns>
        /// <remarks>
        /// ⛔ <c>409</c> тут — РЕЗУЛЬТАТ заміру (дефект <c>DAT-01</c>), а не
        /// збій прогону. Виняток на ньому втратив би саме те, заради чого
        /// піднято двох операторів.
        /// </remarks>
        public async Task<(HttpStatusCode Status, PatchResponseDto? Body)> PatchAsync(
            string url, object body, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = JsonContent.Create(body, options: Json),
            };

            using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return (response.StatusCode, null);
            }

            var parsed = await response.Content
                .ReadFromJsonAsync<PatchResponseDto>(Json, ct).ConfigureAwait(false);

            return (response.StatusCode, parsed);
        }

        public void Dispose()
        {
            _client.Dispose();
            _handler.Dispose();
        }
    }

    private sealed record Target(
        long DocumentId, int PeriodKey, IReadOnlyList<TableUnderLoad> Tables, string? Blocked)
    {
        public static Target Stop(string why) => new(0, 0, [], why);
    }

    /// <summary>Таблиця під навантаженням.</summary>
    /// <param name="InstanceId">Екземпляр таблиці.</param>
    /// <param name="Columns">Редаговані числові колонки.</param>
    /// <param name="Cells">Розмір зрізу в комірках.</param>
    /// <param name="RowKeys">Ключі рядків у СТАЛОМУ порядку — основа поділу між робітниками.</param>
    /// <param name="Versions">Останні відомі версії рядків.</param>
    private sealed record TableUnderLoad(
        long InstanceId,
        string[] Columns,
        int Cells,
        string[] RowKeys,
        ConcurrentDictionary<string, string> Versions);

    private sealed record Call(TimeSpan Due, bool IsWrite);

    private sealed record Sample(double LatencyMs, double ServiceMs, TimeSpan FinishedAt);

    private sealed record PatchOutcome(HttpStatusCode Status, bool IsConflict);

    private sealed record Counters(
        long Batches, long Compilations, long Recompilations, long Escalations, long Transactions);

    private sealed record CpuLoad(double Sql, double Machine);

    private sealed record LoadOutcome(
        Sample[] Reads,
        Sample[] Writes,
        ConcurrentDictionary<int, int> Statuses,
        int Conflicts,
        int Scheduled,
        Counters Before,
        Counters After,
        CpuLoad Cpu,
        double MergePlans,
        int WorkerCount);

    private sealed record RoleDto(int Id, string Code);

    private sealed record PagedDto<T>(IReadOnlyList<T> Items);

    private sealed record ProjectDto(int Id);

    private sealed record DocumentDto(long Id);

    private sealed record CalendarDto(IReadOnlyList<PeriodDto> Periods);

    private sealed record PeriodDto(int PeriodKey, string State);

    private sealed record DocumentTableDto(long TableInstanceId);

    private sealed record SliceDto(IReadOnlyList<SliceColumnDto> Columns, IReadOnlyList<SliceRowDto> Rows);

    private sealed record SliceColumnDto(string Code, string? DataType, bool IsReadOnly);

    private sealed record SliceRowDto(string RowKey, string RowVersion);

    private sealed record PatchResponseDto(IReadOnlyDictionary<string, string> RowVersions);
}
