using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Архівація закритого року.
/// </summary>
/// <remarks>
/// ⚠ Сам DDL і <c>TRUNCATE … WITH (PARTITIONS)</c> виконує **збережена
/// процедура під окремим principal** (`D-66`): обліковий запис застосунку не
/// має ані DDL-прав, ані права запису в <c>arc.*</c>. Ця задача лише **викликає**
/// процедуру, стежить за прогресом і алертить.
/// </remarks>
public sealed class ArchiveJob(EcrDbContext db, ISqlCapabilities capabilities) : IBackgroundJob
{
    /// <inheritdoc />
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) перевірити, що проєкт у Closed і сплив YearGraceOffsetDays;\n" +
            "2) визначити діапазон PeriodKey року за PeriodKind (не жорстко YYYY01..YYYY12: " +
            "   для квартальних це YYYY01..YYYY04, R-A6);\n" +
            "3) EXEC arc.usp_ArchiveYear із розміром батча capabilities.ArchiveBatchSize;\n" +
            "4) стежити за itg.ArchiveRun і повідомляти прогрес;\n" +
            "5) при Failed — алерт і ЗУПИНКА: дані джерела на місці, повторний запуск " +
            "   продовжить із LastDonePeriodKey (АРХ-3a).\n" +
            "Повна зупинка системи не потрібна; потрібне вікно низької активності.");
}
