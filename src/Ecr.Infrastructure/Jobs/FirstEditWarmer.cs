using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Validation;
using Ecr.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// AN-194: прогрів ПЕРШОГО читання зрізу таблиці (<c>GET …/tables/{id}</c>) і ПЕРШОГО <c>PATCH …/cells</c> після старту
/// процесу Api — те, чого не покриває <see cref="EditPathWarmer"/> (AN-190): компіляція EF-запитів сховищ рядків,
/// комірок, періодів, шапки, оформлення, і JIT коду обробників <see cref="GetTableSliceHandler"/> /
/// <see cref="PatchCellsHandler"/> разом із їхніми сховищами.
/// </summary>
/// <remarks>
/// ⛔ Межі, і чому саме такі:
/// <list type="bullet">
/// <item><b>Жодного значення комірки й жодного рішення доступу.</b> EF кешує скомпільований запит за формою виразу, а не
/// за значеннями параметрів, тож кожен порт кличеться з ідентифікаторами, яких не буває (<see cref="NoId"/>): запит
/// компілюється й виконується, рядків не повертає. Рішень доступу немає: профіль справжнього користувача прогрів не
/// будує (це вже не прогрів, а імперсонація); зі служби доступу кличуться тільки «проєкт документа» і профіль
/// облікового запису <see cref="NoId"/>, якого немає (відмова до кешу профілів).</item>
/// <item>Лише ЧИТАННЯ: жодного <c>MERGE</c>/<c>UPDATE</c>, блокувань аркуша, постановки задач чи журналу аудиту;
/// транзакція — порожня, з одним <c>SELECT</c> над табличним параметром (без таблиць).</item>
/// <item>Метадані версій (оформлення, умовне форматування) — лише версій, які вже прогрів AN-190 (відкриті періоди),
/// у той самий кеш, що наповнив би перший користувач.</item>
/// <item>Кожен крок — окремо: збій кроку лише в лічильник і журнал, далі наступний; скасування — зупинкою хоста.</item>
/// </list>
/// </remarks>
public sealed partial class FirstEditWarmer(IServiceScopeFactory scopes, ILogger<FirstEditWarmer> logger)
{
    /// <summary>Ключ вмикання прогріву (типово <c>true</c>).</summary>
    public const string EnabledKey = "Startup:WarmFirstEdit";

    /// <summary>Ідентифікатор, якого не буває в жодній таблиці (усі ідентифікатори — додатні).</summary>
    public const int NoId = -1;

    /// <summary>Дійсний ключ періоду для запитів-«порожняків»: з <see cref="NoId"/> він однаково нічого не знаходить.</summary>
    internal static readonly PeriodKey NoPeriod = new(209912);

    /// <summary>Чи увімкнено прогрів (<see cref="EnabledKey"/>, типово <c>true</c>).</summary>
    /// <param name="configuration">Конфігурація процесу.</param>
    public static bool ReadEnabled(IConfiguration configuration)
        => configuration.GetValue(EnabledKey, defaultValue: true);

    /// <summary>
    /// Простори імен, код яких JIT-компілюється заздалегідь: обробники зрізу й запису, доступ, валідація, перегляд
    /// формул (Application), служби доступу й кеші (Infrastructure). Замір AN-194 (лічильник JIT першого зрізу й
    /// першого <c>PATCH</c>): саме ці простори давали найбільше методів, скомпільованих на запиті користувача.
    /// </summary>
    public static IReadOnlyList<string> JitNamespaces { get; } =
    [
        "Ecr.Application.Documents",
        "Ecr.Application.Security",
        "Ecr.Application.Validation",
        "Ecr.Application.Recalculation",
        "Ecr.Infrastructure.Security",
        "Ecr.Infrastructure.Caching",
    ];

