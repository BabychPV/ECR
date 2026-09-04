using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Перевірка узгодженості даних; знахідки пише в <c>aud.ConsistencyIssue</c>.
/// </summary>
/// <remarks>
/// ⚠ Задача **нічого не виправляє**. Автоматичне «полагодження» неузгодженості
/// приховало б її причину, а причина тут завжди важливіша за наслідок:
/// осиротілий рядок означає, що десь видалили запис довідника, на який
/// посилаються подані документи.
/// </remarks>
public sealed class ConsistencyCheckJob(EcrDbContext db) : IBackgroundJob
{
    /// <inheritdoc />
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: прогнати активні ext.ConsistencyRule; кожну знахідку записати в " +
            "aud.ConsistencyIssue із Severity правила. Нічого не виправляти. " +
            "Повторна знахідка тієї самої проблеми не має плодити дублікати — " +
            "зіставляти за (RuleCode, EntityType, EntityId).");
}
