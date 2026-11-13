using Ecr.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Ecr.Application.Documents.VersionMigration;

/// <summary>
/// V-5: стабільні вʼюхи SSRS після переносу, що зберіг нові аліаси колонок (<c>cfg.ColumnRename</c>): аліаси
/// вшиваються в текст вʼюхи при генерації. Після коміту, «найкращим зусиллям» (як у публікації).
/// </summary>
/// <remarks>
/// ⛔ Викликається ПІСЛЯ коміту переносу: виняток тут дав би клієнтові 500 при вже комітнутому переносі — тому збій
/// лише в журнал (і на <c>/health/ready</c> через сам генератор); повтор — <c>EXEC rpt.usp_GenerateStableViews</c> або
/// перезапуск застосунку.
/// </remarks>
public static partial class MigrationReportViews
{
    /// <summary>Перегенеровує вʼюхи шаблону цільової версії, коли перенос зберіг пари перейменувань; ніколи не кидає.</summary>
    /// <param name="views">Генератор вʼюх; <c>null</c> (прямий конструктор у тестах) — нічого не робить.</param>
    /// <param name="log">Журнал.</param>
    /// <param name="targetVersionId">Цільова версія переносу.</param>
    /// <param name="columnRenames">Скільки пар перейменувань колонок зберіг перенос.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task RefreshAsync(
        IReportViewGenerator? views, ILogger log, int targetVersionId, int columnRenames, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (views is null || columnRenames == 0)
        {
            return;
        }

        try
        {
            await views.GenerateAsync(targetVersionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            LogFailed(log, ex, targetVersionId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Перенос версії: вʼюхи rpt.* шаблону версії {TemplateVersionId} не перегенеровано після нових аліасів cfg.ColumnRename; повтор — EXEC rpt.usp_GenerateStableViews або перезапуск застосунку.")]
    private static partial void LogFailed(ILogger log, Exception ex, int templateVersionId);
}