    /// <summary>
    /// Окремі типи поза <see cref="JitNamespaces"/> (каталог <c>Persistence</c> завеликий — разом із міграціями): сховища
    /// шляху зрізу й запису.
    /// </summary>
    public static IReadOnlyList<Type> JitTypes { get; } =
    [
        typeof(ValidationEngine),
        typeof(Persistence.RowStore),
        typeof(Persistence.NormalizedCellStore),
        typeof(Persistence.MonthRoutedCellStore),
        typeof(Persistence.SheetEditGate),
        typeof(Persistence.LockOrder),
        typeof(Persistence.LockWaitGuard),
        typeof(Persistence.AuditWriter),
        typeof(Persistence.AuditReader),
    ];

    /// <summary>Кроки прогріву: ім'я → дія над провайдером scope. Тести підміняють.</summary>
    public IReadOnlyList<(string Name, Func<IServiceProvider, IReadOnlyList<int>, CancellationToken, Task> Run)> Steps { get; init; }
        = DefaultSteps;

    /// <summary>Типові кроки: порти зрізу й запису, кожен — з ідентифікаторами, яких не буває.</summary>
    public static IReadOnlyList<(string Name, Func<IServiceProvider, IReadOnlyList<int>, CancellationToken, Task> Run)> DefaultSteps { get; } =
    [
        ("rows", WarmRowsAsync),
        ("cells", WarmCellsAsync),
        ("periods", WarmPeriodsAsync),
        ("header", (sp, _, ct) => sp.GetRequiredService<IDocumentHeaderStore>().GetExpressionValuesAsync(NoId, ct)),
        ("units", (sp, _, ct) => sp.GetRequiredService<IUnitCatalog>().GetAsync(ct)),
        ("methodologies", (sp, _, ct) => sp.GetRequiredService<IMethodologyStore>().GetMethodologyIdsBoundToTableAsync(NoId, ct)),
        ("styles", WarmVersionStylesAsync),
        ("overlays", WarmOverlaysAsync),
        ("permits", (sp, _, ct) => sp.GetService<IPermitRegistryIndex>() is { } index ? index.PermitTargetIdsAsync(ct).AsTask() : Task.CompletedTask),
        ("rowWindows", (sp, _, ct) => sp.GetService<IRowWindowColumnIndex>() is { } windows ? windows.WindowColumnsAsync(NoId, ct) : Task.CompletedTask),
        ("documentProject", (sp, _, ct) => sp.GetRequiredService<IAccessDecisionService>().DocumentProjectIdAsync(NoId, ct)),
        ("session", WarmSessionAsync),
        ("transaction", WarmTransactionAsync),
        ("jit", (_, _, ct) => PrepareJit(ct)),
    ];

    /// <summary>Один прохід прогріву. Не кидає нічого, крім скасування.</summary>
    /// <param name="versions">Версії шаблону, вже прогріті AN-190 (для оформлення версій); може бути порожнім.</param>
    /// <param name="ct">Скасування (зупинка хоста).</param>
    /// <returns>Підсумок проходу.</returns>
    /// <exception cref="OperationCanceledException">Прогрів скасовано.</exception>
    public async Task<FirstEditWarmResult> WarmAsync(IReadOnlyList<int> versions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(versions);

        var total = Stopwatch.StartNew();
        var done = new List<string>();
        var failed = new List<string>();
        foreach (var (name, run) in Steps)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var scope = scopes.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    await run(scope.ServiceProvider, versions, ct).ConfigureAwait(false);
                }

