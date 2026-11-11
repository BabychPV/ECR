using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// D-474: версії шаблону з розкладкою <see cref="TemplateLayout.YearWithMonthColumns"/> та їхні аркуші — у пам'яті
/// процесу (<see cref="ITemplateLayoutRegistry"/>).
/// </summary>
/// <remarks>
/// ⚠ Знімок підміняється цілком (одне присвоєння посилання), тож читач ніколи не бачить напівоновлений набір.
/// До першого <see cref="RefreshAsync"/> реєстр порожній — роутер поводиться як до D-474.
/// </remarks>
public sealed class TemplateLayoutRegistry(IServiceScopeFactory scopes) : ITemplateLayoutRegistry
{
    private volatile Snapshot _snapshot = Snapshot.Empty;

    /// <inheritdoc />
    public bool HasAny => _snapshot.Versions.Count > 0;

    /// <inheritdoc />
    public bool IsYearVersion(int templateVersionId) => _snapshot.Versions.Contains(templateVersionId);

    /// <inheritdoc />
    public bool IsYearSheet(int sheetDefId) => _snapshot.Sheets.Contains(sheetDefId);

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken ct)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<EcrDbContext>();
            var versions = await db.TemplateVersions.AsNoTracking()
                .Where(v => v.Layout == TemplateLayout.YearWithMonthColumns)
                .Select(v => v.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (versions.Count == 0)
            {
                _snapshot = Snapshot.Empty;
                return;
            }

            // ⚠ Разом із м'яко видаленими аркушами: таблиця видаленого аркуша нікуди не маршрутизується
            // (`AppliesToTable` відсікає видалені таблиці), а зайвий Id у наборі нічого не коштує.
            var sheets = await db.SheetDefs.AsNoTracking().IgnoreQueryFilters()
                .Where(s => versions.Contains(s.TemplateVersionId))
                .Select(s => s.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            _snapshot = new Snapshot([.. versions], [.. sheets]);
        }
    }

    private sealed record Snapshot(HashSet<int> Versions, HashSet<int> Sheets)
    {
        public static readonly Snapshot Empty = new([], []);
    }
}

/// <summary>
/// Наповнює <see cref="ITemplateLayoutRegistry"/> на старті процесу (до першого запиту) і перечитує його раз на
/// <see cref="Interval"/>: зміну розкладки в іншому процесі (Api ↔ Worker) цей процес бачить із такою затримкою.
/// </summary>
public sealed partial class TemplateLayoutRegistryRefresher(
    ITemplateLayoutRegistry registry, ILogger<TemplateLayoutRegistryRefresher> logger) : BackgroundService
{
    /// <summary>Період перечитування.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Не вдалося оновити реєстр розкладок версій шаблону.")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception);

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // ⛔ Перше читання — ДО прийому запитів: інакше запит до документа річної версії одразу після старту
        // пішов би старим (немаршрутизованим) шляхом.
        await TryRefreshAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await TryRefreshAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // зупинка процесу
        }
    }

    private async Task TryRefreshAsync(CancellationToken ct)
    {
        try
        {
            await registry.RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // фонова служба не падає через тимчасовий збій бази
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRefreshFailed(logger, ex);
        }
    }
}
