// src/Ecr.Infrastructure/Persistence/DocumentCalcStatusStore.cs
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>✎ D-577 (L3): реалізація <see cref="IDocumentCalcStatusStore"/> — лише вузькі читання за наявними індексами.</summary>
/// <remarks>
/// <list type="bullet">
///   <item>застарілі місяці — той самий текст предиката, що картка (<see cref="StaleResultsByIdsQuery"/>, форма для набору),
///   ОДНИМ об'єднанням по місяцях (як <c>DocumentStore.StaleResultsYearCardAsync</c>), але з місяцем у рядку;</item>
///   <item>задачі — фільтрований <c>IX_JobProgress_Document_Active</c> (живі рядки з документом — розміру черги);</item>
///   <item>актуальні прогони — фільтрований індекс <c>(ProjectId, PeriodKey, DocumentId) WHERE Status = 'Current'</c>;</item>
///   <item>провал — <c>IX_JobProgress_State_UpdatedAt</c> діапазоном <c>Failed</c> за добу.</item>
/// </list>
/// </remarks>
public sealed class DocumentCalcStatusStore(EcrDbContext db) : IDocumentCalcStatusStore
{
    /// <summary>Стеля задач документа у відповіді (живих задач документа — одиниці; стеля — запобіжник).</summary>
    private const int MaxActive = 20;

    private const string Queued = "Queued";
    private const string Running = "Running";
    private const string Failed = "Failed";

    /// <inheritdoc />
    public async Task<IReadOnlyList<StaleMonthFact>> StaleMonthsAsync(
        long documentId, int projectId, int periodKey, int? year, CancellationToken ct)
    {
        int[] keys = year is { } y
            ? await StaleResultsYear.PeriodKeysAsync(db, y, projectId, ct).ConfigureAwait(false)
            : [periodKey];
        if (keys.Length == 0)
        {
            return [];
        }

        var rows = await keys
            .Select(k => StaleResultsByIdsQuery.Documents(db, k, [documentId]).Select(r => new { Period = k, r.Since }))
            .Aggregate(Queryable.Concat)
            .OrderBy(r => r.Period)
            .Take(keys.Length)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Kind::Unspecified з datetime2 — журнал пише UTC (L3-01).
        return [.. rows.Select(r => new StaleMonthFact(r.Period, r.Since is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ActiveRecalculationFact>> ActiveAsync(long documentId, CancellationToken ct)
    {
        var code = Application.Ports.JobListKinds.RecalculationJobCode;
        var rows = await db.JobProgresses
            .AsNoTracking()
            .Where(p => p.DocumentId == documentId && (p.State == Queued || p.State == Running) && p.JobCode == code)
            .OrderBy(p => p.CreatedAt)
            .Select(p => new
            {
                p.JobId, p.State, p.Percent, p.TargetKey, p.CreatedByUserId, p.CreatedAt, p.StartedAt, p.Payload,
                Name = db.Users.Where(u => u.Id == p.CreatedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
            })
            .Take(MaxActive)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(r =>
            {
                var (done, total, current) = ChainOf(r.Payload);
                return new ActiveRecalculationFact(
                    r.JobId, r.State, r.Percent, r.TargetKey, r.CreatedByUserId, r.Name, r.CreatedAt,
                    r.State == Running ? r.StartedAt : null, done, total, current);
            }),
        ];
    }

    /// <inheritdoc />
    public async Task<DateTime?> CalculatedAtAsync(
        long documentId, int projectId, IReadOnlyCollection<int> periodKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(periodKeys);
        var keys = periodKeys.Select(k => (int?)k).ToList();

        // Власний прогін документа і прогін усього проєкту (D-557: він пише числа документів свого проєкту).
        return await db.CalculationRuns
            .AsNoTracking()
            .Where(r => r.ProjectId == projectId && keys.Contains(r.PeriodKey) && r.Status == CalculationRun.CurrentStatus
                        && (r.DocumentId == documentId || r.DocumentId == null))
            .MaxAsync(r => r.FinishedAt, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RecalculationFailureFact?> LastFailureAsync(long documentId, DateTime sinceUtc, CancellationToken ct)
    {
        var code = Application.Ports.JobListKinds.RecalculationJobCode;
        var row = await db.JobProgresses
            .AsNoTracking()
            .Where(p => p.State == Failed && p.UpdatedAt > sinceUtc && p.DocumentId == documentId && p.JobCode == code)
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new { p.JobId, p.UpdatedAt, p.ErrorCode, p.CorrelationId, p.TargetKey, p.CreatedByUserId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return row is null
            ? null
            : new RecalculationFailureFact(
                row.JobId, DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc), row.ErrorCode, row.CorrelationId,
                row.TargetKey, row.CreatedByUserId);
    }

    /// <summary>
    /// Шкала ланцюжка року з payload кроку (черга в базі, D-561/D-564): зроблені місяці, разом, місяць кроку. Quartz (payload
    /// немає) чи не ланцюжок — порожньо; період задачі — <c>periodKey</c> payload.
    /// </summary>
    internal static (int? Done, int? Total, int? Current) ChainOf(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return (null, null, null);
        }

        try
        {
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null, null);
            }

            int? done = Find(root, "chainDone") is { ValueKind: JsonValueKind.Array } d ? d.GetArrayLength() : null;
            int? total = Find(root, "chainTotal") is { ValueKind: JsonValueKind.Number } t ? t.GetInt32() : null;
            int? current = Find(root, "chainPlan") is { ValueKind: JsonValueKind.Array } plan && plan.GetArrayLength() > 0
                ? plan[0].GetInt32()
                : Find(root, "periodKey") is { ValueKind: JsonValueKind.Number } p ? p.GetInt32() : null;
            return (done ?? (total is null ? null : 0), total, current);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    /// <summary>Властивість без огляду на регістр (payload черги — camelCase, старі рядки — PascalCase).</summary>
    private static JsonElement? Find(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }
}
