using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ecr.Api.Health;

/// <summary>
/// Перевірка <c>reportviews</c>: чи створено вʼюхи <c>rpt.v_*</c> для SSRS по всіх
/// опублікованих версіях шаблонів (ФВ-10.2, ФВ-10.4).
/// </summary>
/// <remarks>
/// ⛔ Лише <c>Degraded</c>, ніколи <c>Unhealthy</c>: вʼюхи потрібні SSRS, а не
/// введенню даних, і 503 вивів би з ротації робочий сервер. Публікація версії з
/// таблицею &gt; 250 колонок (50422) чи зіткненням імен (50409) проходить, а причина
/// показується тут — з шаблоном, версією, аркушем і таблицею.
/// ⚠ Стан — <see cref="IReportViewStatus"/> у памʼяті: старт ставить фонову перегенерацію
/// по всіх шаблонах (✎ P-4b P3, <see cref="ReportViewsTarget.All"/>), тож після рестарту він відновлюється без таблиці
/// й міграції; що який успіх знімає — <see cref="IReportViewStatus"/>.
/// </remarks>
public sealed class ReportViewsHealthCheck(
    IReportViewStatus status,
    IUiStringCatalog catalog,
    ICurrentUser currentUser) : IHealthCheck
{
    /// <summary>Скільки символів причин потрапляє в опис картки.</summary>
    private const int MaxDetailsLength = 1500;

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        var failures = status.Snapshot();
        var pending = status.PendingTemplates();

        // ✎ P-4b (D-479): після публікації вʼюхи будуються у фоні — «оновлюються», а не «готово». Не Degraded: це нормальний
        // перехідний стан, і SSRS лише ще не бачить нової версії. Збій (нижче) важливіший за «оновлюються».
        if (failures.Count == 0 && pending.Count > 0)
        {
            var updating = await Text(
                "health.reportviews.pending",
                "Report views (rpt.v_*) are being updated in the background after a template publication "
                + "(templates: {templates}); SSRS sees the new version once this finishes.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["templates"] = string.Join(", ", pending.Select(id => id.ToString(CultureInfo.InvariantCulture))),
                },
                cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy(
                updating,
                new Dictionary<string, object>(StringComparer.Ordinal) { ["pendingTemplates"] = string.Join(", ", pending) });
        }

        if (failures.Count == 0)
        {
            var ok = await Text(
                "health.reportviews.ok", "Report views (rpt.v_*) are generated for all published template versions.",
                null, cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy(ok);
        }

        var details = string.Join(
            " | ",
            failures.OrderBy(f => f.TemplateVersionId ?? 0).Select(f => $"[{f.Code}] {f.Message}"));
        if (details.Length > MaxDetailsLength)
        {
            details = details[..MaxDetailsLength] + "…";
        }

        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["failedRuns"] = failures.Count,
            ["codes"] = string.Join(", ", failures.Select(f => f.Code).Distinct().Order()),
            ["templateVersionIds"] = string.Join(", ", failures.Where(f => f.TemplateVersionId is not null).Select(f => f.TemplateVersionId!.Value)),
        };

        var failed = await Text(
            "health.reportviews.failed",
            "Report views rpt.v_* were not created for some published template versions; SSRS does not see them: "
            + "{details}. Publication is not affected. Fix the template (at most 250 columns per table; unique "
            + "template/sheet/table codes) and restart the application or run EXEC rpt.usp_GenerateTemplateViews.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["count"] = failures.Count.ToString(CultureInfo.InvariantCulture),
                ["details"] = details,
            },
            cancellationToken).ConfigureAwait(false);
        return HealthCheckResult.Degraded(failed, data: data);
    }

    private Task<string> Text(
        string key, string fallback, IReadOnlyDictionary<string, string>? parameters, CancellationToken ct)
        => HealthCatalogText.ResolveAsync(catalog, currentUser, key, fallback, parameters, ct);
}
