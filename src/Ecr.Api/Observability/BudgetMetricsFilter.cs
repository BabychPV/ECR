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
            ["Documents.Slice"] = (EcrMetrics.CellsRead, "Читання зрізу"),
            ["Documents.PatchCells"] = (EcrMetrics.CellsWrite, "batch-PATCH"),
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

        // ⚠ Міряємо і невдалі виклики теж. Відмова, що триває чотири секунди,
        // з'їдає бюджет так само, як успіх, — і саме її найлегше не помітити,
        // якщо рахувати лише успішні.
        _ = executed;

        Record(mapping, stopwatch.Elapsed);
    }

    /// <summary>Записує вимір у потрібну метрику.</summary>
    private void Record((string Metric, string Operation) mapping, TimeSpan elapsed)
    {
        switch (mapping.Metric)
        {
            case EcrMetrics.CellsRead:
                metrics.RecordCellsRead(elapsed.TotalMilliseconds, cellCount: 0);
                break;

            case EcrMetrics.CellsWrite:
                metrics.RecordCellsWrite(elapsed.TotalMilliseconds, cellCount: 0);
                break;

            case EcrMetrics.FormulaEvaluate:
                metrics.RecordFormulaEvaluate(elapsed.TotalMilliseconds, formulaCount: 0);
                break;

            default:
                metrics.RecordDuration(mapping.Operation, elapsed.TotalSeconds);
                break;
        }
    }
}
