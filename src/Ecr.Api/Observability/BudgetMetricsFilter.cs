using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Ecr.Api.Observability;

/// <summary>
/// Міряє тривалість дій, що входять у таблицю бюджету (<c>tz/08</c> §8.2).
/// </summary>
/// <remarks>
/// ⚠ Фільтр, а не виклик у кожному контролері. Метрика, яку треба не забути
/// дописати в новій дії, рано чи пізно не дописується — і бюджет перестає
/// вимірюватися саме там, куди щойно додали код. Фільтр міряє все, що
/// оголошене в <see cref="Routes"/>, і мовчить про решту.
/// <para>
/// Раніше <see cref="EcrMetrics"/> існував і не викликався **жодного разу**:
/// клас із метриками був, вимірювання не було. Це та сама «робота, якої ніхто
/// не робить», лише в спостережуваності.
/// </para>
/// </remarks>
public sealed class BudgetMetricsFilter(EcrMetrics metrics) : IAsyncActionFilter
{
    /// <summary>
    /// Дія контролера → метрика і операція бюджету.
    /// </summary>
    /// <remarks>
    /// Ключ — <c>Контролер.Дія</c>: маршрут змінюється презентаційно, а ім'я
    /// дії тримає сенс.
    /// </remarks>
    private static readonly Dictionary<string, (string Metric, string Operation)> Routes =
        new(StringComparer.Ordinal)
        {
            // ⛔ Ключі — РЕАЛЬНІ `Контролер.Дія` (аудит 2026-09-16, §9). Тут
            // стояли `Documents.Slice` і `Documents.PatchCells`, яких немає:
            // читання зрізу віддає `CellsController.GetSlice`, а пакетний
            // запис — `CellsController.Patch`. Тобто ДВІ найгарячіші метрики
            // бюджету (`cells_read`, `cells_write`) не спрацьовували ЖОДНОГО
            // разу — дефект невидимий за побудовою: жоден маршрут не «падає»,
            // просто не міряється.
            ["Cells.GetSlice"] = (EcrMetrics.CellsRead, "Читання зрізу"),
            ["Cells.Patch"] = (EcrMetrics.CellsWrite, "batch-PATCH"),
            ["Documents.Validate"] = (EcrMetrics.JobDuration, "Валідація документа"),
            ["Documents.Recalculate"] = (EcrMetrics.FormulaEvaluate, "Перерахунок піддерева"),
            ["Documents.Export"] = (EcrMetrics.JobDuration, "Експорт документа"),
            ["TemplateVersions.Publish"] = (EcrMetrics.JobDuration, "Публікація версії шаблону"),
            ["Documents.List"] = (EcrMetrics.CellsRead, "Відкриття адміністративного переліку"),
            ["Templates.List"] = (EcrMetrics.CellsRead, "Відкриття адміністративного переліку"),
            ["Projects.List"] = (EcrMetrics.CellsRead, "Відкриття адміністративного переліку"),
        };

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var key = $"{context.RouteData.Values["controller"]}.{context.RouteData.Values["action"]}";

        if (!Routes.TryGetValue(key, out var mapping))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var executed = await next().ConfigureAwait(false);
        stopwatch.Stop();

        // ⚠ Скасований запит НЕ міряємо: клієнт пішов, і його час нічого не
        // каже про бюджет — але роздує хвіст p95 рівно в пік, коли вкладки
        // закривають найчастіше.
        if (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        // ⚠ А от невдалі виклики міряємо. Відмова, що триває чотири секунди,
        // з'їдає бюджет так само, як успіх, — і саме її найлегше не помітити,
        // якщо рахувати лише успішні.
        _ = executed;

        // ⛔ Кількість беремо з того, хто її знає — з самої дії через
        // `HttpContext.Items` (аудит 2026-09-16, §9). До цього тут стояв
        // літеральний `0`, і вимір «комірок на запит» назавжди показував нуль:
        // графік бюджету стверджував, що система читає й пише рівно нуль
        // комірок. Дія, що кількості не повідомила, лишає `null` — тег просто
        // не з'являється, і відсутність видно як відсутність.
        Record(mapping, stopwatch.Elapsed, Count(context.HttpContext));
    }

    /// <summary>Кількість, яку дія поклала в <c>HttpContext.Items</c>.</summary>
    private static int? Count(HttpContext http)
        => http.Items.TryGetValue(EcrMetrics.CountItemKey, out var value) && value is int count
            ? count
            : null;

    /// <summary>Записує вимір у потрібну метрику.</summary>
    private void Record((string Metric, string Operation) mapping, TimeSpan elapsed, int? count)
    {
        switch (mapping.Metric)
        {
            case EcrMetrics.CellsRead:
                metrics.RecordCellsRead(elapsed.TotalMilliseconds, count);
                break;

            case EcrMetrics.CellsWrite:
                metrics.RecordCellsWrite(elapsed.TotalMilliseconds, count);
                break;

            case EcrMetrics.FormulaEvaluate:
                metrics.RecordFormulaEvaluate(elapsed.TotalMilliseconds, count);
                break;

            default:
                metrics.RecordDuration(mapping.Operation, elapsed.TotalSeconds);
                break;
        }
    }
}
