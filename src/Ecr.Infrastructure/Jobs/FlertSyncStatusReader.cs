// src/Ecr.Infrastructure/Jobs/FlertSyncStatusReader.cs
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Стан синку FLERT із журналу обслуговування (<c>itg.MaintenanceRun</c>, код <see cref="FlertCompositionSyncJob.Code"/>):
/// останній прогін і підсумок із його <c>DetailsJson</c> (<see cref="FlertSyncOutcome.ToJson"/> або текст збою).
/// </summary>
/// <remarks>⚠ Відповідь мала (один рядок журналу, без переліку зауважень) — сторінка довідника читає її при кожному відкритті.</remarks>
public sealed class FlertSyncStatusReader(EcrDbContext db, IConfiguration configuration) : IFlertSyncStatusReader
{
    /// <inheritdoc />
    public async Task<FlertSyncStatusDto> ReadAsync(CancellationToken ct)
    {
        var options = FlertCompositionSyncOptions.Read(configuration);
        var codes = FlertRegistryCodes.Default;

        var run = await db.MaintenanceRuns
            .AsNoTracking()
            .Where(r => r.JobCode == FlertCompositionSyncJob.Code)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return new FlertSyncStatusDto(
            options.Enabled,
            [codes.Stream, codes.Case, codes.Composition],
            run is null ? null : Map(run.StartedAt, run.FinishedAt, run.Status, run.DetailsJson));
    }

    /// <summary>Рядок журналу → стан прогону; нерозбірний <c>DetailsJson</c> — лише час і статус.</summary>
    internal static FlertSyncRunDto Map(DateTime startedAt, DateTime? finishedAt, string status, string? detailsJson)
    {
        string? reason = null;
        string? message = null;
        int sourceRows = 0, added = 0, updated = 0, deactivated = 0, reactivated = 0, issueCount = 0;

        if (!string.IsNullOrWhiteSpace(detailsJson))
        {
            try
            {
                using var json = JsonDocument.Parse(detailsJson);
                var root = json.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    reason = Text(root, "reason");
                    sourceRows = Number(root, "sourceRows");
                    added = Sum(root, "added");
                    updated = Sum(root, "updated");
                    deactivated = Sum(root, "deactivated");
                    reactivated = Sum(root, "reactivated");
                    issueCount = Number(root, "issueCount");
                    message = Text(root, "error") ?? FirstError(root);
                }
            }
            catch (JsonException)
            {
                // Чужий формат подробиць — показуємо час і статус без підсумку.
            }
        }

        return new FlertSyncRunDto(
            DateTime.SpecifyKind(startedAt, DateTimeKind.Utc),
            finishedAt is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : null,
            status,
            reason,
            sourceRows,
            added,
            updated,
            deactivated,
            reactivated,
            issueCount,
            message);
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Number(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : 0;

    private static int Sum(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        var sum = 0;
        foreach (var item in value.EnumerateObject())
        {
            if (item.Value.ValueKind == JsonValueKind.Number && item.Value.TryGetInt32(out var n))
            {
                sum += n;
            }
        }

        return sum;
    }

    private static string? FirstError(JsonElement root)
    {
        if (!root.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var issue in issues.EnumerateArray())
        {
            if (issue.ValueKind == JsonValueKind.Object && Text(issue, "severity") == "Error")
            {
                return Text(issue, "detail");
            }
        }

        return null;
    }
}