                done.Add(name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failed.Add(name);
                LogStepFailed(logger, name, ex.GetType().Name);
            }
        }

        var result = new FirstEditWarmResult(done, failed, total.ElapsedMilliseconds);
        LogWarmed(logger, done.Count, failed.Count, result.ElapsedMs);
        return result;
    }

    private static async Task WarmRowsAsync(IServiceProvider sp, IReadOnlyList<int> versions, CancellationToken ct)
    {
        var rows = sp.GetRequiredService<IRowStore>();

        // ⚠ Відсутній екземпляр — штатна відмова «не знайдено» (після запиту до живої таблиці й до архіву).
        try
        {
            _ = await rows.ResolveTableInstancesAsync([NoId], ct).ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            // Очікувано: запити скомпільовано й виконано.
        }

        _ = await rows.GetRowIdsAsync(NoId, NoPeriod, ct).ConfigureAwait(false);
        _ = await rows.GetRowVersionsAsync(NoId, NoPeriod, ct).ConfigureAwait(false);
        _ = await rows.GetRowsAsync(NoId, NoPeriod, ct).ConfigureAwait(false);
        _ = await rows.GetRowsBatchAsync([NoId], NoPeriod, ct).ConfigureAwait(false);
        _ = await rows.GetOrphanFlagsAsync(NoId, NoPeriod, ct).ConfigureAwait(false);
    }

    private static async Task WarmCellsAsync(IServiceProvider sp, IReadOnlyList<int> versions, CancellationToken ct)
    {
        var cells = sp.GetRequiredService<ICellStore>();
        _ = await cells.ReadSliceAsync(NoId, NoPeriod, ct).ConfigureAwait(false);
        _ = await cells.ReadSlicesAsync([NoId], NoPeriod, ct).ConfigureAwait(false);
        _ = await cells.ReadCellsAsync([new CellAddress(NoPeriod, NoId, NoId)], ct).ConfigureAwait(false);
    }

    private static async Task WarmPeriodsAsync(IServiceProvider sp, IReadOnlyList<int> versions, CancellationToken ct)
    {
        var periods = sp.GetRequiredService<IPeriodStore>();
        _ = await periods.FindPeriodBoundsAsync(NoId, NoPeriod.Value, ct).ConfigureAwait(false);
        _ = await periods.FindPeriodStateAsync(NoId, NoPeriod.Value, ct).ConfigureAwait(false);
        _ = await periods.HasReopenedSheetAsync(NoId, [NoId], NoPeriod.Value, ct).ConfigureAwait(false);
    }

    private static async Task WarmVersionStylesAsync(IServiceProvider sp, IReadOnlyList<int> versions, CancellationToken ct)
    {
        var styles = sp.GetRequiredService<IStyleCatalog>();
        var formats = sp.GetService<IConditionalFormatStore>();

        // Без прогрітих версій — один «порожняк», щоб скомпілювати запити.
        foreach (var versionId in versions.Count > 0 ? versions : [NoId])
        {
            ct.ThrowIfCancellationRequested();
            _ = await styles.GetAsync(versionId, ct).ConfigureAwait(false);
            if (formats is not null)
            {
                _ = await formats.GetAsync(versionId, ct, tableCode: string.Empty).ConfigureAwait(false);
            }
        }
    }

    private static async Task WarmOverlaysAsync(IServiceProvider sp, IReadOnlyList<int> versions, CancellationToken ct)
    {
        if (sp.GetService<ICellStyleStore>() is { } cellStyles)
        {
            _ = await cellStyles.ReadInstanceAsync(NoId, NoPeriod, ct).ConfigureAwait(false);
        }

        if (sp.GetService<IFormulaOverrideStore>() is { } overrides)
        {
            _ = await overrides.ReadInstanceAsync(NoId, NoPeriod, ct).ConfigureAwait(false);
        }

        if (sp.GetService<IFormulaErrorStore>() is { } errors)
        {
            _ = await errors.ReadInstancesAsync([NoId], NoPeriod, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Перевірки сеансу на першому запиті з кукою: тайм-аут простою (системне налаштування, той самий кеш), штамп і
    /// профіль — облікового запису, якого немає (<see cref="NoId"/>): запит компілюється, профіль не будується (відмова
    /// «запису не існує» ДО кешу профілів), жодного рішення про справжнього користувача.
    /// </summary>
    private static async Task WarmSessionAsync(IServiceProvider sp, IReadOnlyList<int> versions, CancellationToken ct)
    {
        if (sp.GetService<IIdleTimeoutPolicy>() is { } idle)
        {
            _ = await idle.GetMinutesAsync(ct).ConfigureAwait(false);
        }

        if (sp.GetService<Security.SecurityStampValidator>() is { } stamps)
        {
            _ = await stamps.ReadCurrentAsync(NoId, ct).ConfigureAwait(false);
        }

        try
        {
            _ = await sp.GetRequiredService<IAccessDecisionService>().BuildProfileAsync(NoId, ct).ConfigureAwait(false);
        }
        catch (AccessDeniedException)
        {
            // Очікувано: облікового запису немає.
        }
    }

    /// <summary>
    /// Порожня транзакція з табличним параметром комірок (<see cref="Persistence.NormalizedCellStore.WarmTableParameterAsync"/>):
    /// шлях транзакції EF і табличних параметрів SqlClient, яким іде запис <c>PATCH</c>, — без жодного рядка таблиці.
    /// </summary>
    private static Task WarmTransactionAsync(IServiceProvider sp, IReadOnlyList<int> versions, CancellationToken ct)
    {
        var db = sp.GetRequiredService<Persistence.EcrDbContext>();
        return sp.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(
            token => Persistence.NormalizedCellStore.WarmTableParameterAsync(db, token), ct);
    }

    /// <summary>
    /// JIT-компіляція методів <see cref="JitNamespaces"/> і <see cref="JitTypes"/> (разом із вкладеними типами —
    /// автоматами станів <c>async</c>), щоб перший запит не платив за компіляцію коду, якого до нього ніхто не виконував.
    /// </summary>
    /// <returns>Скільки методів підготовлено.</returns>
    internal static Task<int> PrepareJit(CancellationToken ct)
    {
        var prepared = 0;
        foreach (var type in JitTargets())
        {
            prepared += PrepareType(type, ct);
        }

        return Task.FromResult(prepared);
    }

    /// <summary>Верхньорівневі типи, які готує <see cref="PrepareJit"/> (вкладені — разом із ними).</summary>
    internal static IReadOnlyCollection<Type> JitTargets()
    {
        var types = new HashSet<Type>(JitTypes);
        foreach (var assembly in new[] { typeof(GetTableSliceHandler).Assembly, typeof(FirstEditWarmer).Assembly })
        {
            foreach (var type in assembly.GetTypes())
            {
                if (!type.IsNested && type.Namespace is { } ns
                    && JitNamespaces.Any(p => ns == p || ns.StartsWith(p + ".", StringComparison.Ordinal)))
                {
                    types.Add(type);
                }
            }
        }

        return types;
    }

    private static int PrepareType(Type type, CancellationToken ct)
    {
        if (type.ContainsGenericParameters)
        {
            return 0;
        }

        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                                 | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var prepared = 0;
        foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            ct.ThrowIfCancellationRequested();
            if (method.IsAbstract || method.ContainsGenericParameters)
            {
                continue;
            }

            try
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
                prepared++;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or BadImageFormatException)
            {
                // Метод, який рантайм не готує заздалегідь, — його скомпілює перший виклик, як і без прогріву.
            }
        }

        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            prepared += PrepareType(nested, ct);
        }

        return prepared;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Прогрів першої правки: кроків {Done}, збоїв {Failed}, усього {ElapsedMs} мс.")]
    private static partial void LogWarmed(ILogger logger, int done, int failed, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Прогрів першої правки: крок {Step} не вдався ({ExceptionType}); перший запит виконає його сам.")]
    private static partial void LogStepFailed(ILogger logger, string step, string exceptionType);
}

/// <summary>Підсумок проходу <see cref="FirstEditWarmer.WarmAsync"/>.</summary>
/// <param name="Done">Кроки, що пройшли.</param>
/// <param name="Failed">Кроки, що не вдалися.</param>
/// <param name="ElapsedMs">Тривалість усього проходу.</param>
public sealed record FirstEditWarmResult(IReadOnlyList<string> Done, IReadOnlyList<string> Failed, long ElapsedMs);
