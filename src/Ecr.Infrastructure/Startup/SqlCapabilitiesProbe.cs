using Ecr.Application.Ports;
using Ecr.Domain.Enums;

namespace Ecr.Infrastructure.Startup;

/// <summary>
/// Визначає можливості СУБД при старті (АРХ-7).
/// </summary>
/// <remarks>
/// Редакція впливає **лише на операційні стратегії**, ніколи — на модель даних,
/// семантику чи числа. Тому результат використовують тільки обслуговування
/// індексів, планувальник і <c>ArchiveJob</c>; у бізнес-коді звертатися сюди
/// заборонено.
/// </remarks>
public sealed class SqlCapabilitiesProbe : ISqlCapabilities
{
    /// <summary>Виконує запит і заповнює властивості.</summary>
    public Task ProbeAsync(string connectionString, SqlEditionMode configuredMode, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: SELECT SERVERPROPERTY('Edition'), ('EngineEdition'), ('ProductMajorVersion'), " +
            "DATABASEPROPERTYEX(DB_NAME(),'IsReadCommittedSnapshotOn').\n" +
            "EffectiveMode: configuredMode != Auto → configuredMode; інакше EngineEdition == 3 → " +
            "Enterprise, інакше Standard.\n" +
            "⚠ Developer/Evaluation повідомляють EngineEdition = 3 і виглядають як Enterprise — " +
            "тому в проді режим фіксують явно.\n" +
            "SupportsOnlineIndexRebuild і SupportsResourceGovernor = (EffectiveMode == Enterprise).\n" +
            "ArchiveBatchSize: Standard 500_000, Enterprise 2_000_000.");

    public SqlEditionMode EffectiveMode { get; private set; }
    public string EditionName { get; private set; } = string.Empty;
    public int ProductMajorVersion { get; private set; }
    public bool IsReadCommittedSnapshotOn { get; private set; }
    public bool SupportsOnlineIndexRebuild { get; private set; }
    public bool SupportsResourceGovernor { get; private set; }
    public int ArchiveBatchSize { get; private set; }
}
