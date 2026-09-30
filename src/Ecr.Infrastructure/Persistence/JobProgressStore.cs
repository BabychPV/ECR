using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IJobProgressStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class JobProgressStore(EcrDbContext db) : IJobProgressStore
{
    /// <summary>Спроб загалом: перша + <see cref="Jobs.QuartzJobAdapter.MaxRetryAttempts"/> ретраїв (BE-08).</summary>
    private const int MaxAttempts = Jobs.QuartzJobAdapter.MaxRetryAttempts + 1;

    /// <summary>Роль процесу Api (HTTP-застосунок) в <see cref="JobProgress.InstanceId"/>.</summary>
    public const string RoleApi = "api";

    /// <summary>Роль процесу-воркера (виконавця черги задач) в <see cref="JobProgress.InstanceId"/>.</summary>
    public const string RoleWorker = "wrk";

    /// <summary>Фіксований перелік ролей. Нова роль — лише новою константою тут.</summary>
    public static IReadOnlyList<string> Roles { get; } = [RoleApi, RoleWorker];

    /// <summary>Довжина ролі — рівно три символи для КОЖНОЇ ролі.</summary>
    private const int RoleLength = 3;

    /// <summary>Скільки символів імені машини йде в <see cref="JobProgress.InstanceId"/>.</summary>
    /// <remarks>
    /// 27 + «/» + 3 (роль) + «/» + 32 (GUID «N») = 64 — рівно межа стовпця
    /// (<c>nvarchar(64)</c>, <see cref="JobProgress.MaxInstanceIdLength"/>). ⚠ Було 31
    /// до ролі (P3, ФВ-9.8): чотири символи віддано ролі й роздільнику. NetBIOS-ім'я
    /// Windows — до 15 символів, тож скорочення зачіпає хіба довгі DNS-імена Linux.
    /// </remarks>
    private const int MaxMachineNameLength = 27;

    /// <summary>Скільки перших символів довгого імені лишається перед «~» і хешем.</summary>
    /// <remarks>18 + «~» + 8 hex = 27 = <see cref="MaxMachineNameLength"/>.</remarks>
    private const int HashedNameHeadLength = 18;

    /// <summary>Скільки hex-символів хешу повного імені йде в скорочене ім'я.</summary>
    private const int HashedNameHashLength = 8;

    /// <summary>Межа імені машини в рядках СТАРОГО формату <c>{машина}/{GUID}</c> (до P3).</summary>
    private const int LegacyMaxMachineNameLength = 31;

    /// <summary>Довжина GUID у форматі «N».</summary>
    private const int GuidLength = 32;

    /// <summary>Повне ім'я цієї машини (як його повертає ОС) — для <see cref="FailPreviousInstanceAsync"/>.</summary>
    public static string CurrentHostName { get; } = Environment.MachineName;

    /// <summary>Ім'я цієї машини в тому вигляді, в якому воно стоїть в <see cref="JobProgress.InstanceId"/>.</summary>
    public static string CurrentMachineName { get; } = MachineNameOf(CurrentHostName);

    /// <summary>GUID цього процесу — генерується раз на старті.</summary>
    /// <remarks>
    /// ⚠ GUID, а не PID: Windows повторно видає PID, і новий процес із PID
    /// попереднього не відрізнив би його рядки від своїх.
    /// </remarks>
    private static readonly Guid ProcessGuid = Guid.NewGuid();

    private static readonly Lock RoleGate = new();
    private static string currentRole = RoleApi;
    private static string? currentInstanceId;

    /// <summary>Роль цього процесу; за замовчуванням <see cref="RoleApi"/>.</summary>
    public static string CurrentRole
    {
        get
        {
            lock (RoleGate)
            {
                return currentRole;
            }
        }
    }

    /// <summary>
    /// Ідентифікатор ЦЬОГО процесу: <c>{машина}/{роль}/{GUID}</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Роль — частина ідентифікатора (P3): Api й воркер на ОДНОМУ хості інакше
    /// мали б той самий префікс машини, і старт Api закривав би
    /// (<see cref="FailPreviousInstanceAsync"/>) живі задачі воркера.
    /// ⚠ Перше читання фіксує роль: рядки вже позначено нею, і зміна ролі після
    /// цього розщепила б процес на два «інстанси» (<see cref="UseRole"/> кидає).
    /// </remarks>
    public static string CurrentInstanceId
    {
        get
        {
            lock (RoleGate)
            {
                return currentInstanceId ??= InstanceIdOf(CurrentMachineName, currentRole, ProcessGuid);
            }
        }
    }

    /// <summary>
    /// Задає роль процесу. Викликати на старті хоста ДО першої задачі
    /// (воркер — <see cref="RoleWorker"/>; Api нічого не кличе — роль за замовчуванням).
    /// </summary>
    /// <param name="role">Роль із <see cref="Roles"/>.</param>
    /// <exception cref="ArgumentException">Роль не з переліку.</exception>
    /// <exception cref="InvalidOperationException">Ідентифікатор уже видано з іншою роллю.</exception>
    public static void UseRole(string role)
    {
        EnsureKnownRole(role);

        lock (RoleGate)
        {
            if (string.Equals(role, currentRole, StringComparison.Ordinal))
            {
                return;
            }

            if (currentInstanceId is not null)
            {
                throw new InvalidOperationException(
                    $"Роль процесу вже зафіксовано як «{currentRole}» (ідентифікатор {currentInstanceId} видано): «{role}» задавати треба до першої задачі.");
            }

            currentRole = role;
        }
    }

    /// <summary>
    /// Ім'я машини для <see cref="JobProgress.InstanceId"/>: до 27 символів — як є;
    /// довше — перші 18 символів, «~» і 8 hex SHA-256 ПОВНОГО імені (разом 27).
    /// </summary>
    /// <param name="machineName">Повне ім'я.</param>
    /// <remarks>
    /// ⛔ Не просте обрізання: хости ферми з довгими FQDN (<c>…-node-01</c>,
    /// <c>…-node-02</c>) мають спільні перші 27 символів, і обрізане ім'я
    /// збігалося б — старт одного закривав би задачі іншого як «попереднього
    /// процесу цієї машини». Хеш — SHA-256 (детермінований між процесами й
    /// перезапусками), а не <c>string.GetHashCode</c>, рандомізований на процес.
    /// Функція ідемпотентна: нормалізоване ім'я не довше 27 і повертається як є.
    /// </remarks>
    public static string MachineNameOf(string machineName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);

        if (machineName.Length <= MaxMachineNameLength)
        {
            return machineName;
        }

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(machineName));
        var hex = Convert.ToHexStringLower(hash)[..HashedNameHashLength];

        return $"{machineName[..HashedNameHeadLength]}~{hex}";
    }

    /// <summary>Ім'я машини в рядках старого формату (до P3): перші 31 символ повного імені.</summary>
    /// <param name="machineName">Повне ім'я.</param>
    public static string LegacyMachineNameOf(string machineName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);
        return machineName.Length <= LegacyMaxMachineNameLength ? machineName : machineName[..LegacyMaxMachineNameLength];
    }

    /// <summary>Ідентифікатор процесу: <c>{машина}/{роль}/{GUID N}</c>.</summary>
    /// <param name="machineName">Ім'я машини (вже обрізане, <see cref="MachineNameOf"/>).</param>
    /// <param name="role">Роль із <see cref="Roles"/>.</param>
    /// <param name="process">GUID процесу.</param>
    public static string InstanceIdOf(string machineName, string role, Guid process)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(machineName.Length, MaxMachineNameLength, nameof(machineName));
        EnsureKnownRole(role);
        return $"{machineName}/{role}/{process:N}";
    }

    private static void EnsureKnownRole(string role)
    {
        if (role is null || role.Length != RoleLength || !Roles.Contains(role, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Невідома роль процесу «{role}»: лише {string.Join(", ", Roles)}.", nameof(role));
        }
    }

    /// <inheritdoc />
    public Task QueueAsync(
        string jobId, string jobCode, DateTime utcNow, CancellationToken ct, int? createdByUserId = null,
        string? correlationId = null, long? documentId = null)
        => UpsertAsync(
            jobId, jobCode, utcNow, entry => entry.Queue(utcNow, correlationId, documentId, CurrentInstanceId),
            createdByUserId, ct);

    /// <inheritdoc />
    public Task StartAsync(
        string jobId, string jobCode, DateTime utcNow, CancellationToken ct, int attempt = 1,
        string? correlationId = null)
        => UpsertAsync(
            jobId, jobCode, utcNow, entry => entry.Begin(utcNow, attempt, correlationId, CurrentInstanceId),
            createdByUserId: null, ct);

    /// <summary>Створює або оновлює запис прогресу.</summary>
    /// <remarks>
    /// Повторний виклик із тим самим ідентифікатором — це перезапуск після
    /// збою, а не друга задача: запис оновлюється, а не дублюється.
    ///
    /// ⚠ <paramref name="createdByUserId"/> зберігається ЛИШЕ при створенні
    /// нового запису (Q-156). Перезапуск після збою (<c>StartAsync</c> на
    /// вже наявний запис) не передає автора — і не повинен: автор уже
    /// записаний першим <c>QueueAsync</c>, а другий виклик його б стер.
    /// </remarks>
    private async Task UpsertAsync(
        string jobId, string jobCode, DateTime utcNow, Action<JobProgress> apply, int? createdByUserId,
        CancellationToken ct)
    {
        var entry = await db.JobProgresses
            .FirstOrDefaultAsync(p => p.JobId == jobId, ct)
            .ConfigureAwait(false);

        if (entry is null)
        {
            entry = new JobProgress(jobId, jobCode, utcNow, createdByUserId);
            db.JobProgresses.Add(entry);
        }

        apply(entry);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReportAsync(
        string jobId, int percent, string? message, DateTime utcNow, CancellationToken ct)
    {
        var entry = await db.JobProgresses
            .FirstOrDefaultAsync(p => p.JobId == jobId, ct)
            .ConfigureAwait(false);

        // Прогрес задачі, якої немає в журналі, — не привід падати: сама
        // задача від цього не стає менш корисною.
        if (entry is null)
        {
            return;
        }

        entry.Report(percent, message, utcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task FinishAsync(
        string jobId, string state, string? errorMessage, DateTime utcNow, CancellationToken ct,
        string? errorCode = null)
    {
        var entry = await db.JobProgresses
            .FirstOrDefaultAsync(p => p.JobId == jobId, ct)
            .ConfigureAwait(false);

        if (entry is null)
        {
            return;
        }

        entry.Finish(state, errorMessage, utcNow, errorCode);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RestartAsync(string jobId, DateTime utcNow, CancellationToken ct)
    {
        var entry = await db.JobProgresses
            .FirstOrDefaultAsync(p => p.JobId == jobId, ct)
            .ConfigureAwait(false);

        if (entry is null)
        {
            return false;
        }

        // Ручний перезапуск ставить задачу в чергу ЦЬОГО процесу.
        entry.Queue(utcNow, instanceId: CurrentInstanceId);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return true;
    }

    /// <inheritdoc />
    public async Task<JobStatus?> FindAsync(string jobId, CancellationToken ct)
        => await db.JobProgresses
            .AsNoTracking()
            .Where(p => p.JobId == jobId)
            .Select(p => new JobStatus(
                p.JobId, p.State, p.Percent, p.Message, p.Error, p.Attempt, p.CorrelationId, MaxAttempts,
                p.CreatedAt, p.ErrorCode, p.DocumentId))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int?> GetCreatedByUserIdAsync(string jobId, CancellationToken ct)
        => await db.JobProgresses
            .AsNoTracking()
            .Where(p => p.JobId == jobId)
            .Select(p => (int?)p.CreatedByUserId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobSummary>> ListRecentAsync(
        JobListFilter filter, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = db.JobProgresses.AsNoTracking();

        // ⛔ Три предикати, і кожен — КОН'ЮНКЦІЯ з рештою. «Мої провалені»
        // мусить означати саме це, а не «мої або провалені»: об'єднання
        // віддало б чужі задачі тому, хто права на них не має.
        //
        // ⚠ Автор — це `filter.CreatedByUserId`, який обробник бере з
        // `ICurrentUser`. Жодного шляху сюди з рядка запиту немає за
        // побудовою: тип не має іншого джерела.
        if (filter.CreatedByUserId is { } author)
        {
            query = query.Where(p => p.CreatedByUserId == author);
        }

        if (filter.State is { Length: > 0 } state)
        {
            query = query.Where(p => p.State == state);
        }

        if (filter.JobCode is { Length: > 0 } code)
        {
            query = query.Where(p => p.JobCode == code);
        }

        // ⚠ Лівий join: системна задача (автор null) чи видалений автор
        // лишаються в переліку з CreatedByDisplayName = null.
        return await query
            .OrderByDescending(p => p.UpdatedAt)
            .Take(limit)
            .Select(p => new JobSummary(
                p.JobId, p.JobCode, p.State, p.Percent, p.UpdatedAt, p.StartedAt, p.Attempt, p.CorrelationId,
                db.Users.Where(u => u.Id == p.CreatedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
                p.Message, p.CreatedAt, p.ErrorCode, p.DocumentId, MaxAttempts, null, p.CreatedByUserId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> HeartbeatAsync(string jobId, DateTime utcNow, CancellationToken ct)
        // ⚠ Точковий UPDATE, а не завантаження сутності: биття трапляється
        // кожні 30 секунд на КОЖНУ активну задачу, і читати заради нього цілий
        // рядок означало б платити двома запитами за один запис одного поля.
        //
        // ⚠ Фільтр за станом обов'язковий: биття, яке спізнилося й прийшло
        // після `FinishAsync`, інакше воскресило б ознаку життя на вже
        // завершеній задачі.
        => await db.JobProgresses
            .Where(p => p.JobId == jobId && (p.State == "Running" || p.State == "Queued"))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.HeartbeatAt, utcNow), ct)
            .ConfigureAwait(false) > 0;

    /// <summary>Скільки ідентифікаторів в одному <c>IN (…)</c>.</summary>
    /// <remarks>
    /// ⚠ Порціями: черга інстанса зазвичай — одиниці задач, але <c>Contains</c>
    /// без межі на тисячі значень дає план, який SQL Server не кешує, і
    /// впирається в стелю параметрів (2100).
    /// </remarks>
    private const int KeepAliveChunk = 500;

    /// <inheritdoc />
    public async Task<int> KeepAliveAsync(
        IReadOnlyCollection<string> jobIds, DateTime utcNow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jobIds);

        var touched = 0;

        foreach (var chunk in jobIds.Chunk(KeepAliveChunk))
        {
            // ⚠ Той самий фільтр стану, що в `HeartbeatAsync`: дурабельна
            // деталь провалу теж лежить у локальному планувальнику, і воскресити
            // їй биття означало б нічого — але шум у кожному прогоні.
            touched += await db.JobProgresses
                .Where(p => chunk.Contains(p.JobId) && (p.State == "Running" || p.State == "Queued"))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.HeartbeatAt, utcNow), ct)
                .ConfigureAwait(false);
        }

        return touched;
    }

    /// <inheritdoc />
    public async Task<bool> CancelActiveAsync(string jobId, DateTime utcNow, CancellationToken ct)
        // ⚠ Умовний UPDATE, а не завантаження й `Finish`: між читанням і записом
        // задача могла завершитися сама, і беззастережний запис переписав би
        // справжній результат на «скасовано». Поля — ті самі, що ставить
        // `JobProgress.Finish(state, error: null)`: без помилки, відсоток 100.
        => await db.JobProgresses
            .Where(p => p.JobId == jobId && (p.State == "Running" || p.State == "Queued"))
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(p => p.State, "Cancelled")
                    .SetProperty(p => p.Error, (string?)null)
                    .SetProperty(p => p.ErrorCode, (string?)null)
                    .SetProperty(p => p.Percent, 100)
                    .SetProperty(p => p.UpdatedAt, utcNow),
                ct)
            .ConfigureAwait(false) > 0;

    /// <inheritdoc />
    public async Task<StaleJobsSummary> SummarizeStaleAsync(DateTime utcNow, CancellationToken ct)
    {
        var threshold = utcNow - IJobProgressStore.StaleAfter;

        // ⚠ Той самий предикат, що в `FailStaleAsync`, — інакше health показував
        // би одне, а прибирання робило б інше. Індекс `IX_JobProgress_Stale`
        // (State, HeartbeatAt) покриває його повністю.
        var stale = db.JobProgresses
            .AsNoTracking()
            .Where(p => (p.State == "Running" || p.State == "Queued")
                        && p.Lane == null
                        && (p.HeartbeatAt == null || p.HeartbeatAt < threshold));

        var count = await stale.CountAsync(ct).ConfigureAwait(false);
        if (count == 0)
        {
            return new StaleJobsSummary(0, null);
        }

        var oldest = await stale.MinAsync(p => p.HeartbeatAt, ct).ConfigureAwait(false);

        return new StaleJobsSummary(count, oldest);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Пошук ключа в лапках: конверт — компактний JSON (<c>JobProgressMessageCodec</c>),
    /// і ключ каталогу в ньому — рядкове значення. <c>Failed</c> за 30 діб зберігання —
    /// небагато рядків, шукаються за префіксом <c>IX_JobProgress_Stale</c> (State).
    /// </remarks>
    public Task<int> CountFailedWithMessageKeyAsync(string messageKey, DateTime sinceUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageKey);
        var quoted = "\"" + messageKey + "\"";

        return db.JobProgresses
            .AsNoTracking()
            .CountAsync(
                p => p.State == "Failed" && p.UpdatedAt >= sinceUtc && p.Message != null && p.Message.Contains(quoted),
                ct);
    }

    /// <inheritdoc />
    public async Task<int> PurgeFinishedAsync(DateTime olderThan, int batch, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);

        // ⚠ Предикат спирається на `IX_JobProgress_Stale` (State, HeartbeatAt):
        // биття завершеної задачі не пізніше за її завершення, тож
        // `HeartbeatAt < межа` — пошук за індексом, а не повний перегляд
        // таблиці, яка саме через відсутність прибирання й виросла.
        //
        // ⛔ `UpdatedAt < межа` — друга умова, не надмірність. Прибирання
        // покинутих закриває рядок із давнім биттям СЬОГОДНІ; без цієї умови
        // щойно закритий провал зникав би раніше, ніж його хтось побачив.
        //
        // ⚠ Порцією (`TOP`): видалення сотень тисяч рядків одним запитом
        // тримало б блокування на всій таблиці, яку в цю мить опитують екрани.
        var victims = db.JobProgresses
            .Where(p => (p.State == "Succeeded" || p.State == "Failed" || p.State == "Cancelled")
                        && (p.HeartbeatAt == null || p.HeartbeatAt < olderThan)
                        && p.UpdatedAt < olderThan)
            .OrderBy(p => p.HeartbeatAt)
            .Take(batch)
            .Select(p => p.JobId);

        return await db.JobProgresses
            .Where(p => victims.Contains(p.JobId))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> FailStaleAsync(string reason, DateTime utcNow, CancellationToken ct)
    {
        // ⛔ Поріг рахується ОДИН раз, до вибірки. Обчислення його всередині
        // циклу зробило б межу рухомою: задача, що почалася поки прибирання
        // йде, могла б потрапити під пізніший, зсунутий поріг.
        var threshold = utcNow - IJobProgressStore.StaleAfter;

        // ⛔ Раніше тут не було предиката взагалі — валився КОЖЕН рядок
        // `Running`/`Queued`. Інстанс не один, і перезапуск сусіда вбивав
        // чужу живу роботу. Покинута задача — це та, чиє биття застигло:
        // процес, який упав, не пише нічого.
        //
        // ⚠ `HeartbeatAt == null` — рядок старший за міграцію, що додала
        // колонку. Процес, який його створив, зупинявся заради розгортання
        // цієї ж міграції, тож він гарантовано мертвий.
        //
        // ⛔ `Lane == null` (P3, контракт черги §1): рядок черги в базі
        // (`Lane IS NOT NULL`) закриває ЛИШЕ прострочена оренда (`DbJobQueue`) —
        // його биття пише власник оренди, і «застигле» биття тут не означає
        // «покинута»: оренду ще можуть перехопити й довиконати.
        var candidates = await db.JobProgresses
            .AsNoTracking()
            .Where(p => (p.State == "Running" || p.State == "Queued")
                        && p.Lane == null
                        && (p.HeartbeatAt == null || p.HeartbeatAt < threshold))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var failed = 0;

        foreach (var entry in candidates)
        {
            // ⛔ `Finish` — доменний метод, і правила завершення (запис
            // `Error`, поведінка `Percent`) лишаються ЛИШЕ в ньому: тут він
            // викликається на відчепленій сутності саме щоб обчислити значення,
            // а не щоб продублювати логіку в SQL.
            entry.Finish("Failed", reason, utcNow);

            // ⛔ Умова застарілості ПОВТОРЮЄТЬСЯ в WHERE запису, і це не
            // надмірність. Між вибіркою вище й записом сюди задачу могли
            // перезапустити вручну (`RestartAsync`) або її власник міг
            // прокинутися й ударити — тоді рядок уже не застарілий, і
            // беззастережний UPDATE відтворив би той самий дефект у
            // мікроскопічному вікні. SQL Server перевіряє цю умову й пише
            // атомарно, тож вікна не лишається зовсім.
            var affected = await db.JobProgresses
                .Where(p => p.JobId == entry.JobId
                            && (p.State == "Running" || p.State == "Queued")
                            && p.Lane == null
                            && (p.HeartbeatAt == null || p.HeartbeatAt < threshold))
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(p => p.State, entry.State)
                        .SetProperty(p => p.Error, entry.Error)
                        .SetProperty(p => p.Percent, entry.Percent)
                        .SetProperty(p => p.UpdatedAt, entry.UpdatedAt),
                    ct)
                .ConfigureAwait(false);

            failed += affected;
        }

        // ⚠ Повертається кількість РЕАЛЬНО записаних рядків, а не розмір
        // вибірки: число йде в лог старту, і завищене означало б розслідування
        // задач, яких ніхто не валив.
        return failed;
    }

    /// <inheritdoc />
    public async Task<int> FailPreviousInstanceAsync(
        string machineName, string role, string currentInstanceId, string reason, DateTime utcNow,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentInstanceId);
        EnsureKnownRole(role);

        // ⚠ `machineName` — ПОВНЕ ім'я хоста (`CurrentHostName`); нормалізація
        // (`MachineNameOf`) ідемпотентна, тож уже нормалізоване ім'я теж годиться —
        // тоді лише рядки старого формату довгого імені не впізнаються (їх
        // закриє `FailStaleAsync` за віком биття — безпечний бік).
        var normalized = MachineNameOf(machineName);

        // ⛔ Префікс — машина І роль (P3). Без ролі старт Api на хості воркера
        // закривав би живі задачі воркер-процесів (вони теж «цієї машини»).
        var prefix = $"{normalized}/{role}/";
        if (!currentInstanceId.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Ідентифікатор «{currentInstanceId}» не належить машині «{normalized}» і ролі «{role}».",
                nameof(currentInstanceId));
        }

        // ⛔ Биття тут НЕ перевіряється, і саме в цьому сенс: попередній процес
        // цієї машини й ролі мертвий (його чергу в пам'яті Quartz втрачено),
        // навіть якщо встиг ударити секунду тому. Рядки інших машин і інших
        // ролей — не тут: їхню живість видно лише з биття (`FailStaleAsync`).
        //
        // ⛔ `Lane == null`: рядок черги в базі закриває лише прострочена оренда
        // (контракт черги §1) — перехоплена оренда довиконає його.
        var query = db.JobProgresses
            .Where(p => (p.State == "Running" || p.State == "Queued")
                        && p.Lane == null
                        && p.InstanceId != null
                        && p.InstanceId != currentInstanceId);

        if (string.Equals(role, RoleApi, StringComparison.Ordinal))
        {
            // ⚠ Зворотна сумісність: рядки СТАРОГО формату `{машина}/{GUID}` (до
            // P3) писав лише Api — воркера тоді не існувало. Старий формат —
            // рівно `{перші 31 символ повного імені}/{GUID N}`: точний префікс і
            // точна довжина, один «/» (`NOT LIKE '%/%/%'`).
            //
            // ⚠ Колізія старого формату ЛИШАЄТЬСЯ: два хости зі спільними першими
            // 31 символом мають однаковий старий префікс, і перший старт нової Api
            // на одному закриє рядки старого формату іншого. Вона одноразова —
            // рядки старого формату пишуть лише процеси ДО оновлення, після
            // першого перезапуску нових таких рядків не виникає.
            var legacyPrefix = LegacyMachineNameOf(machineName) + "/";
            var legacyLength = legacyPrefix.Length + GuidLength;

            query = query.Where(p => p.InstanceId!.StartsWith(prefix)
                                     || (p.InstanceId!.StartsWith(legacyPrefix)
                                         && !EF.Functions.Like(p.InstanceId!, "%/%/%")
                                         && p.InstanceId!.Length == legacyLength));
        }
        else
        {
            query = query.Where(p => p.InstanceId!.StartsWith(prefix));
        }

        // ⚠ Поля — ті самі, що пише `JobProgress.Finish("Failed", reason)`
        // (Percent не чіпає), і умова «досі активний» — у самому UPDATE: задачу
        // могли перезапустити в цьому процесі між рядком і записом.
        return await query
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(p => p.State, "Failed")
                    .SetProperty(p => p.Error, reason)
                    .SetProperty(p => p.ErrorCode, (string?)null)
                    .SetProperty(p => p.UpdatedAt, utcNow),
                ct)
            .ConfigureAwait(false);
    }
}
