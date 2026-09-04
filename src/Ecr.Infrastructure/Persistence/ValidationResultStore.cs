using Ecr.Application.Ports;
using Ecr.Domain.Entities.Workflow;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IValidationResultStore"/> над <c>wf.ValidationResult</c>.</summary>
public sealed class ValidationResultStore(EcrDbContext db) : IValidationResultStore
{
    /// <inheritdoc />
    public Task SaveAsync(ValidationSummary summary, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(summary);

        // Прогони НАКОПИЧУЮТЬСЯ, а не перезаписуються: історія перевірок
        // відповідає на питання «коли документ став валідним», а перезапис
        // лишив би тільки останню відповідь — саме ту, що вже нікого не
        // цікавить.
        db.ValidationResults.Add(new ValidationResult(
            summary.DocumentId,
            summary.PeriodKey,
            summary.RunAt,
            summary.ErrorCount,
            summary.WarningCount,
            summary.InfoCount,
            summary.MessagesJson));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<ValidationSummary?> GetLatestAsync(long documentId, int periodKey, CancellationToken ct)
        => await db.ValidationResults
            .AsNoTracking()
            .Where(v => v.DocumentId == documentId && v.PeriodKey == periodKey)
            .OrderByDescending(v => v.RunAt)
            .Select(v => new ValidationSummary(
                v.DocumentId, v.PeriodKey, v.RunAt,
                v.ErrorCount, v.WarningCount, v.InfoCount, v.MessagesJson))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
}
