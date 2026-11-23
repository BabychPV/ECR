// src/Ecr.Infrastructure/Jobs/OpenedPeriodRecalculation.cs
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// ✎ D-575: реалізація <see cref="IOpenedPeriodRecalculation"/> — <see cref="ICalculationTrigger.RequestAsync"/> (ціль
/// <c>doc{id}-p{період}-auto</c>, лише не затверджені аркуші, B83) для кожного документа проєкту з таблицями в періоді.
/// </summary>
/// <remarks>
/// ⚠ Без стелі кількості документів: обрізання мовчки лишило б частину місяця непорахованою (урок «стеля 5000»
/// прив'язок). Документів проєкту — одиниці-десятки, запит один на період.
/// </remarks>
public sealed partial class OpenedPeriodRecalculation(
    EcrDbContext db,
    ICalculationTrigger trigger,
    ILogger<OpenedPeriodRecalculation>? logger = null) : IOpenedPeriodRecalculation
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    /// <inheritdoc />
    public async Task RequestAsync(int projectId, IReadOnlyCollection<int> periodKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(periodKeys);
        if (periodKeys.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var key in periodKeys.Distinct().Order())
            {
                // DISTINCT DocumentId: перерахунок документа сам рахує оригінал і кожну копію аркуша (RS).
                var documentIds = await db.TableInstances
                    .AsNoTracking()
                    .Where(i => i.PeriodKeyValue == key
                                && db.Documents.Any(d => d.Id == i.DocumentId && d.ProjectId == projectId))
                    .Select(i => i.DocumentId)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                foreach (var documentId in documentIds)
                {
                    await trigger.RequestAsync(documentId, new PeriodKey(key), ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(_logger, projectId, ex);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "D-575: проєкт {ProjectId} — перерахунок відкритого періоду не поставлено; стани вже збережено, "
            + "формули місяця порахує нічний розклад або перша правка.")]
    private static partial void LogFailed(ILogger logger, int projectId, Exception exception);
}
