using Ecr.Application.Ports;

namespace Ecr.Infrastructure.Jobs;

/// <summary>Побудова зрізів звітності у фоні.</summary>
/// <remarks>
/// Побудова зрізу читає багато і довго — тому у фон із прогресом, а не
/// синхронно у відповіді API.
/// </remarks>
public sealed class ReportSnapshotJob(IReportSnapshotBuilder builder) : IReportSnapshotJob
{
    /// <summary>Код задачі в черзі.</summary>
    public static string Code => "report-snapshot";

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var request = SnapshotRequest.Parse(payload);

        // ⚠ Прогрес по кроках, а не стрибком 0 → 100. Смуга, що стоїть на
        // нулі й раптом стає повною, для користувача не відрізняється від
        // зависання — і він перезапускає задачу, яка працює.
        await progress.ReportAsync(10, "Читання даних", ct).ConfigureAwait(false);

        var snapshotId = await builder
            .BuildAsync(
                request.ReportVersionId,
                request.ProjectId,
                request.PeriodKey is { } key ? new Domain.ValueObjects.PeriodKey(key) : null,
                request.ParametersJson,
                ct)
            .ConfigureAwait(false);

        await progress.ReportAsync(100, $"Зріз {snapshotId} побудовано", ct).ConfigureAwait(false);
    }
}

/// <summary>Завдання на побудову зрізу.</summary>
/// <param name="ReportVersionId">Версія звіту.</param>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="PeriodKey">Період; <c>null</c> — увесь рік проєкту, а не «поточний».</param>
/// <param name="ParametersJson">Параметри побудови.</param>
public sealed record SnapshotRequest(
    int ReportVersionId, int ProjectId, int? PeriodKey, string? ParametersJson)
{
    /// <summary>Налаштування розбору; спільні на всі виклики.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions Options =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Розбирає завдання черги.</summary>
    /// <param name="payload">Завдання: типізоване або JSON.</param>
    public static SnapshotRequest Parse(object? payload)
    {
        if (payload is SnapshotRequest typed)
        {
            return typed;
        }

        var json = payload as string ?? System.Text.Json.JsonSerializer.Serialize(payload);

        return System.Text.Json.JsonSerializer.Deserialize<SnapshotRequest>(json, Options)
               ?? throw new InvalidOperationException(
                   "Завдання побудови зрізу не розбирається: невідома форма payload.");
    }
}
