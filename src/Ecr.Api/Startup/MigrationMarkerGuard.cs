using System.Collections.Concurrent;
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Domain.Errors;
using Ecr.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Api.Startup;

/// <summary>
/// Проєкти, що зараз переносяться на іншу версію шаблону (маркер <c>migratingTo</c> у <c>doc.Project</c>).
/// </summary>
/// <remarks>
/// ⛔ Б1 (P1-1): перенос комірок іде діапазонами з окремими комітами, а під RCSI читач не блокується й бачив би
/// напівстан — частину комірок уже на колонках цільової версії. Тому читання документів і проєкту під час переносу
/// відмовляє 409 (<see cref="MigrationMarkerFilter"/>). Набір оновлює фонова служба раз на кілька секунд; на гарячому
/// шляху запиту до бази немає (порожній набір — одне читання змінної). Перенос чекає, поки маркер дійде до читачів,
/// ДО першого діапазону (<c>DocumentVersionMigrationStore.MarkerPropagationDelay</c>).
/// </remarks>
public sealed class MigratingProjects
{
    private volatile HashSet<int> projects = [];
    private readonly ConcurrentDictionary<long, int> documentProjects = new();

    /// <summary>Чи є хоч один проєкт у процесі переносу.</summary>
    public bool Any => projects.Count > 0;

    /// <summary>Чи переноситься проєкт.</summary>
    /// <param name="projectId">Проєкт.</param>
    public bool Contains(int projectId) => projects.Contains(projectId);

    /// <summary>Підміняє набір проєктів у процесі переносу.</summary>
    /// <param name="ids">Проєкти з маркером.</param>
    public void Replace(IEnumerable<int> ids) => projects = [.. ids];

    /// <summary>Проєкт документа (відображення незмінне, тому кешується; запит — лише поки є перенос).</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="db">Контекст запиту.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<int?> ProjectOfAsync(long documentId, EcrDbContext db, CancellationToken ct)
    {
        if (documentProjects.TryGetValue(documentId, out var cached))
        {
            return cached;
        }

        var found = await db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => (int?)d.ProjectId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (found is { } projectId)
        {
            documentProjects[documentId] = projectId;
        }

        return found;
    }
}

/// <summary>Фонове оновлення <see cref="MigratingProjects"/> з бази (раз на 2 с; збій лише в журнал).</summary>
public sealed partial class MigratingProjectsRefresher(
    MigratingProjects cache, IServiceScopeFactory scopes, ILogger<MigratingProjectsRefresher> logger) : BackgroundService
{
    /// <summary>Період оновлення.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Не вдалося оновити набір проєктів у процесі переносу версії.")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // фонова служба не падає через тимчасовий збій бази
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogRefreshFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Читає проєкти з маркером і підміняє набір.</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task RefreshAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EcrDbContext>();
        var ids = await db.Database
            .SqlQuery<int>($"SELECT Id AS Value FROM doc.Project WHERE ExternalSettingsJson LIKE '%\"migratingTo\"%'")
            .ToListAsync(ct)
            .ConfigureAwait(false);
        cache.Replace(ids);
    }
}

/// <summary>
/// Відмовляє 409 <c>ECR-DOC-4091</c> на будь-який запит до документа чи проєкту, що переноситься на іншу версію шаблону.
/// </summary>
/// <remarks>
/// Єдина точка для читань (сітка, шапка, експорт, перегляд) і записів; сам перенос (<c>…/migrate-version</c>) пропускається,
/// щоб обірваний перенос можна було повторити. Текст — наявний ключ <c>err.ECR-DOC-4091.structureChanging</c>.
/// </remarks>
public sealed class MigrationMarkerFilter(MigratingProjects projects) : IAsyncActionFilter
{
    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!projects.Any)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        if (path.Contains("migrate-version", StringComparison.OrdinalIgnoreCase))
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Ключі: `projectId`/`documentId` (контролери з префіксом ресурсу) і `id` у DocumentsController/ProjectsController
        // (маршрут `{id:long}`); крім того - query-параметри `projectId`/`documentId` (звіти, пошук, аудит комірок).
        var values = context.RouteData.Values;
        var controller = Convert.ToString(values.GetValueOrDefault("controller"), CultureInfo.InvariantCulture);
        var query = context.HttpContext.Request.Query;
        // P2-N1: окрім ключів маршруту - ПРЕФІКС шляху `/api/v1/documents/{n}/**` і `/api/v1/projects/{n}/**` (версії,
        // порівняння, історія процесу, recall та будь-який майбутній контролер під цими префіксами) і `projectId`/`documentId`
        // у ТІЛІ запиту (наприклад, `POST reports/{code}/build`) - з уже зв'язаних аргументів дії.
        var prefix = PathPrefix.Match(path);
        long? prefixProject = prefix.Success && prefix.Groups[1].Value == "projects" ? ParseId(prefix.Groups[2].Value) : null;
        long? prefixDocument = prefix.Success && prefix.Groups[1].Value == "documents" ? ParseId(prefix.Groups[2].Value) : null;
        var (bodyProject, bodyDocument) = FromArguments(context.ActionArguments);

        long? projectKey = ParseId(values.GetValueOrDefault("projectId"))
                           ?? prefixProject
                           ?? (string.Equals(controller, "Projects", StringComparison.Ordinal) ? ParseId(values.GetValueOrDefault("id")) : null)
                           ?? ParseId(query.TryGetValue("projectId", out var queryProject) ? queryProject.ToString() : null)
                           ?? bodyProject;
        long? documentKey = projectKey is null
            ? ParseId(values.GetValueOrDefault("documentId"))
              ?? prefixDocument
              ?? (string.Equals(controller, "Documents", StringComparison.Ordinal) ? ParseId(values.GetValueOrDefault("id")) : null)
              ?? ParseId(query.TryGetValue("documentId", out var queryDocument) ? queryDocument.ToString() : null)
              ?? bodyDocument
            : null;

        int? projectId = projectKey is { } p ? (int)p : null;
        string? documentLabel = null;
        if (documentKey is { } documentId)
        {
            documentLabel = documentId.ToString(CultureInfo.InvariantCulture);
            var db = context.HttpContext.RequestServices.GetRequiredService<EcrDbContext>();
            projectId = await projects.ProjectOfAsync(documentId, db, context.HttpContext.RequestAborted).ConfigureAwait(false);
        }
        if (projectId is { } id && projects.Contains(id))
        {
            var details = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "err.ECR-DOC-4091.structureChanging",
                ["projectId"] = id.ToString(CultureInfo.InvariantCulture),
            };
            if (documentLabel is not null)
            {
                details["documentId"] = documentLabel;
            }

            throw new ConcurrencyConflictException(
                ErrorCodes.SheetBusy,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Проєкт {id} переноситься на іншу версію шаблону: читання й запис тимчасово недоступні, повторіть після завершення."),
                details);
        }

        await next().ConfigureAwait(false);
    }

    private static readonly System.Text.RegularExpressions.Regex PathPrefix = new(
        @"^/api/v\d+/(documents|projects)/(\d+)(?:/|$)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(50));

    /// <summary>Властивості <c>ProjectId</c>/<c>DocumentId</c> у зв'язаних аргументах дії (тіло запиту).</summary>
    private static (long? Project, long? Document) FromArguments(IDictionary<string, object?> arguments)
    {
        long? project = null;
        long? document = null;
        foreach (var argument in arguments.Values)
        {
            if (argument is null || argument is string)
            {
                continue;
            }

            var type = argument.GetType();
            if (type.IsPrimitive || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
            {
                continue;
            }

            project ??= ParseId(type.GetProperty("ProjectId")?.GetValue(argument));
            document ??= ParseId(type.GetProperty("DocumentId")?.GetValue(argument));
        }

        return (project, document);
    }

    private static long? ParseId(object? raw)
        => long.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
